using System.Text.Json;

namespace ImageGen.Application.Workflows;

/// <summary>
/// The application's view of the workflow catalog: which configurations the current machine can offer, their
/// prompting guides, and the compact per-configuration info the render orchestrator needs. The ComfyUI-specific
/// loading, graph metadata, VRAM/requirement-presence eligibility, and shared-name de-duplication all live behind
/// this port in the Comfy adapter; the core sees only these business objects.
/// </summary>
public interface IWorkflowCatalog
{
    /// <summary>The compact orchestrator-facing info for a configuration id (friendly name, tagging rules, no-change
    /// gate opt-out), or null if the id is unknown.</summary>
    WorkflowInfo? ResolveInfo(string? configId);

    /// <summary>Every workflow configuration the current machine can run, after VRAM/requirement-presence
    /// eligibility and shared-friendly-name de-duplication, each annotated with its per-machine average runtime.
    /// Returns the same list to every caller — per-user hiding (UI picker and API) is applied above this port, by
    /// the picker client and the API endpoint respectively. Throws when the renderer is unreachable (mapped to a
    /// 502).</summary>
    Task<IReadOnlyList<WorkflowDescriptor>> ListEligibleAsync(CancellationToken ct);

    /// <summary>
    /// The editable settings for one configuration on this machine — the render size for each aspect, the step
    /// count, whatever else it sets — with the shipped value beside the overridden one. Null if the id is unknown.
    /// </summary>
    WorkflowSettings? GetSettings(string? configId);

    /// <summary>The prompting guide for one configuration id (resolved loosely, as generate accepts it), or null.</summary>
    PromptingGuide? GetGuide(string? configId);

    /// <summary>Every configuration's prompting guide.</summary>
    IReadOnlyList<PromptingGuide> AllGuides();

    /// <summary>
    /// Every workflow with the reason it is or is not available, and every model slot with what is bound to it.
    ///
    /// <para>This exists so unavailability is not silent: without it a workflow whose files are not recognised
    /// simply does not appear, and no surface anywhere says which slot is empty. Runs auto-matching first, so a
    /// fresh install has already recognised what it can before anyone looks.</para>
    /// </summary>
    Task<CatalogStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// Force an immediate re-probe of ComfyUI's capability state (present files, nodes, folder paths), then return the
    /// freshly-rebuilt status — the models page's manual Rescan, for changes the automatic triggers can't see instantly
    /// (a remote-ComfyUI file change, a node pack installed outside the app, or simply "look again now"). Does NOT touch
    /// the machine SQL sources; the relocated auto-bind pass re-runs off the file invalidation. Throws when the renderer
    /// is unreachable (mapped to a 502), exactly as <see cref="GetStatusAsync"/> does.
    /// </summary>
    Task<CatalogStatus> RescanAsync(CancellationToken ct);

    /// <summary>
    /// The LoRA files present on this machine, for the composer's LoRA picker. When <paramref name="workflowId"/> is
    /// given, each entry is annotated with whether it will actually apply to that workflow's base model (and whether it
    /// affects CLIP); when null, compatibility is not evaluated and every entry is reported compatible. The picker is
    /// offered only for a single selected model, so exactly one workflow is ever evaluated. Throws when the renderer is
    /// unreachable (mapped to a 502).
    /// </summary>
    Task<IReadOnlyList<LoraCatalogEntry>> ListLorasAsync(string? workflowId, CancellationToken ct);

    /// <summary>
    /// The file-backed model slots ONE configuration uses (union of its requirements and its model-ref params), each
    /// with its binding status and the other workflows that share it — for the model picker on the workflow's detail
    /// page. Null if the id is unknown. Node-pack / patch-install slots are omitted (they aren't files you point at;
    /// the library dialog keeps their install button). Throws when the renderer is unreachable (mapped to a 502).
    /// </summary>
    Task<IReadOnlyList<ConfigSlotStatus>?> GetConfigSlotsAsync(string configId, CancellationToken ct);

    /// <summary>Binds a file to a slot on this machine, or clears it when <paramref name="fileName"/> is blank.</summary>
    Task SetBindingAsync(string slotId, string? fileName, CancellationToken ct);

    /// <summary>Sets or clears one per-configuration override (VRAM, visibility, a parameter default).</summary>
    Task SetOverrideAsync(string configId, string settingKey, string? settingValue, CancellationToken ct);

    /// <summary>Duplicates a workflow into a new DB-backed variant on this machine — a coexisting, independently
    /// selectable catalogue entry snapshotting the base's current parameters. Returns the new variant's id. Throws
    /// <see cref="ArgumentException"/> for an unknown base or a blank name.</summary>
    Task<string> DuplicateWorkflowAsync(string baseConfigId, string friendlyName, CancellationToken ct);

    /// <summary>Removes a DB-backed variant (and its per-variant overrides) on this machine. Throws
    /// <see cref="ArgumentException"/> when the id is not a variant — a shipped file config cannot be deleted.</summary>
    Task DeleteVariantAsync(string variantId, CancellationToken ct);

    /// <summary>Validate a caller-supplied render size at submit. A request may carry an <paramref name="aspect"/> name
    /// OR an explicit width+height override, never both — both is ambiguous and refused (#209). Returns a human-readable
    /// refusal, or null when the request is unambiguous. A custom size the model can't render is NOT refused here (#212):
    /// the enqueue normalization pass snaps it to the nearest supported size and rides a notice on that slot.</summary>
    string? ValidateRequestedSize(string? configId, string? aspect, IReadOnlyDictionary<string, JsonElement>? overrides);

    /// <summary>The aspect label a generate submission is RECORDED under (#209): the shape an explicit width+height IS
    /// (by ratio), else the caller's aspect name taken as given, else — when neither is supplied — a fixed-size config's
    /// own declared dims, else square. Always returns a value, so the render path and history keep a non-null aspect
    /// exactly as when the composer submitted one.</summary>
    string ResolveEffectiveAspect(string? configId, string? aspect, IReadOnlyDictionary<string, JsonElement>? overrides);
}
