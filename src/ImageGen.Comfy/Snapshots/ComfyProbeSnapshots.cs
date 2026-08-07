using ImageGen.Application.Snapshots;

namespace ImageGen.Comfy.Snapshots;

/// <summary>
/// The three ComfyUI capability-probe snapshots behind one injectable facade (#198): files-by-kind, present nodes, and
/// folder paths. Groups their <see cref="ISnapshot{T}.Invalidate"/> hooks so a ComfyUI restart, a patch/node-pack
/// install, or a manual rescan can flush all three with one call, and gives multi-source consumers (the catalog
/// service, the rescan endpoint) a single dependency instead of three.
/// </summary>
public sealed class ComfyProbeSnapshots(
    ISnapshot<ComfyFilesByKind> filesByKind,
    ISnapshot<ComfyPresentNodes> presentNodes,
    ISnapshot<ComfyFolderPaths> folderPaths)
{
    /// <summary>The loadable-files-by-kind probe (with its derived flat union).</summary>
    public ISnapshot<ComfyFilesByKind> FilesByKind { get; } = filesByKind;

    /// <summary>The registered-custom-nodes probe.</summary>
    public ISnapshot<ComfyPresentNodes> PresentNodes { get; } = presentNodes;

    /// <summary>The on-disk model-roots probe.</summary>
    public ISnapshot<ComfyFolderPaths> FolderPaths { get; } = folderPaths;

    /// <summary>Flush all three probes — for a ComfyUI restart, a patch/node-pack install, or a manual rescan.</summary>
    public void InvalidateAll()
    {
        FilesByKind.Invalidate();
        PresentNodes.Invalidate();
        FolderPaths.Invalidate();
    }
}
