namespace ImageGen.Comfy;

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
public sealed class QwenPixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-qwen";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => QwenPixelizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QwenPixelizeSchema = new ParamSpec[]
    {
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKinds.Choices },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = 1, Max = 30, Label = "CFG scale" },
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
        new() { Key = WorkflowParamKeys.ProjMethod,  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = WorkflowParamKeys.WStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.WEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.StartPercent, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.EndPercent,   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.ProjectEvery, Type = ParamType.Int,    Min = 1, Max = 8 },
    };

    /// <summary>This workflow's own role-named node ids, atop the inherited edit head and FlattenOnWhite nodes.</summary>
    private const string KontextScale = "20";
    private const string Encode = "22";
    private const string SnapScale = "25";
    private const string SourceEncode = "21";
    private const string RefLatent = "24";
    private const string ImageSize = "40";
    private const string EmptyLatent = "41";
    private const string ZeroNegative = "26";
    private const string ModelSampling = "2";
    private const string CfgNorm = "7";
    private const string Projection = "35";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string FinalQuantize = "36";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);   // model/clip/vae + LoadImage "10"
        object src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (11-14)

        string? instruction = p.Str(WorkflowParamKeys.StylePrompt);
        if (string.IsNullOrWhiteSpace(instruction)) instruction = inputs.Positive;

        int gw = p.IntReq(WorkflowParamKeys.GridW);
        int gh = p.IntReq(WorkflowParamKeys.GridH);
        string palette = p.StrReq(WorkflowParamKeys.Palette);
        int vres = p.IntReq(WorkflowParamKeys.VirtualResolution);

        // The source enters as a SEMANTIC guide through Qwen's vision encoder (image1). The `reference` % knob sets
        // how much the output references the source pixels: 0 = no reference (empty init latent, no ReferenceLatent →
        // QIE GENERATES a new design each seed); >0 = inject the source latent + ReferenceLatent and img2img it at
        // denoise = 1 - reference/100 (100 ≈ copy). When snapping is on, the sprite renders at the clean k×VRES size.
        (int w, int h)? snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);
        bool useRef = p.IntReq(WorkflowParamKeys.Reference) > 0;
        wf[KontextScale] = ComfyGraph.Node(ComfyNodeTypes.FluxKontextImageScale, new { image = src });
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.TextEncodeQwenImageEditPlus, new { clip = clip0, image1 = ComfyGraph.Ref(KontextScale, 0), prompt = instruction });
        object cond, initLatent;
        if (useRef)
        {
            // source-referenced img2img: init from the source latent (snapped to the clean size if enabled). The
            // FixedScale must be its OWN node (25) and referenced — passing the node dict inline as VAEEncode's
            // `pixels` input hands the encoder a dict instead of an image ('dict' has no attribute 'shape').
            object srcPixels;
            if (snap is { } sa) { wf[SnapScale] = PixelHarnessGraph.FixedScale(src, sa.w, sa.h); srcPixels = ComfyGraph.Ref(SnapScale, 0); }
            else srcPixels = ComfyGraph.Ref(KontextScale, 0);
            wf[SourceEncode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = srcPixels, vae = vae0 });
            wf[RefLatent] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = ComfyGraph.Ref(Encode, 0), latent = ComfyGraph.Ref(SourceEncode, 0) });
            cond = ComfyGraph.Ref(RefLatent, 0);
            initLatent = ComfyGraph.Ref(SourceEncode, 0);
        }
        else
        {
            if (snap is { } sl)
                wf[EmptyLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptySD3LatentImage, new { width = sl.w, height = sl.h, batch_size = 1 });
            else
            {
                wf[ImageSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(KontextScale, 0) });
                wf[EmptyLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptySD3LatentImage, new { width = ComfyGraph.Ref(ImageSize, 0), height = ComfyGraph.Ref(ImageSize, 1), batch_size = 1 });
            }
            cond = ComfyGraph.Ref(Encode, 0);
            initLatent = ComfyGraph.Ref(EmptyLatent, 0);
        }
        wf[ZeroNegative] = ComfyGraph.Node(ComfyNodeTypes.ConditioningZeroOut, new { conditioning = cond });

        // Qwen 2511 sampling fix (ModelSamplingAuraFlow + CFGNorm), then patch with the per-step projection.
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingAuraFlow, new { model = model0, shift = p.DblReq(WorkflowParamKeys.Shift) });
        wf[CfgNorm] = ComfyGraph.Node(ComfyNodeTypes.CFGNorm, new { model = ComfyGraph.Ref(ModelSampling, 0), strength = 1.0 });
        wf[Projection] = ComfyGraph.Node(ComfyNodeTypes.PixelManifoldProjection, new
        {
            model = ComfyGraph.Ref(CfgNorm, 0),
            vae = vae0,
            grid_w = gw,
            grid_h = gh,
            palette,
            method = p.StrReq(WorkflowParamKeys.ProjMethod),
            w_start = p.DblReq(WorkflowParamKeys.WStart),
            w_end = p.DblReq(WorkflowParamKeys.WEnd),
            start_percent = p.DblReq(WorkflowParamKeys.StartPercent),
            end_percent = p.DblReq(WorkflowParamKeys.EndPercent),
            project_every = p.IntReq(WorkflowParamKeys.ProjectEvery),
            virtual_resolution = vres,
        });

        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = PixelSnap.Denoise(p, 0),   // reference% -> denoise; 0 (default) == 1.0 == generate fresh
            model = ComfyGraph.Ref(Projection, 0),
            positive = cond,
            negative = ComfyGraph.Ref(ZeroNegative, 0),
            latent_image = initLatent,
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[FinalQuantize] = ComfyGraph.Node(ComfyNodeTypes.PixelQuantize, new
        {
            image = ComfyGraph.Ref(Decode, 0),
            grid_w = gw,
            grid_h = gh,
            palette,
            method = p.StrReq(WorkflowParamKeys.FinalMethod),
            virtual_resolution = vres,
        });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(FinalQuantize, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
