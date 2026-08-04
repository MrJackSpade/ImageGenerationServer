namespace ImageGen.Comfy.Patches;

/// <summary>Where a patch stands against one ComfyUI installation. Derived every time it is asked for.</summary>
public enum PatchState
{
    /// <summary>The patch reverse-applies cleanly: the tree holds exactly what it puts there.</summary>
    Applied,

    /// <summary>It applies cleanly and has not been.</summary>
    NotApplied,

    /// <summary>What it patches is not installed. Only reachable for a patch that declares a <see cref="ComfyPatch.SourceUrl"/>.</summary>
    TargetMissing,

    /// <summary>Neither direction fits. Something else has changed the code this patch expects.</summary>
    Conflicted,
}

/// <summary>
/// One named change to a ComfyUI installation.
///
/// <para>Every modification this application makes to ComfyUI is one of these — the core fix, the node packs
/// it ships, and the fixes it carries for third-party packs alike. There is deliberately no second mechanism:
/// a change that arrives by being copied into place cannot be listed, cannot be removed, and is invisible
/// until something built on it breaks.</para>
/// </summary>
/// <param name="Id">Stable identifier. The API takes this; it is not a display string and must not change.</param>
/// <param name="Title">Short name, for the settings page. Anything longer belongs in <paramref name="Does"/>.</param>
/// <param name="Does">
/// What this patch makes the renderer do, in a sentence or two, in the present tense. Shown UNDER the name on the
/// settings page, because a list of names is a list nobody can decide anything from: the question in front of
/// somebody looking at that page is whether to apply or remove a thing, and the name alone does not answer it.
/// Distinct from <paramref name="Why"/> — this is the effect, that is the reasoning behind it.
/// </param>
/// <param name="Why">What it fixes and what breaks without it. Shown as the row's tooltip.</param>
/// <param name="Target">Directory the diff is relative to, ComfyUI-root-relative. "." is ComfyUI itself.</param>
/// <param name="SourceUrl">Upstream repository of the pack this patches, when the patch may install it. Null for a patch whose target is always present, or which creates its target itself.</param>
/// <param name="Rev">Commit the diff was taken against, and the revision <paramref name="SourceUrl"/> is fetched at. Pinned, never a branch.</param>
/// <param name="Warn">Shown, and confirmed, before removing. For a patch whose absence changes a guarantee rather than just a feature.</param>
/// <param name="Order">Display and apply order.</param>
/// <param name="Provides">
/// Catalogue requirement ids this patch satisfies — the <c>custom_node</c> slots a workflow lists as needed.
/// Declared HERE rather than on the catalogue entry so that adding a patch never means editing a shipped
/// catalogue file, and so someone adding a requirement of their own need know nothing about patches.
/// </param>
/// <param name="Files">The diff itself.</param>
public sealed record ComfyPatch(
    string Id,
    string Title,
    string Does,
    string Why,
    string Target,
    string? SourceUrl,
    string? Rev,
    string? Warn,
    int Order,
    IReadOnlyList<string> Provides,
    IReadOnlyList<FileDiff> Files)
{
    /// <summary>
    /// True when this patch only ever creates files — a node pack this repository ships. Such a patch builds its
    /// own target, so a missing directory means "not applied yet" rather than "the thing it patches is not
    /// installed".
    /// </summary>
    public bool CreatesItsTarget => Files.Count > 0 && Files.All(f => f.Change == FileChange.Add);

    /// <summary>
    /// True when this patch is an installation and nothing more: a third-party pack we run UNMODIFIED, pinned to
    /// the revision it was known to work at.
    ///
    /// <para>These carry no diff because there is nothing of ours in them, and they exist because "we changed
    /// nothing" is not the same as "it need not be here". A pack that only ever arrived by somebody cloning it by
    /// hand is a dependency the app cannot state, cannot check and cannot reinstall — which is exactly how a
    /// rebuilt ComfyUI comes back missing the packs fifteen workflows need.</para>
    ///
    /// <para>What is verified is PRESENCE, not content: an unpacked archive keeps no revision of its own. That is
    /// the honest limit of an install-only patch and the page says "installed" rather than "in place".</para>
    /// </summary>
    public bool IsInstallOnly => Files.Count == 0 && SourceUrl is not null;

    /// <summary>The absolute directory this patch's paths are relative to, under a given ComfyUI root.</summary>
    public string ResolveTarget(string comfyRoot) =>
        Target == "."
            ? comfyRoot
            : Path.Combine(comfyRoot, Target.Replace('/', Path.DirectorySeparatorChar));
}
