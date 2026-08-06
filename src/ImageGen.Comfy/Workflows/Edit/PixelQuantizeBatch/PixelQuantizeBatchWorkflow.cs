using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.PixelQuantizeBatch;

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
public sealed class PixelQuantizeBatchWorkflow : EditWorkflow<PixelQuantizeBatchParams>
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
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = WorkflowParamKeys.GridH, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        new() { Key = WorkflowParamKeys.Palette, Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette", Help = "median engine only — a locked palette is temporally consistent" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method" },
        new() { Key = WorkflowParamKeys.Engine, Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelEngines, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + one global palette over the batch" },
        new() { Key = WorkflowParamKeys.Thicken, Type = ParamType.Double, Min = 0, Max = 8, Label = "FP line thicken px" },
        new() { Key = WorkflowParamKeys.Tau, Type = ParamType.Double, Min = 0, Max = 2, Label = "FP de-AA tau" },
        new() { Key = WorkflowParamKeys.Lam, Type = ParamType.Double, Min = 0.001, Max = 0.2, Label = "FP flatten strength" },
        new() { Key = WorkflowParamKeys.K, Type = ParamType.Int, Min = 2, Max = 128, Label = "FP palette k-means" },
        new() { Key = WorkflowParamKeys.Beta, Type = ParamType.Double, Min = 0, Max = 4, Label = "FP rarity bias" },
        new() { Key = WorkflowParamKeys.Step, Type = ParamType.Double, Min = 1, Max = 20, Label = "FP DIN99d lattice step" },
        // Key BEFORE pixelizing (same as the still + video paths' in-graph keying): matte the whole batch (BiRefNet)
        // and feed the RGBA straight into the quantizer, which carries the alpha through to transparent-background
        // sprites — so there's no separate downstream matte to chain. Off = the frames enter opaque.
        new() { Key = WorkflowParamKeys.KeyBackground, Type = ParamType.Bool, Label = "Key background", Help = "Matte (BiRefNet) before pixelizing → transparent-background sprites" },
        new() { Key = WorkflowParamKeys.MatteThreshold, Type = ParamType.Double, Min = 0, Max = 1, Label = "Matte cutoff", Help = "0 = soft matte (quantizer hard-cuts per cell); >0 = hard BiRefNet cutoff" },
    };

    protected override ComfyWorkflowGraph Build(PixelQuantizeBatchParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // Batch = the source frame + every reference frame, IN ORDER, stacked into one (N,H,W,3) tensor via a chain of
        // core ImageBatch nodes (image1 accumulates, image2 is the next frame). The batch order is the order the caller
        // uploaded them (source, ref0, ref1, …), which the caller keeps == frame order, so the emitted lossless_frames
        // come back in that same order. All frames share one resolution, so ImageBatch never has to rescale.
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("The pixel quantizer needs a source image, but none was provided.") },
        };
        Output<Slot.Image> batch = LoadImage.ImageOut(EditNodes.Source);
        int node = 100;
        foreach (string refName in inputs.ReferenceImageNames)
        {
            string loadId = node++.ToString();
            g[loadId] = new LoadImage { Image = refName };
            string batchId = node++.ToString();
            g[batchId] = new ImageBatch { Image1 = batch, Image2 = LoadImage.ImageOut(loadId) };
            batch = ImageBatch.Out(batchId);
        }
        // key_background: matte the whole batch first, feed the RGBA (subject + alpha) into the quantizer at full res.
        // BiRefNetMatte processes the batched tensor (same node the video matte runs per frame); output 0 = RGBA.
        if (p.KeyBackground)
        {
            g[Nodes.Matte] = new global::ImageGen.Comfy.BiRefNetMatte { Image = batch, Threshold = QuantizeGuards.Req(p.MatteThreshold, WorkflowParamKeys.MatteThreshold) };
            batch = global::ImageGen.Comfy.BiRefNetMatte.Out(Nodes.Matte);
        }

        int gw = p.GridW;
        int gh = p.GridH;
        // The engine is a contract, not a flag: the concrete params type IS the branch, and its knobs are all present.
        g[Nodes.Quantize] = p switch
        {
            // Feature-preserving: derives ONE global palette + frequencies across all N frames (no replay globals —
            // this IS the derivation pass). Same node + knobs as the video fp.
            PixelQuantizeBatchFpParams fp => new PixelQuantizeFP
            {
                Image = batch,
                GridW = gw,
                GridH = gh,
                VirtualResolution = p.VirtualResolution,
                Thicken = fp.Thicken,
                Tau = fp.Tau,
                Lam = fp.Lam,
                K = fp.K,
                Beta = fp.Beta,
                Step = fp.Step,
            },
            PixelQuantizeBatchMedianParams md => new global::ImageGen.Comfy.PixelQuantize
            {
                Image = batch,
                GridW = gw,
                GridH = gh,
                Palette = md.Palette,
                Method = md.FinalMethod,
                VirtualResolution = p.VirtualResolution,
            },
            _ => throw new InvalidOperationException($"Unknown pixel-quantize engine contract: {p.GetType().Name}."),
        };
        g[Nodes.Save] = new SaveImage { Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.Quantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
