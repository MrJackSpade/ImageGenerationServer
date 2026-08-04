//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// A user's relationships to workflows: which they starred, which they hid, and the labels they put on them.
/// <para>These are RELATIONS — user × workflow, and user × workflow × tag — and they are now stored as such
/// (dbo.UserFavoriteWorkflow, dbo.UserHiddenWorkflow, dbo.UserWorkflowTag). They used to be three JSON blobs on the
/// user row, which meant nothing could ask which users favourited a workflow, and nothing cleaned up when a workflow
/// left the catalog.</para>
/// <para>Deliberately NOT part of <see cref="User"/>. Every authenticated request loads the user; these are read on
/// the settings path only, and hanging three more queries off the hot path to carry data almost nobody asks for is
/// how a relation becomes "too slow to normalise".</para>
/// </summary>
/// <param name="Favorites">Workflow ids the user starred. Not sensitive — stored plain, so they can be joined.</param>
/// <param name="Hidden">Workflow ids the user hid from the UI picker. Also plain.</param>
/// <param name="HiddenApi">Workflow ids the user hid from the API workflow list — independent of <paramref
/// name="Hidden"/>, so a workflow can be in the picker but not returned to that user's API key, or the reverse.</param>
/// <param name="Tags">workflow id → the user's own labels for it. The LABELS are the user's words, so they are
/// encrypted (deterministically, since each is a set member that has to stay unique per workflow).</param>
public sealed record UserWorkflowPrefs(
    IReadOnlyList<string> Favorites,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> HiddenApi,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tags)
{
    /// <summary>The empty set, for a user who has starred, hidden and labelled nothing.</summary>
    public static UserWorkflowPrefs Empty { get; } =
        new([], [], [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
