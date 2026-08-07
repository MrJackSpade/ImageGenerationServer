using ImageGen.Application.Workflows;

namespace ImageGen.Comfy;

/// <summary>
/// The cross-workflow fan-out of a model binding. A binding is global per <c>(machine, slot)</c>, so pointing a slot
/// at a different file from one workflow's page changes it for every workflow that requires that same slot. This
/// inverts the per-workflow required-slots lists into "who else shares this slot", which the detail-page picker names
/// in red so the change is never silent (issue #195).
/// </summary>
internal static class SlotSharing
{
    /// <summary>The display names of the OTHER workflows that also require <paramref name="slotId"/>, excluding
    /// <paramref name="configId"/> itself, de-duplicated and ordered. Empty when the slot belongs to this workflow
    /// alone — then the picker shows no warning.</summary>
    public static IReadOnlyList<string> Others(
        IReadOnlyList<WorkflowStatus> workflows, string configId, string slotId) =>
        [.. workflows
            .Where(w => !string.Equals(w.Id, configId, StringComparison.OrdinalIgnoreCase)
                        && w.RequiredSlots.Contains(slotId, StringComparer.OrdinalIgnoreCase))
            .Select(w => w.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
}
