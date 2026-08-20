namespace ImageGen.Comfy.Edit.Pixelize;

/// <summary>
/// Diffusion pixelizer — the quality half. Img2img at a partial denoise where the model (Flux-dev by default; the
/// neutral high-fidelity denoiser, NOT an authoring model) is patched with the per-step PixelManifoldProjection so
/// every denoise step clamps the x0 estimate onto a fixed grid+palette. The projection does the pixel-art
/// structuring; the model only supplies local coherence — hence low strength + a strong w-ramp. A final PixelQuantize
/// renders the authoritative crisp output so VAE noise never reaches it.
///
/// One class, many configs: any latent img2img model (Flux/SDXL-style) binds here via requirements+params. Qwen
/// Image Edit, which needs its own edit conditioning, will be a sibling class that inserts the same projection patch.
/// The sampler runs at grid*block so the decoded image and the projection target share a resolution. API-only.
/// </summary>
public sealed class PixelizeWorkflow : EditWorkflow<PixelizeParams>
{
    public override bool NormalizesSourceResolution => true;
    public override string Name => "pixelize";
    /// <summary>Restyle to grid+palette — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchemaSpec;

    private static readonly IReadOnlyList<ParamSpec> PixelizeSchemaSpec =
    [
        // model loading (consumed by EditWorkflow.LoadModel)
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        // No default. A GENERIC workflow cannot know which CLIP family a configuration is for; a "flux"
        // default would be silently wrong for any configuration that omits it -- pixelize-hidream would
        // inherit it and hand CLIPLoader a type it does not accept. An omission must surface, not be guessed.
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        // sampling
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = ParamBounds.CfgMin, Max = ParamBounds.CfgMax, Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },   // Flux distilled guidance (omit the node for non-flux)
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        .. SeedParam.Schema,
        // The harness uses a FIXED style prompt for the correction pass (not the edit instruction). Blank it to
        // fall back to the caller's instruction.
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Style prompt" },
        // Low strength: the projection leads, the model only cleans up. The harness Flux default is 0.3.
        new() { Key = WorkflowParamKeys.Reference,  Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        // Virtual resolution = the sprite's pixel count on its longest edge; the grid is derived from the INPUT
        // aspect so output aspect == input aspect. 0 = use explicit grid_w/grid_h. The sampler runs at `megapixels`
        // (aspect-preserving), NOT a fixed grid*block, so a portrait input no longer gets squashed to 3:2.
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW,      Type = ParamType.Int,    Min = 0, Max = 4096 },
        new() { Key = WorkflowParamKeys.GridH,      Type = ParamType.Int,    Min = 0, Max = 4096 },
        new() { Key = WorkflowParamKeys.Megapixels,  Type = ParamType.Double, Min = 0.1, Max = 4.0, Label = "Megapixels", Help = "Working resolution area, aspect preserved (ignored when Snap res is on)" },
        // When snap_resolution is on AND width+height are given, the render res is snapped to a clean integer
        // multiple of VRES (exact k×k cells) within the model's range, overriding the megapixels sizing above.
        new() { Key = WorkflowParamKeys.Width,           Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render width", Help = "Explicit render width; 0 = model default" },
        new() { Key = WorkflowParamKeys.Height,          Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render height", Help = "Explicit render height; 0 = model default" },
        new() { Key = WorkflowParamKeys.SnapResolution, Type = ParamType.Bool, Label = "Snap res", Help = "Override the render size to a clean integer multiple of VRES" },
        new() { Key = WorkflowParamKeys.Palette,     Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        new() { Key = WorkflowParamKeys.ProjMethod, Type = ParamType.Enum,   Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = WorkflowParamKeys.FinalMethod,Type = ParamType.Enum,   Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        // projection weight ramp (over log-sigma progress) + the window it applies in
        new() { Key = WorkflowParamKeys.WStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.WEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.StartPercent, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.EndPercent,   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.ProjectEvery, Type = ParamType.Int,    Min = 1, Max = 8 },
    ];

    /// <summary>The 16-px grid for the megapixels fallback — single source for the scale node and the ETA size.</summary>
    private const int BudgetSteps = 16;

    /// <summary>Renders at the working scale the graph picks — the clean k×VRES snap when snap-resolution is on, else
    /// the megapixels budget — NOT the raw upload dims. The ETA keys on that so pixel-art render time isn't credited by
    /// upload size.</summary>
    protected override (int Width, int Height) EtaRenderSize(PixelizeParams p, ResolvedRequirements req, int sourceWidth, int sourceHeight)
        => PixelSnap.Target(req.Resolution, p.VirtualResolution, p.SnapResolution, p.Width, p.Height, sourceWidth, sourceHeight) is (int w, int h)
            ? (w, h)
            : BudgetScale.Snap(sourceWidth, sourceHeight, p.Megapixels, BudgetSteps);

    protected override ComfyWorkflowGraph Build(PixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // nodes 4/5/6 + LoadImage "10"
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (nodes 11-14)

        int gw = p.GridW;
        int gh = p.GridH;
        int vres = p.VirtualResolution;
        string palette = p.Palette;

        // source image -> working resolution -> init latent. Default: preserve input aspect at a megapixel area
        // (snapped /16). When snapping is on, override with the clean k×VRES render size instead.
        (int w, int h)? snap = PixelSnap.Target(req.Resolution, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);
        g[Nodes.WorkingScale] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : new ImageScaleToTotalPixels { Image = src, UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = p.Megapixels, ResolutionSteps = BudgetSteps };
        g[Nodes.InitEncode] = new VAEEncode { Pixels = ImageScale.Out(Nodes.WorkingScale), Vae = vae0 };

        // conditioning: the harness's fixed style prompt (or the caller's instruction if it's blanked),
        // optional Flux guidance, empty negative (cfg 1 ignores it)
        string prompt = string.IsNullOrWhiteSpace(p.StylePrompt) ? inputs.Positive : p.StylePrompt;
        g[Nodes.Positive] = new CLIPTextEncode { Text = prompt, Clip = clip0 };
        Output<Slot.Conditioning> posSrc = CLIPTextEncode.Out(Nodes.Positive);
        if (p.Guidance is double gd)
        {
            g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = gd };
            posSrc = FluxGuidance.Out(Nodes.Guidance);
        }

        g[Nodes.Negative] = new CLIPTextEncode { Text = "", Clip = clip0 };

        // patch the model with the per-step pixel-manifold projection (the diffusion pixelizer)
        g[Nodes.Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);

        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = PixelSnap.Denoise(p.Reference, 70),   // reference% -> denoise (default 70 → denoise 0.3)
            Model = PixelManifoldProjection.Out(Nodes.Projection),
            Positive = posSrc,
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = VAEEncode.Out(Nodes.InitEncode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        // authoritative final render — quantize the decode so VAE noise never reaches the output
        g[Nodes.FinalQuantize] = PixelizeSchema.FinalQuantize(VAEDecode.Out(Nodes.Decode), gw, gh, palette, vres, p.FinalMethod);
        g[Nodes.Save] = new SaveImage { Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.FinalQuantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
