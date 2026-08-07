using ImageGen.Application.Workflows;
using ImageGen.Comfy.Snapshots;
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
        // Read-only projection over the snapshots (#201): no ComfyUI round trips, no SQL reads, no auto-bind write —
        // the recognition pass now runs inside the bindings rebuild (#199), so the candidates below come from the SAME
        // rebuild that recognition ran against and the page's numbers agree with what it saw.
        IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> byKind = (await _probes.FilesByKind.GetAsync(ct)).ByKind;
        BindingsSnapshot bindingsSnap = await _snapshots.Bindings.GetAsync(ct);
        IReadOnlyDictionary<string, ModelBinding> bindings = bindingsSnap.Bindings;
        // Fold in this machine's DB-backed variants so the status list includes them (their rebuild pushes into the catalog).
        _ = await _snapshots.Variants.GetAsync(ct);

        IReadOnlyList<Requirement> slots = _catalog.AllRequirements();
        Dictionary<string, IReadOnlyList<string>> candidatesBySlot =
            new(bindingsSnap.Candidates, StringComparer.OrdinalIgnoreCase);
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
        // met exactly when ComfyUI has that node registered. The present-nodes snapshot is probed over ALL declared
        // node requirements, so a Contains check here answers the same question the live probe did.
        IReadOnlySet<string> presentNodes = (await _probes.PresentNodes.GetAsync(ct)).Nodes;

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

            // BOTH halves of the configuration's model list: the requirements block AND the model refs its params
            // set (an optional LoRA, a second MoE expert) — a params ref is every bit as necessary, and gating on
            // requirements alone shows a config as ready whose params slot is unbound, so the picker offers it and
            // the render path then refuses it (the H3 Turbo LoRA regression).
            List<string> required = [.. cfg.Requirements.All()
                .Concat(_catalog.ModelRefSlots(wf, cfg))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            List<string> missing = [.. required.Where(id => !Satisfied(id))];

            workflows.Add(new WorkflowStatus(
                cfg.Id,
                cfg.FriendlyName ?? cfg.Id,
                Ready: missing.Count == 0,
                MissingSlots: missing,
                RequiredSlots: required,
                // The per-config resolved kind, so a disabled workflow badges its specific kind (#162/#163) — known
                // here because it reads the registered class and the config, not a bound slot file.
                Kind: KindToken(ResolveKind(cfg, wf))));
        }

        return new CatalogStatus(
            [.. workflows.OrderByDescending(w => w.Ready).ThenBy(w => w.FriendlyName, StringComparer.OrdinalIgnoreCase)],
            slotStatus);
    }

    /// <inheritdoc/>
    public async Task<CatalogStatus> RescanAsync(CancellationToken ct)
    {
        // Flush the three Comfy probes, then read status — GetStatusAsync awaits the rebuilt snapshots, and because the
        // files invalidation cascades to the bindings source, the recognition/auto-bind pass has re-run against the new
        // files by the time this returns. Concurrent rescans coalesce on the sync worker into one rebuild.
        _probes.InvalidateAll();
        return await GetStatusAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConfigSlotStatus>?> GetConfigSlotsAsync(string configId, CancellationToken ct)
    {
        if (_catalog.FindConfig(configId) is null)
        {
            return null;
        }

        // The full picture is the authority on both halves of what this needs: every slot's binding status, and — via
        // each workflow's RequiredSlots — which OTHER workflows share a slot. Reusing it keeps the required-slots union
        // computed in exactly one place (GetStatusAsync) rather than re-derived here.
        CatalogStatus status = await GetStatusAsync(ct);
        WorkflowStatus? me = status.Workflows.FirstOrDefault(
            w => string.Equals(w.Id, configId, StringComparison.OrdinalIgnoreCase));
        if (me is null)
        {
            return null;
        }

        Dictionary<string, ModelSlotStatus> byId = status.Slots.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        List<ConfigSlotStatus> slots = [];
        foreach (string slotId in me.RequiredSlots)
        {
            // Node-pack / patch-install slots aren't a file you point at — the library dialog installs them. Out of
            // scope for the detail-page picker (issue #195), so they're left out rather than shown as an empty dropdown.
            if (_catalog.FindRequirement(slotId) is { Node: { Length: > 0 } })
            {
                continue;
            }

            IReadOnlyList<string> sharedWith = SlotSharing.Others(status.Workflows, configId, slotId);

            slots.Add(byId.TryGetValue(slotId, out ModelSlotStatus? s)
                ? new ConfigSlotStatus(s.Id, s.Label, s.Kind, s.BoundFile, s.IsAuto, s.Candidates, s.Available, sharedWith)
                : new ConfigSlotStatus(slotId, slotId, string.Empty, null, false, [], [], sharedWith));
        }

        return slots;
    }

    /// <inheritdoc/>
    public async Task SetBindingAsync(string slotId, string? fileName, CancellationToken ct)
    {
        // isAuto: false — a person chose this, so auto-matching must never overwrite it later.
        await _overrides.SetBindingAsync(Environment.MachineName, slotId, fileName, isAuto: false, ct);
        // Flush the bindings snapshot and await its rebuild, which re-reads and pushes into the in-memory catalog.
        // Block-until-rebuilt gives the write its old synchronous behavior without the duplicated inline re-read/push.
        _snapshots.Bindings.Invalidate();
        _ = await _snapshots.Bindings.GetAsync(ct);
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
        // Flush the overrides snapshot and await its rebuild, which re-reads and pushes into the in-memory catalog so
        // the merge sees the new value immediately (replaces the old inline re-push).
        _snapshots.ParamOverrides.Invalidate();
        _ = await _snapshots.ParamOverrides.GetAsync(ct);
    }
}