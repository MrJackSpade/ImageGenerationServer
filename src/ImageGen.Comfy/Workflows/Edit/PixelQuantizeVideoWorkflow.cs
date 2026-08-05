using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Deterministic pixel-art quantizer for VIDEO (video-to-video) — the model-free, no-VRAM pixelizer applied to every
/// frame of a source clip. Decodes the clip to frames (<c>LoadVideo</c> → <c>GetVideoComponents</c>), runs the same
/// <c>PixelQuantize</c> node that does the still pixelizer over the whole frame batch (it processes a batched IMAGE
/// tensor and keeps each frame's resolution), and re-encodes an animated WEBP at the source clip's own frame rate. A
/// LOCKED (named) palette is the default so the palette is identical every frame — temporally consistent, no
/// frame-to-frame flicker. No diffusion, no checkpoint: a quantize costs effectively nothing and never blocks a real
/// generation. <see cref="SourceMedia"/> = Video tells <see cref="ComfyClient.SubmitEditAsync"/> to upload the source
/// as a real video file (an animated-webp clip is transcoded to mp4 first) instead of a PNG. This is the only editor
/// the UI offers when the source is a clip.
/// </summary>
public sealed class PixelQuantizeVideoWorkflow : Workflow<PixelQuantizeVideoParams>
{
    public override string Name => "pixel-quantize-video";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    /// <summary>Outputs an animated WEBP clip.</summary>
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Consumes a video clip (video-to-video) — the one editor that does.</summary>
    public override WorkflowMedia SourceMedia => WorkflowMedia.Video;
    /// <summary>No prompt at all — the quantize is purely deterministic.</summary>
    public override bool PromptDirectsMotion => false;
    /// <summary>Restyle to a fixed grid+palette — exempt from the no-change gate (also moot for video, which skips it).</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure-CPU quantizer — no checkpoint, must not be hidden by the catalog's no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => QuantizeSchema;

    /// <summary>Node ids named by role. Values preserved exactly.</summary>
    private static class Nodes
    {
        public const string Source = "10";
        public const string Frames = "11";
        public const string Matte = "15";
        public const string Quantize = "20";
        public const string Save = "9";
    }

    /// <summary>The <c>engine</c> param's feature-preserving value — routes to <c>PixelQuantizeFP</c>.</summary>
    private const string FpEngine = "fp";

    private static readonly IReadOnlyList<ParamSpec> QuantizeSchema = new ParamSpec[]
    {
        // Virtual resolution = the grid's longest edge (aspect from the frame); each frame keeps its input resolution.
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = WorkflowParamKeys.GridH, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        // A named (locked) palette is the same every frame → temporally consistent. 'adaptive' would re-derive a
        // palette per frame and flicker, so a locked palette is the default for video.
        new() { Key = WorkflowParamKeys.Palette, Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette", Help = "A locked (named) palette is temporally consistent — no frame-to-frame flicker" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // 0 (default) = keep the source clip's frame rate (wired from GetVideoComponents); >0 overrides it.
        new() { Key = WorkflowParamKeys.Fps, Type = ParamType.Double, Min = 0, Max = 60, Label = "Output FPS", Help = "0 = keep the source clip's frame rate" },
        // Engine selector. 'median' = the original per-frame PixelQuantize (named/locked palette). 'fp' =
        // PixelQuantizeFP: L0 flatten + XDoG line-thicken + de-AA edge-collapse, then ONE global per-video
        // palette (DIN99d) so it's temporally consistent WITHOUT a named palette (the palette/final_method
        // params are ignored for 'fp'). The fp_* knobs below tune it.
        new() { Key = WorkflowParamKeys.Engine, Type = ParamType.Enum, Choices = new[] { "median", "fp" }, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + global palette" },
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
    };

    protected override ComfyWorkflowGraph Build(PixelQuantizeVideoParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // Source clip → frames (+ its frame rate). No model head: the quantizer is pure CPU.
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
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
            g[Nodes.Matte] = new BiRefNetMatte { Image = GetVideoComponents.ImagesOut(Nodes.Frames), Threshold = QuantizeGuards.Req(p.MatteThreshold, WorkflowParamKeys.MatteThreshold) };
            frames = BiRefNetMatte.Out(Nodes.Matte);
        }
        // Both engines process the whole (N,H,W,C) frame batch and return N quantized frames at the same resolution.
        if (p.Engine == FpEngine)
        {
            // Feature-preserving: derives ONE global palette across all frames, so 'palette'/'final_method' are unused.
            g[Nodes.Quantize] = new PixelQuantizeFP
            {
                Image = frames,
                GridW = gw,
                GridH = gh,
                VirtualResolution = p.VirtualResolution,
                Thicken = QuantizeGuards.Req(p.Thicken, WorkflowParamKeys.Thicken),
                Tau = QuantizeGuards.Req(p.Tau, WorkflowParamKeys.Tau),
                Lam = QuantizeGuards.Req(p.Lam, WorkflowParamKeys.Lam),
                K = QuantizeGuards.Req(p.K, WorkflowParamKeys.K),
                Beta = QuantizeGuards.Req(p.Beta, WorkflowParamKeys.Beta),
                Step = QuantizeGuards.Req(p.Step, WorkflowParamKeys.Step),
            };
        }
        else
        {
            g[Nodes.Quantize] = new PixelQuantize
            {
                Image = frames,
                GridW = gw,
                GridH = gh,
                Palette = QuantizeGuards.Req(p.Palette, WorkflowParamKeys.Palette),
                Method = QuantizeGuards.Req(p.FinalMethod, WorkflowParamKeys.FinalMethod),
                VirtualResolution = p.VirtualResolution,
            };
        }
        // Keep the source clip's frame rate by default (GetVideoComponents output 2); an explicit fps>0 overrides it.
        // Keyed output must be LOSSLESS so the alpha channel survives the webp encode (as the matte/deflicker passes do).
        double fps = p.Fps;
        int quality = key ? 100 : 80;
        g[Nodes.Save] = fps > 0
            ? new SaveAnimatedWEBPFixedFps
            {
                Images = PixelQuantize.Out(Nodes.Quantize),
                FilenamePrefix = OutputPrefixes.Edit,
                Fps = fps,
                Lossless = key,
                Quality = quality,
                Method = ComfyWidgets.WebpMethod.Default,
            }
            : new SaveAnimatedWEBP
            {
                Images = PixelQuantize.Out(Nodes.Quantize),
                FilenamePrefix = OutputPrefixes.Edit,
                Fps = GetVideoComponents.FpsOut(Nodes.Frames),
                Lossless = key,
                Quality = quality,
                Method = ComfyWidgets.WebpMethod.Default,
            };
        return g;
    }
}

/// <summary>Video pixel-quantizer parameters — the grid/virtual-resolution snap, the output frame rate, the engine
/// selector, and the feature-preserving engine knobs. The always-read values (<c>virtual_resolution</c>/<c>grid_w</c>/
/// <c>grid_h</c>/<c>fps</c>/<c>engine</c>) are <c>required</c>; the branch-only knobs (<c>palette</c>/<c>final_method</c>
/// for median, <c>thicken</c>…<c>step</c> for fp, <c>matte_threshold</c> for keying) are nullable and guarded in their
/// branch with <c>QuantizeGuards.Req</c> (the old <c>*Req</c> throw); <c>key_background</c> is a defaulted bool.</summary>
public sealed record PixelQuantizeVideoParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)]                                        public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)]                                        public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)]                                        public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]
    [Range(0.0, 60.0)]                                      public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Engine)]            public required string Engine { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)]           public string? Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)]       public string? FinalMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Thicken)]
    [Range(0.0, 8.0)]                                       public double? Thicken { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Tau)]
    [Range(0.0, 2.0)]                                       public double? Tau { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lam)]
    [Range(0.001, 0.2)]                                     public double? Lam { get; init; }
    [JsonPropertyName(WorkflowParamKeys.K)]
    [Range(2, 128)]                                         public int? K { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Beta)]
    [Range(0.0, 4.0)]                                       public double? Beta { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Step)]
    [Range(1.0, 20.0)]                                      public double? Step { get; init; }
    [JsonPropertyName(WorkflowParamKeys.KeyBackground)]     public bool KeyBackground { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MatteThreshold)]
    [Range(0.0, 1.0)]                                       public double? MatteThreshold { get; init; }
}
