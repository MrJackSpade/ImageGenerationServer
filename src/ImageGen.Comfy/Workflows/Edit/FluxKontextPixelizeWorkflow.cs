namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on FLUX.1-Kontext. Mirrors the Kontext edit graph (CLIP encode → ReferenceLatent on the
/// source's encoded latent → FluxGuidance), but patches the model with the per-step
/// <c>PixelManifoldProjection</c> so every denoise step clamps the x0 estimate onto a fixed grid+palette,
/// and renders the authoritative output with a final <c>PixelQuantize</c>. Virtual resolution sets the
/// sprite's pixel count independent of Kontext's render bucket.
/// </summary>
public sealed class FluxKontextPixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-kontext";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KontextLike(PixelizeSchema.DefaultPixelPrompt);

    /// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes; FlattenOnWhite owns 11-14
    /// internally).</summary>
    private const string Positive = "60";
    private const string Scale = "62";
    private const string Encode = "63";
    private const string RefLatent = "64";
    private const string Guidance = "65";
    private const string NegativeZero = "66";
    private const string Projection = "35";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Quantize = "36";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // 4/5/6 + LoadImage 10
        var src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (11-14)

        var instruction = p.Str(WorkflowParamKeys.StylePrompt);
        if (string.IsNullOrWhiteSpace(instruction)) instruction = inputs.Positive;
        int gw = p.IntReq(WorkflowParamKeys.GridW);
        int gh = p.IntReq(WorkflowParamKeys.GridH);
        var palette = p.StrReq(WorkflowParamKeys.Palette);
        int vres = p.IntReq(WorkflowParamKeys.VirtualResolution);

        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = instruction, clip = clip0 });
        var snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);   // override the Kontext bucket with the clean k×VRES size when on
        wf[Scale] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : ComfyGraph.Node(ComfyNodeTypes.FluxKontextImageScale, new { image = src });
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(Scale, 0), vae = vae0 });
        wf[RefLatent] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = ComfyGraph.Ref(Positive, 0), latent = ComfyGraph.Ref(Encode, 0) });
        wf[Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(RefLatent, 0), guidance = p.DblReq(WorkflowParamKeys.Guidance) });
        wf[NegativeZero] = ComfyGraph.Node(ComfyNodeTypes.ConditioningZeroOut, new { conditioning = ComfyGraph.Ref(Positive, 0) });

        wf[Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p);
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = PixelSnap.Denoise(p, 0),   // reference% -> denoise; 0 (default) == 1.0 == regenerate from the source ref
            model = ComfyGraph.Ref(Projection, 0),
            positive = ComfyGraph.Ref(Guidance, 0),
            negative = ComfyGraph.Ref(NegativeZero, 0),
            latent_image = ComfyGraph.Ref(Encode, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Quantize] = PixelizeSchema.FinalQuantize(ComfyGraph.Ref(Decode, 0), gw, gh, palette, vres, p);
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Quantize, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
