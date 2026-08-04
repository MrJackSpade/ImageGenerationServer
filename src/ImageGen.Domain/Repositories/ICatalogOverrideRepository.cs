//TODO: CHECK FOR FALLBACKS
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

/// <summary>
/// One per-configuration setting overridden on this machine.
/// </summary>
/// <param name="ConfigId">The workflow configuration.</param>
/// <param name="SettingKey">
/// Namespaced key: <c>vram.min</c>, <c>vram.max</c>, or <c>param.&lt;key&gt;</c> for an exposed parameter's default.
/// </param>
/// <param name="SettingValue">The raw text, coerced through the parameter's existing type when it is read.</param>
public sealed record CatalogOverride(string ConfigId, string SettingKey, string SettingValue);

/// <summary>
/// The install-wide catalogue overrides for a machine: model bindings and per-configuration settings.
///
/// <para>Keyed by machine name rather than by user, because these describe the BOX — which file is on its disk,
/// what its GPU can afford — not somebody's preference. The shipped catalogue is immutable; everything a user
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

    /// <summary>Every per-configuration override on this machine, keyed by config id then setting key.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(
        string machineName, CancellationToken ct);

    /// <summary>
    /// Sets one configuration setting, replacing any existing value. <paramref name="settingValue"/> null or
    /// blank REMOVES the override, restoring the shipped default.
    /// </summary>
    Task SetOverrideAsync(
        string machineName, string configId, string settingKey, string? settingValue, CancellationToken ct);
}
