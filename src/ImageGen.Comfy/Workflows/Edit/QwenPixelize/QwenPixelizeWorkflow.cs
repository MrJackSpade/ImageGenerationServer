using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.QwenPixelize;

/// <summary>
/// Diffusion pixelizer on Qwen-Image-Edit — generate pixel art DIRECTLY from a reference image. QIE's instruction
/// edit (TextEncodeQwenImageEditPlus with the reference as image1 + ReferenceLatent) redraws the image per the
/// instruction while the per-step PixelManifoldProjection clamps the x0 estimate onto the pixel grid+palette every
/// step — so the model and the projection co-operate: QIE produces manifold-friendly structure instead of fighting
/// an already-finished image. Generates at full denoise (QIE-style), not a partial img2img.
///
/// Uses VIRTUAL RESOLUTION (longest-edge virtual-pixel count) by default, so the sprite's pixel count is set
/// independently of whatever bucket QIE renders at. Final PixelQuantize renders the authoritative output. API-only.
/// </summary>
public sealed class QwenPixelizeWorkflow : EditWorkflow<QwenPixelizeParams>
{
    public override string Name => "pixelize-qwen";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => QwenPixelizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QwenPixelizeSchema = new ParamSpec[]
    {
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = ParamBounds.CfgMin, Max = ParamBounds.CfgMax, Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Shift,     Type = ParamType.Double },   // ModelSamplingAuraFlow shift (2511)
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Instruction" },
        // false (default) = GENERATE a new on-character design from the reference (semantic/vision guidance only,
        // empty init) — varies by seed. true = faithful edit-in-place (inject source latent, init from it) = pixelize
        // the same image every time.
        new() { Key = WorkflowParamKeys.Reference, Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        // Virtual resolution = the sprite's pixel count on its longest edge (aspect preserved), independent of the
        // model's render bucket. 0 = use explicit grid_w/grid_h instead.
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW,    Type = ParamType.Int, Min = 0, Max = 4096 },
        new() { Key = WorkflowParamKeys.GridH,    Type = ParamType.Int, Min = 0, Max = 4096 },
        // Snap the render res to a clean integer multiple of VRES (exact k×k cells) within the model's range,
        // overriding the FluxKontextImageScale bucket. Needs width+height (the requested fixed aspect).
        new() { Key = WorkflowParamKeys.Width,           Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render width", Help = "Explicit render width; 0 = model default" },
        new() { Key = WorkflowParamKeys.Height,          Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render height", Help = "Explicit render height; 0 = model default" },
        new() { Key = WorkflowParamKeys.SnapResolution, Type = ParamType.Bool, Label = "Snap res", Help = "Override the render size to a clean integer multiple of VRES" },
        new() { Key = WorkflowParamKeys.OutScale, Type = ParamType.Int, Min = 1, Max = 16, Label = "Output upscale" },
        new() { Key = WorkflowParamKeys.Palette,   Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        new() { Key = WorkflowParamKeys.ProjMethod,  Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = WorkflowParamKeys.WStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.WEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.StartPercent, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.EndPercent,   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.ProjectEvery, Type = ParamType.Int,    Min = 1, Max = 8 },
    };

    protected override ComfyWorkflowGraph Build(QwenPixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // model/clip/vae + LoadImage "10"
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (11-14)

        string instruction = string.IsNullOrWhiteSpace(p.StylePrompt) ? inputs.Positive : p.StylePrompt;

        int gw = p.GridW;
        int gh = p.GridH;
        string palette = p.Palette;
        int vres = p.VirtualResolution;

        // The source enters as a SEMANTIC guide through Qwen's vision encoder (image1). The `reference` % knob sets
        // how much the output references the source pixels: 0 = no reference (empty init latent, no ReferenceLatent →
        // QIE GENERATES a new design each seed); >0 = inject the source latent + ReferenceLatent and img2img it at
        // denoise = 1 - reference/100 (100 ≈ copy). When snapping is on, the sprite renders at the clean k×VRES size.
        (int w, int h)? snap = PixelSnap.Target(req.Resolution, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);
        bool useRef = p.Reference > 0;
        g[Nodes.KontextScale] = new FluxKontextImageScale { Image = src };
        g[Nodes.Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = FluxKontextImageScale.Out(Nodes.KontextScale), Prompt = instruction };
        Output<Slot.Conditioning> cond;
        Output<Slot.Latent> initLatent;
        if (useRef)
        {
            // source-referenced img2img: init from the source latent (snapped to the clean size if enabled). The
            // FixedScale must be its OWN node (25) and referenced — passing the node dict inline as VAEEncode's
            // `pixels` input hands the encoder a dict instead of an image ('dict' has no attribute 'shape').
            Output<Slot.Image> srcPixels;
            if (snap is { } sa) { g[Nodes.SnapScale] = PixelHarnessGraph.FixedScale(src, sa.w, sa.h); srcPixels = ImageScale.Out(Nodes.SnapScale); }
            else srcPixels = FluxKontextImageScale.Out(Nodes.KontextScale);
            g[Nodes.SourceEncode] = new VAEEncode { Pixels = srcPixels, Vae = vae0 };
            g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = TextEncodeQwenImageEditPlus.Out(Nodes.Encode), Latent = VAEEncode.Out(Nodes.SourceEncode) };
            cond = ReferenceLatent.Out(Nodes.RefLatent);
            initLatent = VAEEncode.Out(Nodes.SourceEncode);
        }
        else
        {
            if (snap is { } sl)
                g[Nodes.EmptyLatentNode] = new EmptyLatent(ComfyNodeTypes.EmptySD3LatentImage) { Width = sl.w, Height = sl.h, BatchSize = 1 };
            else
            {
                g[Nodes.ImageSize] = new GetImageSize { Image = FluxKontextImageScale.Out(Nodes.KontextScale) };
                g[Nodes.EmptyLatentNode] = new EmptySD3LatentFromSize { Width = GetImageSize.WidthOut(Nodes.ImageSize), Height = GetImageSize.HeightOut(Nodes.ImageSize), BatchSize = 1 };
            }
            cond = TextEncodeQwenImageEditPlus.Out(Nodes.Encode);
            initLatent = EmptyLatent.Out(Nodes.EmptyLatentNode);
        }
        g[Nodes.ZeroNegative] = new ConditioningZeroOut { Conditioning = cond };

        // Qwen 2511 sampling fix (ModelSamplingAuraFlow + CFGNorm), then patch with the per-step projection.
        g[Nodes.ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = p.Shift };
        g[Nodes.CfgNorm] = new CFGNorm { Model = ModelSamplingAuraFlow.Out(Nodes.ModelSampling), Strength = 1.0 };
        g[Nodes.Projection] = PixelizeSchema.Projection(CFGNorm.Out(Nodes.CfgNorm), vae0, gw, gh, palette, vres, p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);

        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = PixelSnap.Denoise(p.Reference, 0),   // reference% -> denoise; 0 (default) == 1.0 == generate fresh
            Model = PixelManifoldProjection.Out(Nodes.Projection),
            Positive = cond,
            Negative = ConditioningZeroOut.Out(Nodes.ZeroNegative),
            LatentImage = initLatent,
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.FinalQuantize] = PixelizeSchema.FinalQuantize(VAEDecode.Out(Nodes.Decode), gw, gh, palette, vres, p.FinalMethod);
        g[Nodes.Save] = new SaveImage { Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.FinalQuantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
