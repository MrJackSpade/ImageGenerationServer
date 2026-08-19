using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.PixelQuantizeVideo;

/// <summary>
/// Deterministic pixel-art quantizer for VIDEO (video-to-video) — the model-free, no-VRAM pixelizer applied to every
/// frame of a source clip. Decodes the clip to frames (<c>LoadVideo</c> → <c>GetVideoComponents</c>), runs the same
/// <c>PixelQuantize</c> node that does the still pixelizer over the whole frame batch (it processes a batched IMAGE
/// tensor and keeps each frame's resolution), and re-encodes an animated WEBP at the source clip's own frame rate. A
/// LOCKED (named) palette is the default so the palette is identical every frame — temporally consistent, no
/// frame-to-frame flicker. No diffusion, no checkpoint: a quantize costs effectively nothing and never blocks a real
/// generation. <see cref="SourceMedia"/> = Video tells <see cref="ComfyClient.SubmitEditAsync"/> to upload the source
/// as a real video file (an animated-webp clip is transcoded to mp4 first) instead of a PNG.
/// </summary>
public sealed class PixelQuantizeVideoWorkflow : Workflow<PixelQuantizeVideoParams>
{
    public override string Name => "pixel-quantize-video";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    /// <summary>Outputs an animated WEBP clip.</summary>
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Consumes a video clip (video-to-video).</summary>
    public override WorkflowMedia SourceMedia => WorkflowMedia.Video;
    /// <summary>No prompt at all — the quantize is purely deterministic.</summary>
    public override bool PromptDirectsMotion => false;
    /// <summary>Restyle to a fixed grid+palette — exempt from the no-change gate (also moot for video, which skips it).</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure-CPU quantizer — no checkpoint, must not be hidden by the catalog's no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => QuantizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QuantizeSchema =
    [
        // Virtual resolution = the grid's longest edge (aspect from the frame); each frame keeps its input resolution.
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = WorkflowParamKeys.GridH, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        // A named (locked) palette is the same every frame → temporally consistent. 'adaptive' would re-derive a
        // palette per frame and flicker, so a locked palette is the default for video.
        new() { Key = WorkflowParamKeys.Palette, Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette", Help = "A locked (named) palette is temporally consistent — no frame-to-frame flicker" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // 0 (default) = keep the source clip's frame rate (wired from GetVideoComponents); >0 overrides it.
        new() { Key = WorkflowParamKeys.Fps, Type = ParamType.Double, Min = 0, Max = 60, Label = "Output FPS", Help = "0 = keep the source clip's frame rate" },
        // Engine selector. 'median' = the original per-frame PixelQuantize (named/locked palette). 'fp' =
        // PixelQuantizeFP: L0 flatten + XDoG line-thicken + de-AA edge-collapse, then ONE global per-video
        // palette (DIN99d) so it's temporally consistent WITHOUT a named palette (the palette/final_method
        // params are ignored for 'fp'). The fp_* knobs below tune it.
        new() { Key = WorkflowParamKeys.Engine, Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelEngines, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + global palette" },
        new() { Key = WorkflowParamKeys.Thicken, Type = ParamType.Double, Min = 0, Max = 8, Label = "FP line thicken px", Help = "fp engine: XDoG outline thicken (sub-pixel ok)" },
        new() { Key = WorkflowParamKeys.Tau, Type = ParamType.Double, Min = 0, Max = 2, Label = "FP de-AA tau", Help = "fp engine: edge-collapse plateau/transition threshold" },
        new() { Key = WorkflowParamKeys.Lam, Type = ParamType.Double, Min = 0.001, Max = 0.2, Label = "FP flatten strength" },
        new() { Key = WorkflowParamKeys.K, Type = ParamType.Int, Min = 2, Max = 128, Label = "FP palette k-means" },
        new() { Key = WorkflowParamKeys.Beta, Type = ParamType.Double, Min = 0, Max = 4, Label = "FP rarity bias" },
        new() { Key = WorkflowParamKeys.Step, Type = ParamType.Double, Min = 1, Max = 20, Label = "FP DIN99d lattice step" },
        // Key BEFORE pixelizing: matte every frame (BiRefNet) at FULL resolution and feed the RGBA batch straight into
        // the quantizer, which carries the alpha through to a transparent-background clip (saved lossless so it
        // survives). The matte runs INSIDE this graph — the RGBA stays an in-memory tensor, never round-tripping through
        // a webp decode that would drop the alpha. Off = the legacy opaque path.
        new() { Key = WorkflowParamKeys.KeyBackground, Type = ParamType.Bool, Label = "Key background", Help = "Matte (BiRefNet) before pixelizing → transparent-background clip (lossless)" },
        new() { Key = WorkflowParamKeys.MatteThreshold, Type = ParamType.Double, Min = 0, Max = 1, Label = "Matte cutoff", Help = "0 = soft matte (quantizer hard-cuts per cell); >0 = hard BiRefNet cutoff" },
    ];

    protected override ComfyWorkflowGraph Build(PixelQuantizeVideoParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // Source clip → frames (+ its frame rate). No model head: the quantizer is pure CPU.
        ComfyWorkflowGraph g = new()
        {
            [Nodes.Source] = new LoadVideo { File = inputs.SourceVideoName ?? throw new RenderValidationException("The video quantizer needs a source clip, but none was provided.") },
            [Nodes.Frames] = new GetVideoComponents { Video = LoadVideo.VideoOut(Nodes.Source) },
        };
        int gw = p.GridW;
        int gh = p.GridH;
        // key_background: matte every frame first, feeding RGBA (subject + alpha) into the quantizer. The BiRefNetMatte
        // node sits between the decoded frames and the quantizer so the alpha stays a tensor (no lossy round-trip).
        bool key = p.KeyBackground;
        Output<Slot.Image> frames = GetVideoComponents.ImagesOut(Nodes.Frames);
        if (key)
        {
            g[Nodes.Matte] = new global::ImageGen.Comfy.BiRefNetMatte { Image = GetVideoComponents.ImagesOut(Nodes.Frames), Threshold = QuantizeGuards.Req(p.MatteThreshold, WorkflowParamKeys.MatteThreshold) };
            frames = global::ImageGen.Comfy.BiRefNetMatte.Out(Nodes.Matte);
        }
        // Both engines process the whole (N,H,W,C) frame batch and return N quantized frames at the same resolution.
        // The engine is a contract, not a flag: the concrete params type IS the branch, and its knobs are all present.
        g[Nodes.Quantize] = p switch
        {
            // Feature-preserving: derives ONE global palette across all frames.
            PixelQuantizeVideoFpParams fp => new PixelQuantizeFP
            {
                Image = frames,
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
            PixelQuantizeVideoMedianParams md => new global::ImageGen.Comfy.PixelQuantize
            {
                Image = frames,
                GridW = gw,
                GridH = gh,
                Palette = md.Palette,
                Method = md.FinalMethod,
                VirtualResolution = p.VirtualResolution,
            },
            _ => throw new InvalidOperationException($"Unknown pixel-quantize engine contract: {p.GetType().Name}."),
        };
        // Keep the source clip's frame rate by default (GetVideoComponents output 2); an explicit fps>0 overrides it.
        // Keyed output must be LOSSLESS so the alpha channel survives the webp encode (as the matte/deflicker passes do).
        double fps = p.Fps;
        int quality = key ? 100 : 80;
        g[Nodes.Save] = fps > 0
            ? new SaveAnimatedWEBPFixedFps
            {
                Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.Quantize),
                FilenamePrefix = OutputPrefixes.Edit,
                Fps = fps,
                Lossless = key,
                Quality = quality,
                Method = ComfyWidgets.WebpMethod.Default,
            }
            : new SaveAnimatedWEBP
            {
                Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.Quantize),
                FilenamePrefix = OutputPrefixes.Edit,
                Fps = GetVideoComponents.FpsOut(Nodes.Frames),
                Lossless = key,
                Quality = quality,
                Method = ComfyWidgets.WebpMethod.Default,
            };
        return g;
    }
}
