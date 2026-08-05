using ImageGen.Application.Workflows;
using ImageGen.Domain.Repositories;

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
        var machine = Environment.MachineName;
        var byKind = await _comfy.GetPresentFilesByKindAsync(ct);

        // Recognise what we can before reporting, so a fresh install has already bound the obvious things.
        var slots = _catalog.AllRequirements();
        var bindings = await _overrides.BindingsAsync(machine, ct);
        var matches = ModelMatcher.Match(
            slots.Where(s => !bindings.ContainsKey(s.Id))
                 .Select(s => new MatchableSlot(s.Id, s.Kind, s.Match)),
            byKind);

        var auto = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in matches)
            if (m.AutoBind is { } bind)
                auto[m.SlotId] = bind;
        if (auto.Count > 0)
        {
            await _overrides.AddAutoBindingsAsync(machine, auto, ct);
            bindings = await _overrides.BindingsAsync(machine, ct);
            _log.LogInformation("Recognised {Count} model file(s) automatically on {Machine}.", auto.Count, machine);
        }

        _catalog.SetBindings(bindings.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase));

        var candidatesBySlot = matches.ToDictionary(m => m.SlotId, m => m.Candidates, StringComparer.OrdinalIgnoreCase);
        var slotStatus = slots
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ModelSlotStatus(
                s.Id,
                s.Label,
                s.Kind.ToString(),
                bindings.TryGetValue(s.Id, out var b) ? b.FileName : null,
                b?.IsAuto ?? false,
                candidatesBySlot.TryGetValue(s.Id, out var c) ? c : [],
                byKind.TryGetValue(s.Kind, out var files) ? files : []))
            .ToList();

        // A requirement that names a node is a custom-node PACK, not a file: there is nothing to bind, and it is
        // met exactly when ComfyUI has that node registered. Asked separately because the file lists cannot answer
        // it — a node that loads nothing contributes no filenames to disappear when the pack does.
        var nodeRequirements = slots.Where(s => !string.IsNullOrWhiteSpace(s.Node)).ToList();
        var presentNodes = nodeRequirements.Count == 0
            ? (IReadOnlySet<string>)new HashSet<string>()
            : await _comfy.GetPresentNodesAsync(nodeRequirements.Select(s => s.Node).OfType<string>(), ct);

        // A bound file that ComfyUI no longer reports is as missing as one that was never bound — the weights were
        // moved or deleted, and saying "bound" would be a lie the picker then contradicts.
        bool Satisfied(string slotId)
        {
            if (_catalog.FindRequirement(slotId) is not { } r) return false;
            if (!string.IsNullOrWhiteSpace(r.Node)) return presentNodes.Contains(r.Node);

            return bindings.TryGetValue(slotId, out var bound)
                && byKind.TryGetValue(r.Kind, out var files)
                && files.Contains(bound.FileName, StringComparer.OrdinalIgnoreCase);
        }

        var workflows = new List<WorkflowStatus>();
        foreach (var cfg in _catalog.AllConfigs())
        {
            var wf = _registry.Find(cfg.WorkflowName);
            if (wf is null) continue;

            var required = cfg.Requirements.All().ToList();
            var missing = required.Where(id => !Satisfied(id)).ToList();

            workflows.Add(new WorkflowStatus(
                cfg.Id,
                cfg.FriendlyName ?? cfg.Id,
                Ready: missing.Count == 0,
                MissingSlots: missing,
                RequiredSlots: required));
        }

        return new CatalogStatus(
            workflows.OrderByDescending(w => w.Ready).ThenBy(w => w.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList(),
            slotStatus);
    }

    /// <inheritdoc/>
    public async Task SetBindingAsync(string slotId, string? fileName, CancellationToken ct)
    {
        var machine = Environment.MachineName;
        // isAuto: false — a person chose this, so auto-matching must never overwrite it later.
        await _overrides.SetBindingAsync(machine, slotId, fileName, isAuto: false, ct);
        var bindings = await _overrides.BindingsAsync(machine, ct);
        _catalog.SetBindings(bindings.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Refuse a render size the model does not document. Uses the envelope the CONFIGURATION declares — no bound is
    /// invented here, and a configuration that declares none is not second-guessed.
    /// </summary>
    private void GuardAspectAgainstEnvelope(string configId, string json)
    {
        var cfg = _catalog.FindConfig(configId);
        if (cfg?.Resolution is not { } env) return;
        var name = cfg.FriendlyName ?? cfg.Id;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var aspect in doc.RootElement.EnumerateObject())
        {
            if (aspect.Value.ValueKind != System.Text.Json.JsonValueKind.Array || aspect.Value.GetArrayLength() < 2) continue;
            int w = aspect.Value[0].GetInt32(), h = aspect.Value[1].GetInt32();

            if (w < env.MinW || w > env.MaxW || h < env.MinH || h > env.MaxH)
                throw new ArgumentException(
                    $"{aspect.Name} is {w}x{h}, outside what {name} supports "
                    + $"({env.MinW}-{env.MaxW} wide, {env.MinH}-{env.MaxH} tall).");

            if (env.Step > 0 && (w % env.Step != 0 || h % env.Step != 0))
                throw new ArgumentException(
                    $"{aspect.Name} is {w}x{h}; {name} needs both sides to be a multiple of {env.Step}.");
        }
    }

    /// <summary>The settings-page override key for a configuration's render-size aspect map.</summary>
    private const string AspectOverrideKey = "param.aspect";

    /// <inheritdoc/>
    public async Task SetOverrideAsync(string configId, string settingKey, string? settingValue, CancellationToken ct)
    {
        // A render size outside what the model documents is refused here, with the model's own numbers in the
        // message. The browser's min/max is advisory — this is the write path — and storing 4096 for a model whose
        // envelope stops at 1920 buys a failed render minutes later instead of an answer now.
        if (string.Equals(settingKey, AspectOverrideKey, StringComparison.OrdinalIgnoreCase)
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
