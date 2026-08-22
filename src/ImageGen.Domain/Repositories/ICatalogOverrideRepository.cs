namespace ImageGen.Domain.Repositories;

/// <summary>
/// Which file on this machine fills a catalogue slot.
/// </summary>
/// <param name="SlotId">The slot, as named by <c>configurations/models/&lt;id&gt;.json</c>.</param>
/// <param name="FileName">The filename exactly as ComfyUI reports it.</param>
/// <param name="IsAuto">
/// True when a <c>match</c> pattern chose this rather than a person. The distinction is load-bearing: an
/// automatic binding may be re-evaluated when the catalogue's patterns improve, a hand-picked one never is.
/// </param>
public sealed record ModelBinding(string SlotId, string FileName, bool IsAuto);

/// <summary>An explicit per-workflow model pin. Unlike a shared <see cref="ModelBinding"/>, its presence records
/// durable user intent even when <see cref="FileName"/> currently equals the shared binding.</summary>
public sealed record ConfigModelBindingOverride(
    string ConfigId, string SlotId, string FileName, DateTime UpdatedAtUtc);

/// <summary>The atomic outcome of selecting a model from a workflow-scoped picker.</summary>
public enum WorkflowBindingResult
{
    /// <summary>The slot had no shared binding, so this selection established it and removed this workflow's old pin.</summary>
    SharedCreated,

    /// <summary>A shared binding already existed, so this selection created or replaced the workflow's explicit pin.</summary>
    WorkflowPinned,
}

/// <summary>
/// One per-configuration setting overridden on this machine.
/// </summary>
/// <param name="ConfigId">The workflow configuration.</param>
/// <param name="SettingKey">
/// Namespaced setting key, normally <c>param.&lt;key&gt;</c> for a workflow parameter's default.
/// </param>
/// <param name="SettingValue">The raw text, coerced through the parameter's existing type when it is read.</param>
public sealed record CatalogOverride(string ConfigId, string SettingKey, string SettingValue);

/// <summary>
/// The install-wide catalogue overrides for a machine: model bindings and per-configuration settings.
///
/// <para>Keyed by machine name rather than by user, because these describe the BOX — which file is on its disk and
/// which configuration defaults it uses — not somebody's preference. The shipped catalogue is immutable; everything a user
/// can legitimately change about it lives here.</para>
/// </summary>
public interface ICatalogOverrideRepository
{
    /// <summary>Every model binding on this machine, keyed by slot id.</summary>
    Task<IReadOnlyDictionary<string, ModelBinding>> BindingsAsync(string machineName, CancellationToken ct);

    /// <summary>
    /// Sets a slot's binding, replacing any existing one. <paramref name="fileName"/> null or blank CLEARS it,
    /// which is how a user rejects a wrong automatic guess.
    /// </summary>
    Task SetBindingAsync(string machineName, string slotId, string? fileName, bool isAuto, CancellationToken ct);

    /// <summary>
    /// Records automatic bindings for slots that have none, in one round trip. Never touches a slot that is
    /// already bound — a hand-picked binding, or an automatic one the user has since corrected, must survive
    /// every subsequent load.
    /// </summary>
    Task AddAutoBindingsAsync(string machineName, IReadOnlyDictionary<string, string> slotToFile, CancellationToken ct);

    /// <summary>Every explicit workflow model pin on this machine, keyed by config id then slot id.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>>> BindingOverridesAsync(
        string machineName, CancellationToken ct);

    /// <summary>
    /// Atomically applies a workflow-scoped selection. If the slot has no shared binding, creates that shared manual
    /// binding and removes this workflow's pin. Otherwise creates or replaces the explicit pin, including when the
    /// selected filename equals the shared filename.
    /// </summary>
    Task<WorkflowBindingResult> SetConfigBindingAsync(
        string machineName, string configId, string slotId, string fileName, CancellationToken ct);

    /// <summary>Removes exactly one explicit pin so the workflow resumes inheriting the shared binding.</summary>
    Task ClearConfigBindingAsync(string machineName, string configId, string slotId, CancellationToken ct);

    /// <summary>Copies all explicit pins from one configuration to another. Inherited slots have no rows to copy.</summary>
    Task CopyConfigBindingsAsync(string machineName, string sourceConfigId, string targetConfigId, CancellationToken ct);

    /// <summary>Removes every explicit model pin for one configuration, used when deleting a variant.</summary>
    Task ClearConfigBindingsAsync(string machineName, string configId, CancellationToken ct);

    /// <summary>Every per-configuration override on this machine, keyed by config id then setting key.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(
        string machineName, CancellationToken ct);

    /// <summary>
    /// Sets one configuration setting, replacing any existing value. <paramref name="settingValue"/> null or
    /// blank REMOVES the override, restoring the shipped default.
    /// </summary>
    Task SetOverrideAsync(
        string machineName, string configId, string settingKey, string? settingValue, CancellationToken ct);

    /// <summary>Removes every override for one configuration on this machine. Used when a DB-backed variant is deleted,
    /// so its per-variant tweaks don't outlive it (and can't be inherited by a later variant that reuses the id).</summary>
    Task ClearOverridesAsync(string machineName, string configId, CancellationToken ct);
}
