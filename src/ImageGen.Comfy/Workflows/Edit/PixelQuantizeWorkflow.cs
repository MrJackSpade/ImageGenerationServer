using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Deterministic pixel-art quantizer — the model-free half of the pixelizer. Snaps the source image
/// onto a fixed grid + palette (OKLab nearest, mode-per-cell) via the <c>PixelQuantize</c> ComfyUI
/// node (ComfyUI-PixelHarness). No model, no VRAM: just LoadImage → PixelQuantize → SaveImage, so a
/// quantize job costs effectively nothing and never blocks a real generation.
///
/// This is BOTH the standalone frame/still pixelizer and (with block-render after a VAE decode) the
/// authoritative final renderer. API-only: its configuration ships <c>visible:false</c> so it never
/// appears in the UI edit dropdown; the orchestrator submits it by id via /forge/edit.
///
/// The diffusion projection sibling (PixelizeWorkflow, Flux-dev + per-step PixelManifoldProjection)
/// reuses the same quantizer math for its projection target.
/// </summary>
public sealed class PixelQuantizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixel-quantize";
    /// <summary>Restyle to grid+palette — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure CPU quantizer — no checkpoint, must not be hidden by the no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => QuantizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QuantizeSchema = new ParamSpec[]
    {
        // Virtual resolution = the grid's longest edge (aspect from the input); the output keeps the INPUT resolution.
        new() { Key = "virtual_resolution", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = "grid_h", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        // "adaptive", an inline hex list ("aabbcc, 112233, ..."), or a bundled name ("chroma-256").
        // The inline path is how a per-character LOCKED palette is fed for frame-to-frame consistency.
        new() { Key = "palette", Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        // Named final_method (not method) to match the diffusion pixelizers' final-render param, so the key is shared
        // across every pixelizer and stays in the multi-select intersection panel. (The PixelQuantize node input is
        // still "method" — see Build.)
        new() { Key = "final_method",  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // Engine selector, mirroring pixel-quantize-video. 'fp' routes to PixelQuantizeFP (L0 flatten + XDoG
        // thicken + de-AA + DIN99d palette). For a SINGLE frame the fp result is only identical to that frame's
        // whole-batch run when BOTH batch globals are replayed via fp_palette + fp_frequencies below; deriving
        // them from one frame is valid fp but not batch-exact. palette/final_method are ignored for 'fp'.
        new() { Key = "engine", Type = ParamType.Enum, Choices = new[] { "median", "fp" }, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + global palette" },
        new() { Key = "thicken", Type = ParamType.Double, Min = 0, Max = 8, Label = "FP line thicken px", Help = "fp engine: XDoG outline thicken (sub-pixel ok)" },
        new() { Key = "tau", Type = ParamType.Double, Min = 0, Max = 2, Label = "FP de-AA tau", Help = "fp engine: edge-collapse plateau/transition threshold" },
        new() { Key = "lam", Type = ParamType.Double, Min = 0.001, Max = 0.2, Label = "FP flatten strength" },
        new() { Key = "k", Type = ParamType.Int, Min = 2, Max = 128, Label = "FP palette k-means" },
        new() { Key = "beta", Type = ParamType.Double, Min = 0, Max = 4, Label = "FP rarity bias" },
        new() { Key = "step", Type = ParamType.Double, Min = 1, Max = 20, Label = "FP DIN99d lattice step" },
        // fp REPLAY globals from a previous fp run (both emitted in its ui/side-channel and persisted by Forge):
        // fp_palette = inline hex list, fp_frequencies = float list indexed by that palette's ORDER. Empty = derive
        // from this image. Distinct keys from 'palette' (the median-engine named-palette enum) on purpose.
        new() { Key = "fp_palette", Type = ParamType.String, Label = "FP replay palette", Help = "Inline hex list from a previous fp run; empty = derive" },
        new() { Key = "fp_frequencies", Type = ParamType.String, Label = "FP replay frequencies", Help = "Float list (palette order) from the same fp run; empty = derive" },
        // Key BEFORE pixelizing: matte the source (BiRefNet) at FULL resolution and feed the RGBA straight into the
        // quantizer, which carries the alpha through and outputs a transparent-background sprite (PNG keeps the alpha).
        // Off = the legacy flatten-onto-white path. On, the flatten is skipped — the matte IS the background handling.
        new() { Key = "key_background", Type = ParamType.Bool, Label = "Key background", Help = "Matte (BiRefNet) before pixelizing → transparent-background sprite" },
        new() { Key = "matte_threshold", Type = ParamType.Double, Min = 0, Max = 1, Label = "Matte cutoff", Help = "0 = soft matte (quantizer hard-cuts per cell); >0 = hard BiRefNet cutoff" },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // No model head: the quantizer is pure CPU. Source → (matte | flatten-on-white) → quantize → save.
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("The pixel quantizer needs a source image, but none was provided.") }),
        };
        // key_background: matte first, feed the RGBA (subject + alpha) into the quantizer at full res. Otherwise the
        // legacy flatten-onto-white (RGBA→RGB) so a transparent source doesn't halo. BiRefNetMatte output 0 = RGBA.
        bool key = p.Bool("key_background");
        object src;
        if (key)
        {
            wf["15"] = ComfyGraph.Node("BiRefNetMatte", new { image = ComfyGraph.Ref("10", 0), threshold = p.DblReq("matte_threshold") });
            src = ComfyGraph.Ref("15", 0);
        }
        else
        {
            src = PixelHarnessGraph.FlattenOnWhite(wf);
        }
        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        if (p.StrReq("engine") == "fp")
        {
            // Feature-preserving engine, same node + knobs as pixel-quantize-video's fp branch, plus the replay
            // globals so a single frame can reproduce its whole-batch result exactly.
            wf["20"] = ComfyGraph.Node("PixelQuantizeFP", new
            {
                image = src,
                grid_w = gw,
                grid_h = gh,
                virtual_resolution = p.IntReq("virtual_resolution"),
                thicken = p.DblReq("thicken"),
                tau = p.DblReq("tau"),
                lam = p.DblReq("lam"),
                k = p.IntReq("k"),
                beta = p.DblReq("beta"),
                step = p.DblReq("step"),
                palette = p.Str("fp_palette") ?? "",
                frequencies = p.Str("fp_frequencies") ?? "",
            });
        }
        else
        {
            wf["20"] = ComfyGraph.Node("PixelQuantize", new
            {
                image = src,
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
