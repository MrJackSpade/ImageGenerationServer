namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on FLUX.2-Klein 4B. Mirrors the Klein custom-sampler edit graph (ReferenceLatent on the
/// source, BasicGuider + SamplerCustomAdvanced over a fresh Flux.2 latent), with the model patched by the
/// per-step <c>PixelManifoldProjection</c> before the guider and a final <c>PixelQuantize</c> render.
/// </summary>
public sealed class Flux2Klein4bPixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-klein4b";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KleinLike(PixelizeSchema.DefaultPixelPrompt);

    /// <summary>This subclass's own node ids.</summary>
    private const string Positive = "60";
    private const string ScaledImage = "62";
    private const string Encode = "63";
    private const string ImageSize = "64";
    private const string Guidance = "65";
    private const string ReferenceLatent = "66";
    private const string Projection = "35";
    private const string Guider = "22";
    private const string EmptyLatent = "28";
    private const string Scheduler = "29";
    private const string Noise = "20";
    private const string SamplerSelect = "21";
    private const string SplitSigmas = "27";
    private const string Sampler = "23";
    private const string Decode = "8";
    private const string FinalQuantize = "36";
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
        var snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);   // override the megapixels bucket with the clean k×VRES size when on
        wf[ScaledImage] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = src, upscale_method = "lanczos", megapixels = p.DblReq(WorkflowParamKeys.Megapixels), resolution_steps = 64 });
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(ScaledImage, 0), vae = vae0 });
        wf[ImageSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledImage, 0) });
        wf[Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(Positive, 0), guidance = p.DblReq(WorkflowParamKeys.Guidance) });
        wf[ReferenceLatent] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = ComfyGraph.Ref(Guidance, 0), latent = ComfyGraph.Ref(Encode, 0) });

        wf[Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p);
        wf[Guider] = ComfyGraph.Node(ComfyNodeTypes.BasicGuider, new { model = ComfyGraph.Ref(Projection, 0), conditioning = ComfyGraph.Ref(ReferenceLatent, 0) });
        wf[EmptyLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptyFlux2LatentImage, new { width = ComfyGraph.Ref(ImageSize, 0), height = ComfyGraph.Ref(ImageSize, 1), batch_size = 1 });
        wf[Scheduler] = ComfyGraph.Node(ComfyNodeTypes.Flux2Scheduler, new { steps = p.IntReq(WorkflowParamKeys.Steps), width = ComfyGraph.Ref(ImageSize, 0), height = ComfyGraph.Ref(ImageSize, 1) });
        wf[Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = ComfyGraph.Seed(p) });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        // reference% -> img2img: 0 generates from the empty latent over the full schedule; >0 inits from the source
        // latent and runs only the denoise tail (SplitSigmasDenoise low_sigmas = denoise fraction of the steps).
        object sigmas, initLatent;
        if (p.IntReq(WorkflowParamKeys.Reference) > 0)
        {
            wf[SplitSigmas] = ComfyGraph.Node(ComfyNodeTypes.SplitSigmasDenoise, new { sigmas = ComfyGraph.Ref(Scheduler, 0), denoise = PixelSnap.Denoise(p, 0) });
            sigmas = ComfyGraph.Ref(SplitSigmas, 1);        // low_sigmas — the img2img tail
            initLatent = ComfyGraph.Ref(Encode, 0);    // source latent
        }
        else { sigmas = ComfyGraph.Ref(Scheduler, 0); initLatent = ComfyGraph.Ref(EmptyLatent, 0); }
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Noise, 0), guider = ComfyGraph.Ref(Guider, 0), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas, latent_image = initLatent });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[FinalQuantize] = PixelizeSchema.FinalQuantize(ComfyGraph.Ref(Decode, 0), gw, gh, palette, vres, p);
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(FinalQuantize, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
