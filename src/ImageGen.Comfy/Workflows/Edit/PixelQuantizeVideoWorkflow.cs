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
public sealed class PixelQuantizeVideoWorkflow : IWorkflow
{
    public string Name => "pixel-quantize-video";
    public WorkflowKind Kind => WorkflowKind.Edit;
    /// <summary>Outputs an animated WEBP clip.</summary>
    public WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Consumes a video clip (video-to-video) — the one editor that does.</summary>
    public WorkflowMedia SourceMedia => WorkflowMedia.Video;
    /// <summary>No prompt at all — the quantize is purely deterministic.</summary>
    public bool PromptDirectsMotion => false;
    /// <summary>Restyle to a fixed grid+palette — exempt from the no-change gate (also moot for video, which skips it).</summary>
    public bool PreservesComposition => true;
    /// <summary>Pure-CPU quantizer — no checkpoint, must not be hidden by the catalog's no-model guard.</summary>
    public bool RequiresModel => false;
    public IReadOnlyList<ParamSpec> Schema => QuantizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QuantizeSchema = new ParamSpec[]
    {
        // Virtual resolution = the grid's longest edge (aspect from the frame); each frame keeps its input resolution.
        new() { Key = "virtual_resolution", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = "grid_h", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        // A named (locked) palette is the same every frame → temporally consistent. 'adaptive' would re-derive a
        // palette per frame and flicker, so a locked palette is the default for video.
        new() { Key = "palette", Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette", Help = "A locked (named) palette is temporally consistent — no frame-to-frame flicker" },
        new() { Key = "final_method", Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // 0 (default) = keep the source clip's frame rate (wired from GetVideoComponents); >0 overrides it.
        new() { Key = "fps", Type = ParamType.Double, Min = 0, Max = 60, Label = "Output FPS", Help = "0 = keep the source clip's frame rate" },
        // Engine selector. 'median' = the original per-frame PixelQuantize (named/locked palette). 'fp' =
        // PixelQuantizeFP: L0 flatten + XDoG line-thicken + de-AA edge-collapse, then ONE global per-video
        // palette (DIN99d) so it's temporally consistent WITHOUT a named palette (the palette/final_method
        // params are ignored for 'fp'). The fp_* knobs below tune it.
        new() { Key = "engine", Type = ParamType.Enum, Choices = new[] { "median", "fp" }, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + global palette" },
        new() { Key = "thicken", Type = ParamType.Double, Min = 0, Max = 8, Label = "FP line thicken px", Help = "fp engine: XDoG outline thicken (sub-pixel ok)" },
        new() { Key = "tau", Type = ParamType.Double, Min = 0, Max = 2, Label = "FP de-AA tau", Help = "fp engine: edge-collapse plateau/transition threshold" },
        new() { Key = "lam", Type = ParamType.Double, Min = 0.001, Max = 0.2, Label = "FP flatten strength" },
        new() { Key = "k", Type = ParamType.Int, Min = 2, Max = 128, Label = "FP palette k-means" },
        new() { Key = "beta", Type = ParamType.Double, Min = 0, Max = 4, Label = "FP rarity bias" },
        new() { Key = "step", Type = ParamType.Double, Min = 1, Max = 20, Label = "FP DIN99d lattice step" },
        // Key BEFORE pixelizing: matte every frame (BiRefNet) at FULL resolution and feed the RGBA batch straight into
        // the quantizer, which carries the alpha through to a transparent-background clip (saved lossless so it
        // survives). The matte runs INSIDE this graph — the RGBA stays an in-memory tensor, never round-tripping through
        // a webp decode that would drop the alpha. Off = the legacy opaque path.
        new() { Key = "key_background", Type = ParamType.Bool, Label = "Key background", Help = "Matte (BiRefNet) before pixelizing → transparent-background clip (lossless)" },
        new() { Key = "matte_threshold", Type = ParamType.Double, Min = 0, Max = 1, Label = "Matte cutoff", Help = "0 = soft matte (quantizer hard-cuts per cell); >0 = hard BiRefNet cutoff" },
    };

    public Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // Source clip → frames (+ its frame rate). No model head: the quantizer is pure CPU.
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadVideo", new { file = inputs.SourceVideoName ?? throw new RenderValidationException("The video quantizer needs a source clip, but none was provided.") }),
            ["11"] = ComfyGraph.Node("GetVideoComponents", new { video = ComfyGraph.Ref("10", 0) }),
        };
        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        // key_background: matte every frame first, feeding RGBA (subject + alpha) into the quantizer. The BiRefNetMatte
        // node sits between the decoded frames and the quantizer so the alpha stays a tensor (no lossy round-trip).
        bool key = p.Bool("key_background");
        object frames = ComfyGraph.Ref("11", 0);
        if (key)
        {
            wf["15"] = ComfyGraph.Node("BiRefNetMatte", new { image = ComfyGraph.Ref("11", 0), threshold = p.DblReq("matte_threshold") });
            frames = ComfyGraph.Ref("15", 0);
        }
        // Both engines process the whole (N,H,W,C) frame batch and return N quantized frames at the same resolution.
        if (p.StrReq("engine") == "fp")
        {
            // Feature-preserving: derives ONE global palette across all frames, so 'palette'/'final_method' are unused.
            wf["20"] = ComfyGraph.Node("PixelQuantizeFP", new
            {
                image = frames,
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
                image = frames,
                grid_w = gw,
                grid_h = gh,
                palette = p.StrReq("palette"),
                method = p.StrReq("final_method"),
                virtual_resolution = p.IntReq("virtual_resolution"),
            });
        }
        // Keep the source clip's frame rate by default (GetVideoComponents output 2); an explicit fps>0 overrides it.
        // Keyed output must be LOSSLESS so the alpha channel survives the webp encode (as the matte/deflicker passes do).
        double fps = p.DblReq("fps");
        object fpsArg = fps > 0 ? fps : ComfyGraph.Ref("11", 2);
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("20", 0), filename_prefix = "forgemcp_edit", fps = fpsArg, lossless = key, quality = key ? 100 : 80, method = "default" });
        return wf;
    }
}
