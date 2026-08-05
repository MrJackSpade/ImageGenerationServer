using ImageGen.Application.Workflows;

namespace ImageGen.Comfy;

/// <summary>
/// The LoRA-catalogue half of the service: the LoRA files this machine offers to the composer's picker, each
/// annotated (when a workflow is named) with whether it will actually apply to that workflow's base model.
/// <para>The picker is offered only for a SINGLE selected model (a LoRA is model-specific), so exactly one workflow is
/// evaluated. Compatibility is computed from file headers only (no VRAM, no ComfyUI round-trip) — see
/// <see cref="LoraCompatibility"/> — and works for both <c>.safetensors</c> and <c>.gguf</c> base models.</para>
/// </summary>
public sealed partial class WorkflowCatalogService
{
    /// <summary>ComfyUI's folder-paths key for the LoRA roots.</summary>
    private const string LorasFolderKey = "loras";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LoraCatalogEntry>> ListLorasAsync(string? workflowId, CancellationToken ct)
    {
        var byKind = await _comfy.GetPresentFilesByKindAsync(ct);
        var loras = byKind.TryGetValue(RequirementKind.Lora, out var files) ? files : [];
        var names = loras.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // Without a workflow to check against, compatibility isn't evaluated — the picker shows the full list.
        var cfg = workflowId is null ? null : _catalog.FindConfig(workflowId);
        if (cfg is null)
            return names.Select(n => new LoraCatalogEntry(n, Compatible: true, ClipCapable: true)).ToList();

        // Resolve the workflow's bound checkpoint and ComfyUI's on-disk roots, read the checkpoint's layer dimensions
        // once, then compare each LoRA's feature dimensions against them (file headers only, cached per file).
        var checkpointFile = _catalog.Resolve(cfg).Checkpoint;
        var folders = await _comfy.GetFolderPathsAsync(ct);
        var checkpointDims = ResolveCheckpointDims(checkpointFile, folders);
        var loraRoots = folders.TryGetValue(LorasFolderKey, out var lr) ? lr : [];

        return names.Select(n =>
        {
            var path = ResolveInRoots(loraRoots, n);
            if (path is null)
                return new LoraCatalogEntry(n, Compatible: true, ClipCapable: true);   // can't locate the file → show it
            var r = LoraCompatibility.Evaluate(path, checkpointDims);
            return new LoraCatalogEntry(n, r.Compatible, r.ClipCapable);
        }).ToList();
    }

    /// <summary>The set of layer dimensions in the workflow's bound checkpoint, or null when it can't be located/read.
    /// A diffusion model lives under <c>checkpoints</c> (CheckpointLoaderSimple) or <c>diffusion_models</c>/<c>unet</c>
    /// (UNETLoader/GGUF), so all three roots are searched.</summary>
    private static IReadOnlySet<long>? ResolveCheckpointDims(
        string? checkpointFile, IReadOnlyDictionary<string, IReadOnlyList<string>> folders)
    {
        if (string.IsNullOrWhiteSpace(checkpointFile)) return null;
        var roots = new List<string>();
        foreach (var key in new[] { "checkpoints", "diffusion_models", "unet" })
            if (folders.TryGetValue(key, out var r)) roots.AddRange(r);
        var path = ResolveInRoots(roots, checkpointFile);
        return path is null ? null : LoraCompatibility.CheckpointDims(path);
    }

    /// <summary>The first root under which the subfolder-qualified <paramref name="name"/> exists on disk, or null.</summary>
    private static string? ResolveInRoots(IReadOnlyList<string> roots, string name)
    {
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
