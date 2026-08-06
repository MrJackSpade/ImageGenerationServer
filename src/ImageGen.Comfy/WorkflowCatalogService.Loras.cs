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
    /// <inheritdoc/>
    public async Task<IReadOnlyList<LoraCatalogEntry>> ListLorasAsync(string? workflowId, CancellationToken ct)
    {
        IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> byKind = await _comfy.GetPresentFilesByKindAsync(ct);
        IReadOnlyList<string> loras = byKind.TryGetValue(RequirementKind.Lora, out IReadOnlyList<string>? files) ? files : [];
        List<string> names = [.. loras.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

        // Without a workflow to check against, compatibility isn't evaluated — the picker shows the full list.
        WorkflowConfiguration? cfg = workflowId is null ? null : _catalog.FindConfig(workflowId);
        if (cfg is null)
        {
            return [.. names.Select(n => new LoraCatalogEntry(n, Compatible: true, ClipCapable: true))];
        }

        // Resolve the workflow's bound checkpoint and ComfyUI's on-disk roots, read the checkpoint's layer dimensions
        // once, then compare each LoRA's feature dimensions against them (file headers only, cached per file).
        string checkpointFile = _catalog.Resolve(cfg).Checkpoint;
        IReadOnlyDictionary<string, IReadOnlyList<string>> folders = await _comfy.GetFolderPathsAsync(ct);
        IReadOnlySet<long>? checkpointDims = ResolveCheckpointDims(checkpointFile, folders);
        IReadOnlyList<string> loraRoots = folders.TryGetValue(ComfyFolderKeys.Loras, out IReadOnlyList<string>? lr) ? lr : [];

        return [.. names.Select(n =>
        {
            string? path = ResolveInRoots(loraRoots, n);
            if (path is null)
            {
                return new LoraCatalogEntry(n, Compatible: true, ClipCapable: true);   // can't locate the file → show it
            }

            LoraCompatibility.Result r = LoraCompatibility.Evaluate(path, checkpointDims);
            return new LoraCatalogEntry(n, r.Compatible, r.ClipCapable);
        })];
    }

    /// <summary>The set of layer dimensions in the workflow's bound checkpoint, or null when it can't be located/read.
    /// A diffusion model lives under <c>checkpoints</c> (CheckpointLoaderSimple) or <c>diffusion_models</c>/<c>unet</c>
    /// (UNETLoader/GGUF), so all three roots are searched.</summary>
    private static IReadOnlySet<long>? ResolveCheckpointDims(
        string? checkpointFile, IReadOnlyDictionary<string, IReadOnlyList<string>> folders)
    {
        if (string.IsNullOrWhiteSpace(checkpointFile))
        {
            return null;
        }

        List<string> roots = [];
        foreach (string? key in new[] { "checkpoints", "diffusion_models", "unet" })
        {
            if (folders.TryGetValue(key, out IReadOnlyList<string>? r))
            {
                roots.AddRange(r);
            }
        }

        string? path = ResolveInRoots(roots, checkpointFile);
        return path is null ? null : LoraCompatibility.CheckpointDims(path);
    }

    /// <summary>The first root under which the subfolder-qualified <paramref name="name"/> exists on disk, or null.</summary>
    private static string? ResolveInRoots(IReadOnlyList<string> roots, string name)
    {
        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}