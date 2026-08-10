namespace ImageGen.Domain.Entities;

/// <summary>
/// A user's relationships to workflows: which they starred, which they hid, and the labels they put on them.
/// <para>These are RELATIONS — user × workflow, and user × workflow × tag — stored as such
/// (dbo.UserFavoriteWorkflow, dbo.UserHiddenWorkflow, dbo.UserWorkflowTag). As JSON blobs on the user row nothing
/// could ask which users favourited a workflow, and nothing would clean up when a workflow left the catalog.</para>
/// <para>Deliberately NOT part of <see cref="User"/>. Every authenticated request loads the user; these are read on
/// the settings path only, and hanging three more queries off the hot path to carry data almost nobody asks for is
/// how a relation becomes "too slow to normalise".</para>
/// </summary>
/// <param name="Favorites">Workflow ids the user starred. Not sensitive — stored plain, so they can be joined.</param>
/// <param name="Hidden">Workflow ids the user hid from the UI picker. Also plain.</param>
/// <param name="HiddenApi">Workflow ids the user hid from the API workflow list — independent of <paramref
/// name="Hidden"/>, so a workflow can be in the picker but not returned to that user's API key, or the reverse.</param>
/// <param name="Tags">workflow id → the labels the user ADDED on top of the workflow's base (definition) tags. The
/// LABELS are the user's words, so they are encrypted (deterministically, since each is a set member that has to stay
/// unique per workflow). This is the additive half of the per-workflow tag delta.</param>
/// <param name="RemovedTags">workflow id → the BASE tags the user took off. The displayed set is (the definition's
/// base tags + <paramref name="Tags"/>) minus these; a base tag added to the definition later still shows because it
/// was never in anyone's removed set. Stored the same encrypted way, in the same relation, distinguished by a flag.</param>
public sealed record UserWorkflowPrefs(
    IReadOnlyList<string> Favorites,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> HiddenApi,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RemovedTags)
{
    /// <summary>The empty set, for a user who has starred, hidden and labelled nothing.</summary>
    public static UserWorkflowPrefs Empty { get; } =
        new([], [], [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
