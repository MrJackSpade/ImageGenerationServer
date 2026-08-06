using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.PixelQuantize;

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
public sealed class PixelQuantizeWorkflow : EditWorkflow<PixelQuantizeParams>
{
    public override string Name => "pixel-quantize";
    /// <summary>Restyle to grid+palette — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure CPU quantizer — no checkpoint, must not be hidden by the no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => QuantizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QuantizeSchema =
    [
        // Virtual resolution = the grid's longest edge (aspect from the input); the output keeps the INPUT resolution.
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = WorkflowParamKeys.GridH, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        // "adaptive", an inline hex list ("aabbcc, 112233, ..."), or a bundled name ("chroma-256").
        // The inline path is how a per-character LOCKED palette is fed for frame-to-frame consistency.
        new() { Key = WorkflowParamKeys.Palette, Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        // Named final_method (not method) to match the diffusion pixelizers' final-render param, so the key is shared
        // across every pixelizer and stays in the multi-select intersection panel. (The PixelQuantize node input is
        // still "method" — see Build.)
        new() { Key = WorkflowParamKeys.FinalMethod,  Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // Engine selector, mirroring pixel-quantize-video. 'fp' routes to PixelQuantizeFP (L0 flatten + XDoG
        // thicken + de-AA + DIN99d palette). For a SINGLE frame the fp result is only identical to that frame's
        // whole-batch run when BOTH batch globals are replayed via fp_palette + fp_frequencies below; deriving
        // them from one frame is valid fp but not batch-exact. palette/final_method are ignored for 'fp'.
        new() { Key = WorkflowParamKeys.Engine, Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelEngines, Label = "Engine", Help = "median = named-palette per-frame snap; fp = feature-preserving + global palette" },
        new() { Key = WorkflowParamKeys.Thicken, Type = ParamType.Double, Min = 0, Max = 8, Label = "FP line thicken px", Help = "fp engine: XDoG outline thicken (sub-pixel ok)" },
        new() { Key = WorkflowParamKeys.Tau, Type = ParamType.Double, Min = 0, Max = 2, Label = "FP de-AA tau", Help = "fp engine: edge-collapse plateau/transition threshold" },
        new() { Key = WorkflowParamKeys.Lam, Type = ParamType.Double, Min = 0.001, Max = 0.2, Label = "FP flatten strength" },
        new() { Key = WorkflowParamKeys.K, Type = ParamType.Int, Min = 2, Max = 128, Label = "FP palette k-means" },
        new() { Key = WorkflowParamKeys.Beta, Type = ParamType.Double, Min = 0, Max = 4, Label = "FP rarity bias" },
        new() { Key = WorkflowParamKeys.Step, Type = ParamType.Double, Min = 1, Max = 20, Label = "FP DIN99d lattice step" },
        // fp REPLAY globals from a previous fp run (both emitted in its ui/side-channel and persisted by Forge):
        // fp_palette = inline hex list, fp_frequencies = float list indexed by that palette's ORDER. Empty = derive
        // from this image. Distinct keys from 'palette' (the median-engine named-palette enum) on purpose.
        new() { Key = WorkflowParamKeys.FpPalette, Type = ParamType.String, Label = "FP replay palette", Help = "Inline hex list from a previous fp run; empty = derive" },
        new() { Key = WorkflowParamKeys.FpFrequencies, Type = ParamType.String, Label = "FP replay frequencies", Help = "Float list (palette order) from the same fp run; empty = derive" },
        // Key BEFORE pixelizing: matte the source (BiRefNet) at FULL resolution and feed the RGBA straight into the
        // quantizer, which carries the alpha through and outputs a transparent-background sprite (PNG keeps the alpha).
        // Off = the legacy flatten-onto-white path. On, the flatten is skipped — the matte IS the background handling.
        new() { Key = WorkflowParamKeys.KeyBackground, Type = ParamType.Bool, Label = "Key background", Help = "Matte (BiRefNet) before pixelizing → transparent-background sprite" },
        new() { Key = WorkflowParamKeys.MatteThreshold, Type = ParamType.Double, Min = 0, Max = 1, Label = "Matte cutoff", Help = "0 = soft matte (quantizer hard-cuts per cell); >0 = hard BiRefNet cutoff" },
    ];

    protected override ComfyWorkflowGraph Build(PixelQuantizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // No model head: the quantizer is pure CPU. Source → (matte | flatten-on-white) → quantize → save.
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("The pixel quantizer needs a source image, but none was provided.") },
        };
        // key_background: matte first, feed the RGBA (subject + alpha) into the quantizer at full res. Otherwise the
        // legacy flatten-onto-white (RGBA→RGB) so a transparent source doesn't halo. BiRefNetMatte output 0 = RGBA.
        Output<Slot.Image> src;
        if (p.KeyBackground)
        {
            g[Nodes.Matte] = new global::ImageGen.Comfy.BiRefNetMatte { Image = LoadImage.ImageOut(EditNodes.Source), Threshold = QuantizeGuards.Req(p.MatteThreshold, WorkflowParamKeys.MatteThreshold) };
            src = global::ImageGen.Comfy.BiRefNetMatte.Out(Nodes.Matte);
        }
        else
        {
            src = PixelHarnessGraph.FlattenOnWhite(g);
        }

        int gw = p.GridW;
        int gh = p.GridH;
        // The engine is a contract, not a flag: the concrete params type IS the branch, and its knobs are all present.
        g[Nodes.Quantize] = p switch
        {
            // Feature-preserving engine, same node + knobs as pixel-quantize-video's fp branch, plus the replay
            // globals so a single frame can reproduce its whole-batch result exactly.
            PixelQuantizeFpParams fp => new PixelQuantizeFPReplay
            {
                Image = src,
                GridW = gw,
                GridH = gh,
                VirtualResolution = p.VirtualResolution,
                Thicken = fp.Thicken,
                Tau = fp.Tau,
                Lam = fp.Lam,
                K = fp.K,
                Beta = fp.Beta,
                Step = fp.Step,
                Palette = fp.FpPalette ?? "",
                Frequencies = fp.FpFrequencies ?? "",
            },
            PixelQuantizeMedianParams md => new global::ImageGen.Comfy.PixelQuantize
            {
                Image = src,
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