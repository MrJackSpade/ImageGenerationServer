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
    /// <para>This exists because unavailability used to be silent: a workflow whose files were not recognised
    /// simply did not appear, and there was no surface anywhere that said which slot was empty. Runs
    /// auto-matching first, so a fresh install has already recognised what it can before anyone looks.</para>
    /// </summary>
    Task<CatalogStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// The LoRA files present on this machine, for the composer's LoRA picker. When <paramref name="workflowId"/> is
    /// given, each entry is annotated with whether it will actually apply to that workflow's base model (and whether it
    /// affects CLIP); when null, compatibility is not evaluated and every entry is reported compatible. Throws when the
    /// renderer is unreachable (mapped to a 502).
    /// </summary>
    Task<IReadOnlyList<LoraCatalogEntry>> ListLorasAsync(string? workflowId, CancellationToken ct);

    /// <summary>Binds a file to a slot on this machine, or clears it when <paramref name="fileName"/> is blank.</summary>
    Task SetBindingAsync(string slotId, string? fileName, CancellationToken ct);

    /// <summary>Sets or clears one per-configuration override (VRAM, visibility, a parameter default).</summary>
    Task SetOverrideAsync(string configId, string settingKey, string? settingValue, CancellationToken ct);
}
