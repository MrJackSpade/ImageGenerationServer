using ImageGen.Application.Workflows;

namespace ImageGen.Comfy;

/// <summary>
/// The cross-workflow fan-out of a shared model binding. A Models-page edit can affect every workflow inheriting the
/// slot; workflow-scoped pickers instead create explicit pins. This helper names the OTHER slot users for diagnostics.
/// </summary>
internal static class SlotSharing
{
    /// <summary>The display names of the OTHER workflows that also require <paramref name="slotId"/>, excluding
    /// <paramref name="configId"/> itself, de-duplicated and ordered. Empty when the slot belongs to this workflow
    /// alone — then the picker shows no warning.</summary>
    public static IReadOnlyList<string> Others(
        IReadOnlyList<WorkflowStatus> workflows, string configId, string slotId,
        Func<WorkflowStatus, bool>? include = null) =>
        [.. workflows
            .Where(w => !string.Equals(w.Id, configId, StringComparison.OrdinalIgnoreCase)
                     && w.RequiredSlots.Contains(slotId, StringComparer.OrdinalIgnoreCase)
                     && (include?.Invoke(w) ?? true))
            .Select(w => w.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
}
