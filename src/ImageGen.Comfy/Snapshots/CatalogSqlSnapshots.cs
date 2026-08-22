using ImageGen.Application.Snapshots;
using ImageGen.Domain.Repositories;
using System.Text.Json;

namespace ImageGen.Comfy.Snapshots;

/// <summary>
/// This machine's model bindings, plus the recognition pass's per-slot candidate lists (#199). The candidates come from
/// the SAME rebuild the auto-bind pass ran against, so the models page's numbers agree with what recognition saw.
/// </summary>
public sealed class BindingsSnapshot(
    IReadOnlyDictionary<string, ModelBinding> bindings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>> overrides,
    IReadOnlyDictionary<string, IReadOnlyList<string>> candidates)
{
    /// <summary>Every model binding on this machine, keyed by slot id (after the recognition pass's auto-bindings).</summary>
    public IReadOnlyDictionary<string, ModelBinding> Bindings { get; } = bindings;

    /// <summary>Explicit workflow pins, keyed by config id then slot id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>> Overrides { get; } = overrides;

    /// <summary>slotId → the recognition pass's candidate files for that slot.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates { get; } = candidates;
}

/// <summary>This machine's per-configuration setting overrides, keyed by config id then setting key (#199).</summary>
public sealed class ParamOverridesSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrides)
{
    /// <summary>configId → (settingKey → value).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Overrides { get; } = overrides;
}

/// <summary>This machine's DB-backed workflow variants, parsed into specs the catalog folds in (#199).</summary>
public sealed class VariantsSnapshot(IReadOnlyList<VariantSpec> specs)
{
    /// <summary>The variant specs (base + snapshotted params) folded into the in-memory catalogue by the rebuild.</summary>
    public IReadOnlyList<VariantSpec> Specs { get; } = specs;
}

/// <summary>
/// The three machine-scoped SQL snapshots behind one injectable facade (#199): bindings (with recognition), param
/// overrides, and variants. The catalog service reads through it and flushes the matching source on each write.
/// </summary>
public sealed class CatalogSnapshots(
    ISnapshot<BindingsSnapshot> bindings,
    ISnapshot<ParamOverridesSnapshot> paramOverrides,
    ISnapshot<VariantsSnapshot> variants)
{
    /// <summary>Bindings + recognition candidates.</summary>
    public ISnapshot<BindingsSnapshot> Bindings { get; } = bindings;

    /// <summary>Per-configuration setting overrides.</summary>
    public ISnapshot<ParamOverridesSnapshot> ParamOverrides { get; } = paramOverrides;

    /// <summary>DB-backed variants.</summary>
    public ISnapshot<VariantsSnapshot> Variants { get; } = variants;
}

/// <summary>
/// The loaders for the three machine-scoped SQL snapshot sources (#199). Each rebuild pushes its result into the
/// in-memory <see cref="WorkflowCatalog"/> so the synchronous submit-path <c>Resolve()</c> works without a query — the
/// push that used to live in <c>ListEligibleAsync</c>/<c>GetStatusAsync</c> now happens here, guaranteed before the
/// first submit by the startup warm and re-guaranteed by flush-on-write.
///
/// <para>The bindings rebuild also carries the auto-bind recognition pass relocated out of <c>GetStatusAsync</c>: read
/// bindings → run the matcher against the current <see cref="ComfyFilesByKind"/> → if (and only if) it bound anything,
/// re-read → push → publish. A read that awaits the rebuild observes the completed recognition, and because it writes
/// only on an actual new match (and <c>AddAutoBindingsAsync</c> never overwrites), the chain converges.</para>
/// </summary>
public sealed class CatalogSqlSnapshotSources(
    WorkflowCatalog catalog,
    ICatalogOverrideRepository overrides,
    IWorkflowVariantRepository variants,
    ISnapshot<ComfyFilesByKind> files,
    ILogger<CatalogSqlSnapshotSources> log)
{
    private readonly WorkflowCatalog _catalog = catalog;
    private readonly ICatalogOverrideRepository _overrides = overrides;
    private readonly IWorkflowVariantRepository _variants = variants;
    private readonly ISnapshot<ComfyFilesByKind> _files = files;
    private readonly ILogger<CatalogSqlSnapshotSources> _log = log;

    private static string Machine => Environment.MachineName;

    /// <summary>Rebuild the bindings snapshot, running the relocated recognition pass and pushing into the catalog.</summary>
    public async Task<BindingsSnapshot> LoadBindingsAsync(CancellationToken ct)
    {
        string machine = Machine;
        // PEEK the files source, do NOT await it: this loader runs on the single sync worker, and awaiting another
        // source's rebuild here would deadlock (the worker can't run that rebuild while blocked in this one). The
        // files→bindings cascade fires from inside the files rebuild, so by the time this runs via the cascade, files
        // is Fresh and PeekCurrent returns the current sweep. If files last faulted (ComfyUI unreachable), PeekCurrent
        // rethrows that fault so bindings faults too — the intended degrade, exactly what a live GetStatus surfaced.
        ComfyFilesByKind present = _files.PeekCurrent();
        IReadOnlyDictionary<string, ModelBinding> bindings = await _overrides.BindingsAsync(machine, ct);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>> configBindings =
            await _overrides.BindingOverridesAsync(machine, ct);

        IReadOnlyList<Requirement> slots = _catalog.AllRequirements();
        IReadOnlyList<SlotMatch> matches = ModelMatcher.Match(
            slots.Where(s => !bindings.ContainsKey(s.Id)).Select(s => new MatchableSlot(s.Id, s.Kind, s.Match)),
            present.ByKind);

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
            bindings = await _overrides.BindingsAsync(machine, ct);   // re-read so the published value includes them
            _log.LogInformation("Recognised {Count} model file(s) automatically on {Machine}.", auto.Count, machine);
        }

        _catalog.SetBindings(bindings, configBindings);

        Dictionary<string, IReadOnlyList<string>> candidates =
            matches.ToDictionary(m => m.SlotId, m => m.Candidates, StringComparer.OrdinalIgnoreCase);
        return new BindingsSnapshot(bindings, configBindings, candidates);
    }

    /// <summary>Rebuild the param-overrides snapshot and push it into the catalog.</summary>
    public async Task<ParamOverridesSnapshot> LoadOverridesAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ov = await _overrides.OverridesAsync(Machine, ct);
        _catalog.SetParamOverrides(ov);
        return new ParamOverridesSnapshot(ov);
    }

    /// <summary>Rebuild the variants snapshot (parsing each stored param blob) and fold it into the catalog.</summary>
    public async Task<VariantsSnapshot> LoadVariantsAsync(CancellationToken ct)
    {
        IReadOnlyList<WorkflowVariant> rows = await _variants.VariantsAsync(Machine, ct);
        List<VariantSpec> specs = [.. rows.Select(v =>
            new VariantSpec(v.VariantId, v.BaseConfigId, v.FriendlyName, ParseSnapshot(v.VariantId, v.ParamsJson)))];
        _catalog.SetVariants(specs);
        return new VariantsSnapshot(specs);
    }

    /// <summary>Parse a variant's stored parameter snapshot; a corrupt blob yields an empty snapshot (the variant then
    /// renders the base's own values) rather than taking the whole rebuild down — an existing, deliberate containment.</summary>
    private Dictionary<string, JsonElement> ParseSnapshot(string variantId, string paramsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson) ?? [];
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Workflow variant '{Id}' has an unreadable parameter snapshot; using the base's values.", variantId);
            return [];
        }
    }
}
