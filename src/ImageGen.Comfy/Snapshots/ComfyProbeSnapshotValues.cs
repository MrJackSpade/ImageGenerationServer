namespace ImageGen.Comfy.Snapshots;

/// <summary>
/// The consolidated result of ONE ComfyUI capability sweep (#198): the loadable files grouped by requirement kind,
/// with the flat present-files union DERIVED from it (one pass over ComfyUI per rebuild, not two). The SeedVR2
/// on-disk narrowing and HuggingFace-shard collapse already happened inside the sweep, so both views agree.
/// </summary>
public sealed class ComfyFilesByKind
{
    /// <param name="byKind">The by-kind file lists straight from <c>ComfyClient.GetPresentFilesByKindAsync</c>.</param>
    public ComfyFilesByKind(IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> byKind)
    {
        ByKind = Domain.Ensure.NotNull(byKind);
        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyList<string> files in byKind.Values)
        {
            all.UnionWith(files);
        }

        AllFiles = all;
    }

    /// <summary>What each slot kind can be filled with on this machine. A kind absent here has no loader in this build.</summary>
    public IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> ByKind { get; }

    /// <summary>The flat union of every kind's files — the presence-gating set (was <c>GetPresentFilesAsync</c>).</summary>
    public IReadOnlySet<string> AllFiles { get; }

    /// <summary>The files for one kind, or an empty list when the kind has no loader in this build.</summary>
    public IReadOnlyList<string> ForKind(RequirementKind kind) =>
        ByKind.TryGetValue(kind, out IReadOnlyList<string>? files) ? files : [];
}

/// <summary>Which of the catalog's declared custom nodes this ComfyUI has registered (#198).</summary>
public sealed class ComfyPresentNodes(IReadOnlySet<string> nodes)
{
    /// <summary>The registered node class names.</summary>
    public IReadOnlySet<string> Nodes { get; } = Domain.Ensure.NotNull(nodes);

    /// <summary>Whether ComfyUI has the given node registered.</summary>
    public bool Contains(string node) => Nodes.Contains(node);
}

/// <summary>ComfyUI's on-disk model roots by category, from <c>/internal/folder_paths</c> (#198). Empty when the
/// endpoint is absent (older build) or the renderer is another machine.</summary>
public sealed class ComfyFolderPaths(IReadOnlyDictionary<string, IReadOnlyList<string>> roots)
{
    /// <summary>Category (e.g. "loras", "checkpoints") → the absolute directories ComfyUI searches.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Roots { get; } = Domain.Ensure.NotNull(roots);

    /// <summary>Every distinct absolute directory across all categories.</summary>
    public IEnumerable<string> AllDirectories => Roots.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase);
}
