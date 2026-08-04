using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Deterministic pixel-art quantizer for a BATCH OF STILL IMAGES — the images-in, images-out sibling of
/// <see cref="PixelQuantizeVideoWorkflow"/>. It feeds N uploaded frames (the edit source + every reference image, in
/// order) into the SAME <c>PixelQuantizeFP</c> node the video path uses, by stacking them into one <c>(N,H,W,3)</c>
/// tensor with a chain of core <c>ImageBatch</c> nodes — so the fp engine derives ONE global palette + label
/// frequencies across the whole set (temporal coherence) and emits the per-frame lossless sprites + those globals as
/// its side-channel outputs, exactly as the video workflow does. The point: the palette derivation is defined over a
/// SET OF IMAGES, so it takes images directly — no <c>LoadVideo</c>, no <c>GetVideoComponents</c>, no ffmpeg transport,
/// no animated-WEBP re-encode. All frames must share one resolution (they do — the caller upscales every frame to the
/// export's native budget before submit). API-only; the orchestrator submits it by id with the frames as source + refs.
/// </summary>
public sealed class PixelQuantizeBatchWorkflow : EditWorkflowBase
{
    public override string Name => "pixel-quantize-batch";
    /// <summary>Restyle to grid+palette — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure CPU quantizer — no checkpoint, must not be hidden by the no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => QuantizeSchema;

    /// <summary>The fp knobs mirror pixel-quantize-video's fp branch (this is the batch equivalent of that derivation
    /// pass); engine defaults to 'fp' because the whole reason to batch is deriving the global palette the fp engine needs.</summary>
    private static readonly IReadOnlyList<ParamSpec> QuantizeSchema = new ParamSpec[]
    {
        new() { Key = "virtual_resolution", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = "grid_h", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        new() { Key = "palette", Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette", Help = "median engine only — a locked palette is temporally consistent" },
        new() { Key = "final_method", Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method" },
        new() { Key = "engine", Type = ParamType.Enum, Choices = new[] { "median", "fp" }, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + one global palette over the batch" },
        new() { Key = "thicken", Type = ParamType.Double, Min = 0, Max = 8, Label = "FP line thicken px" },
        new() { Key = "tau", Type = ParamType.Double, Min = 0, Max = 2, Label = "FP de-AA tau" },
        new() { Key = "lam", Type = ParamType.Double, Min = 0.001, Max = 0.2, Label = "FP flatten strength" },
        new() { Key = "k", Type = ParamType.Int, Min = 2, Max = 128, Label = "FP palette k-means" },
        new() { Key = "beta", Type = ParamType.Double, Min = 0, Max = 4, Label = "FP rarity bias" },
        new() { Key = "step", Type = ParamType.Double, Min = 1, Max = 20, Label = "FP DIN99d lattice step" },
        // Key BEFORE pixelizing (same as the still + video paths' in-graph keying): matte the whole batch (BiRefNet)
        // and feed the RGBA straight into the quantizer, which carries the alpha through to transparent-background
        // sprites — so there's no separate downstream matte to chain. Off = the frames enter opaque.
        new() { Key = "key_background", Type = ParamType.Bool, Label = "Key background", Help = "Matte (BiRefNet) before pixelizing → transparent-background sprites" },
        new() { Key = "matte_threshold", Type = ParamType.Double, Min = 0, Max = 1, Label = "Matte cutoff", Help = "0 = soft matte (quantizer hard-cuts per cell); >0 = hard BiRefNet cutoff" },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // Batch = the source frame + every reference frame, IN ORDER, stacked into one (N,H,W,3) tensor via a chain of
        // core ImageBatch nodes (image1 accumulates, image2 is the next frame). The batch order is the order the caller
        // uploaded them (source, ref0, ref1, …), which the caller keeps == frame order, so the emitted lossless_frames
        // come back in that same order. All frames share one resolution, so ImageBatch never has to rescale.
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("The pixel quantizer needs a source image, but none was provided.") }),
        };
        object batch = ComfyGraph.Ref("10", 0);
        int node = 100;
        foreach (var refName in inputs.ReferenceImageNames)
        {
            var loadId = (node++).ToString();
            wf[loadId] = ComfyGraph.Node("LoadImage", new { image = refName });
            var batchId = (node++).ToString();
            wf[batchId] = ComfyGraph.Node("ImageBatch", new { image1 = batch, image2 = ComfyGraph.Ref(loadId, 0) });
            batch = ComfyGraph.Ref(batchId, 0);
        }
        // key_background: matte the whole batch first, feed the RGBA (subject + alpha) into the quantizer at full res.
        // BiRefNetMatte processes the batched tensor (same node the video matte runs per frame); output 0 = RGBA.
        if (p.Bool("key_background"))
        {
            wf["15"] = ComfyGraph.Node("BiRefNetMatte", new { image = batch, threshold = p.DblReq("matte_threshold") });
            batch = ComfyGraph.Ref("15", 0);
        }

        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        if (p.StrReq("engine") == "fp")
        {
            // Feature-preserving: derives ONE global palette + frequencies across all N frames (no replay globals —
            // this IS the derivation pass), so 'palette'/'final_method' are unused. Same node + knobs as the video fp.
            wf["20"] = ComfyGraph.Node("PixelQuantizeFP", new
            {
                image = batch,
                grid_w = gw,
                grid_h = gh,
                virtual_resolution = p.IntReq("virtual_resolution"),
                thicken = p.DblReq("thicken"),
                tau = p.DblReq("tau"),
                lam = p.DblReq("lam"),
                k = p.IntReq("k"),
                beta = p.DblReq("beta"),
                step = p.DblReq("step"),
            });
        }
        else
        {
            wf["20"] = ComfyGraph.Node("PixelQuantize", new
            {
                image = batch,
                grid_w = gw,
                grid_h = gh,
                palette = p.StrReq("palette"),
                method = p.StrReq("final_method"),
                virtual_resolution = p.IntReq("virtual_resolution"),
            });
        }
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("20", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
