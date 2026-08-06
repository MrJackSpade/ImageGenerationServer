using ImageGen.Application.Workflows;
using ImageGen.Domain.Repositories;
using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// The diagnostics half of the catalogue service: what this machine can run, what it cannot, and why.
///
/// <para>Without this, unavailability is silent — a workflow whose files are not recognised simply does not appear
/// in the picker, with no surface anywhere naming the empty slot, and the failure looks exactly like "this box
/// cannot afford that model".</para>
/// </summary>
public sealed partial class WorkflowCatalogService
{
    /// <inheritdoc/>
    public async Task<CatalogStatus> GetStatusAsync(CancellationToken ct)
    {
        string machine = Environment.MachineName;
        IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> byKind = await _comfy.GetPresentFilesByKindAsync(ct);

        // Recognise what we can before reporting, so a fresh install has already bound the obvious things.
        IReadOnlyList<Requirement> slots = _catalog.AllRequirements();
        IReadOnlyDictionary<string, ModelBinding> bindings = await _overrides.BindingsAsync(machine, ct);
        IReadOnlyList<SlotMatch> matches = ModelMatcher.Match(
            slots.Where(s => !bindings.ContainsKey(s.Id))
                 .Select(s => new MatchableSlot(s.Id, s.Kind, s.Match)),
            byKind);

        Dictionary<string, string> auto = new(StringComparer.OrdinalIgnoreCase);
        foreach (SlotMatch m in matches)
        {
            if (m.AutoBind is { } bind)
            {
                auto[m.SlotId] = bind;
            }
        }

        if (auto.Count > 0)
        {
            await _overrides.AddAutoBindingsAsync(machine, auto, ct);
            bindings = await _overrides.BindingsAsync(machine, ct);
            _log.LogInformation("Recognised {Count} model file(s) automatically on {Machine}.", auto.Count, machine);
        }

        _catalog.SetBindings(bindings.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase));

        Dictionary<string, IReadOnlyList<string>> candidatesBySlot = matches.ToDictionary(m => m.SlotId, m => m.Candidates, StringComparer.OrdinalIgnoreCase);
        List<ModelSlotStatus> slotStatus = [.. slots
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ModelSlotStatus(
                s.Id,
                s.Label,
                s.Kind.ToString(),
                bindings.TryGetValue(s.Id, out ModelBinding? b) ? b.FileName : null,
                b?.IsAuto ?? false,
                candidatesBySlot.TryGetValue(s.Id, out IReadOnlyList<string>? c) ? c : [],
                byKind.TryGetValue(s.Kind, out IReadOnlyList<string>? files) ? files : []))];

        // A requirement that names a node is a custom-node PACK, not a file: there is nothing to bind, and it is
        // met exactly when ComfyUI has that node registered. Asked separately because the file lists cannot answer
        // it — a node that loads nothing contributes no filenames to disappear when the pack does.
        List<Requirement> nodeRequirements = [.. slots.Where(s => !string.IsNullOrWhiteSpace(s.Node))];
        IReadOnlySet<string> presentNodes = nodeRequirements.Count == 0
            ? new HashSet<string>()
            : await _comfy.GetPresentNodesAsync(nodeRequirements.Select(s => s.Node).OfType<string>(), ct);

        // A bound file that ComfyUI no longer reports is as missing as one that was never bound — the weights were
        // moved or deleted, and saying "bound" would be a lie the picker then contradicts.
        bool Satisfied(string slotId)
        {
            if (_catalog.FindRequirement(slotId) is not { } r)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(r.Node))
            {
                return presentNodes.Contains(r.Node);
            }

            return bindings.TryGetValue(slotId, out ModelBinding? bound)
                && byKind.TryGetValue(r.Kind, out IReadOnlyList<string>? files)
                && files.Contains(bound.FileName, StringComparer.OrdinalIgnoreCase);
        }

        List<WorkflowStatus> workflows = [];
        foreach (WorkflowConfiguration cfg in _catalog.AllConfigs())
        {
            IWorkflow? wf = _registry.Find(cfg.WorkflowName);
            if (wf is null)
            {
                continue;
            }

            List<string> required = [.. cfg.Requirements.All()];
            List<string> missing = [.. required.Where(id => !Satisfied(id))];

            workflows.Add(new WorkflowStatus(
                cfg.Id,
                cfg.FriendlyName ?? cfg.Id,
                Ready: missing.Count == 0,
                MissingSlots: missing,
                RequiredSlots: required));
        }

        return new CatalogStatus(
            [.. workflows.OrderByDescending(w => w.Ready).ThenBy(w => w.FriendlyName, StringComparer.OrdinalIgnoreCase)],
            slotStatus);
    }

    /// <inheritdoc/>
    public async Task SetBindingAsync(string slotId, string? fileName, CancellationToken ct)
    {
        string machine = Environment.MachineName;
        // isAuto: false — a person chose this, so auto-matching must never overwrite it later.
        await _overrides.SetBindingAsync(machine, slotId, fileName, isAuto: false, ct);
        IReadOnlyDictionary<string, ModelBinding> bindings = await _overrides.BindingsAsync(machine, ct);
        _catalog.SetBindings(bindings.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Refuse a render size the model does not document. Uses the envelope the CONFIGURATION declares — no bound is
    /// invented here, and a configuration that declares none is not second-guessed.
    /// </summary>
    private void GuardAspectAgainstEnvelope(string configId, string json)
    {
        WorkflowConfiguration? cfg = _catalog.FindConfig(configId);
        if (cfg?.Resolution is not { } env)
        {
            return;
        }

        string name = cfg.FriendlyName ?? cfg.Id;

        using JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (JsonProperty aspect in doc.RootElement.EnumerateObject())
        {
            if (aspect.Value.ValueKind != System.Text.Json.JsonValueKind.Array || aspect.Value.GetArrayLength() < 2)
            {
                continue;
            }

            int w = aspect.Value[0].GetInt32(), h = aspect.Value[1].GetInt32();

            // Same envelope check the submit path runs (ResolutionGuard) — the write path just throws the type the
            // settings API maps to a 400 and names the model in the subject.
            if (ResolutionGuard.Violation(env, w, h, $"{aspect.Name} ({name})") is { } msg)
            {
                throw new ArgumentException(msg + ".");
            }
        }
    }

    /// <inheritdoc/>
    public string? ValidateRequestedSize(string? configId, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        // Only a request carrying BOTH an explicit width and height is a custom-size request — anything else uses the
        // configuration's aspect map (already envelope-checked on the write path), so there is nothing to validate.
        if (overrides is null
            || !overrides.TryGetValue(WorkflowParamKeys.Width, out JsonElement wEl) || !TryPixel(wEl, out int w)
            || !overrides.TryGetValue(WorkflowParamKeys.Height, out JsonElement hEl) || !TryPixel(hEl, out int h))
        {
            return null;
        }

        // The configuration's declared envelope — the same numbers the settings page shows and the write path guards —
        // checked through the ONE render-size guard, so submit and render refuse with identical wording. A configuration
        // that declares none is not second-guessed (the render path has no bound to enforce either).
        return ResolutionGuard.RenderSizeViolation(_catalog.FindConfig(configId)?.Resolution, w, h);
    }

    /// <summary>Read a pixel dimension from an override value — a JSON number, or a numeric string. Anything else is
    /// "not an explicit size" (returns false), left to normal binding rather than mistaken for a custom size.</summary>
    private static bool TryPixel(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
        {
            return true;
        }

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <inheritdoc/>
    public async Task SetOverrideAsync(string configId, string settingKey, string? settingValue, CancellationToken ct)
    {
        // A render size outside what the model documents is refused here, with the model's own numbers in the
        // message. The browser's min/max is advisory — this is the write path — and storing 4096 for a model whose
        // envelope stops at 1920 buys a failed render minutes later instead of an answer now.
        if (string.Equals(settingKey, SettingKeys.AspectOverride, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settingValue))
        {
            GuardAspectAgainstEnvelope(configId, settingValue);
        }

        await _overrides.SetOverrideAsync(Environment.MachineName, configId, settingKey, settingValue, ct);
        // Re-push immediately: the merge reads an in-memory snapshot, so without this the value would not apply
        // until something else reloaded the catalog.
        _catalog.SetParamOverrides(await _overrides.OverridesAsync(Environment.MachineName, ct));
    }
}