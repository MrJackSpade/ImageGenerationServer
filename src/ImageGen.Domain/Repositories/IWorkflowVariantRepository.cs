namespace ImageGen.Domain.Repositories;

/// <summary>
/// One DB-backed workflow variant on this machine: a duplicate of a shipped configuration held as a coexisting,
/// independently selectable catalogue entry (e.g. a hi-res and a low-res version of one model, for A/B testing).
/// </summary>
/// <param name="VariantId">The variant's own catalogue id — the string the client sends as <c>model</c>. Unique against
/// both the shipped files and other variants on this machine.</param>
/// <param name="BaseConfigId">The shipped configuration this was duplicated from. The variant inherits its workflow
/// class, requirements and card live; only its parameters are its own.</param>
/// <param name="FriendlyName">The display name the user gave the variant at duplication.</param>
/// <param name="ParamsJson">A SNAPSHOT of the base's effective parameters at copy time, as a JSON object
/// { paramKey: value }. Independent of the base thereafter — later base edits do not flow through.</param>
public sealed record WorkflowVariant(string VariantId, string BaseConfigId, string FriendlyName, string ParamsJson);

/// <summary>
/// The DB-backed workflow variants for a machine. Keyed by machine name for the same reason
/// <see cref="ICatalogOverrideRepository"/> is: a variant is a property of THIS box's catalogue, not a user preference,
/// and the shipped catalogue files are immutable. Per-variant parameter tweaks after duplication ride the existing
/// <see cref="ICatalogOverrideRepository"/> overrides, keyed on the variant's id.
/// </summary>
public interface IWorkflowVariantRepository
{
    /// <summary>Every variant defined on this machine.</summary>
    Task<IReadOnlyList<WorkflowVariant>> VariantsAsync(string machineName, CancellationToken ct);

    /// <summary>Persists a new variant. The caller has already ensured <see cref="WorkflowVariant.VariantId"/> is unique
    /// against the files and existing variants.</summary>
    Task AddAsync(string machineName, WorkflowVariant variant, CancellationToken ct);

    /// <summary>Removes a variant by id. A no-op when the id is not a variant on this machine.</summary>
    Task DeleteAsync(string machineName, string variantId, CancellationToken ct);
}
