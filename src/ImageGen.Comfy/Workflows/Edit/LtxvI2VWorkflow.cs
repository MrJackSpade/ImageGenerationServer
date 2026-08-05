namespace ImageGen.Comfy;

/// <summary>LTX-Video image-to-video: fast distilled model; source conditions frame 0. LTX has no CLIP in the
/// checkpoint — it loads an external T5.</summary>
public sealed class LtxvI2VWorkflow : EditWorkflowBase
{
    public override string Name => "ltxv-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the node's length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    /// <summary>Own node ids (source LoadImage is the inherited <c>Nodes.Source</c>).</summary>
    private const string T5Loader = "50";
    private const string ScaledSource = "51";
    private const string SourceSize = "52";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string ImgToVideo = "53";
    private const string Conditioning = "54";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out _, out object? vae0);
        model0 = ComfyGraph.ApplyLora(wf, model0, p);   // optional anime-style LoRA on the LTX model
        long seed = ComfyGraph.Seed(p);
        int frames = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        // LTX loads its own external T5 (clip_type "ltxv").
        wf[T5Loader] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "ltxv", device = "default" });
        object ltxClip = ComfyGraph.Ref(T5Loader, 0);
        double budgetMp = 0.39;   // LTX's native i2v megapixel budget — always applied (the source is scaled to it)
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 32 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = ltxClip });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = ltxClip });
        wf[ImgToVideo] = ComfyGraph.Node(ComfyNodeTypes.LTXVImgToVideo, new { positive = ComfyGraph.Ref(Positive, 0), negative = ComfyGraph.Ref(Negative, 0), vae = vae0, image = ComfyGraph.Ref(ScaledSource, 0), width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1), length = frames, batch_size = 1, strength = 1.0 });
        wf[Conditioning] = ComfyGraph.Node(ComfyNodeTypes.LTXVConditioning, new { positive = ComfyGraph.Ref(ImgToVideo, 0), negative = ComfyGraph.Ref(ImgToVideo, 1), frame_rate = fps });
        wf[Scheduler] = ComfyGraph.Node(ComfyNodeTypes.LTXVScheduler, new { steps = p.IntReq(WorkflowParamKeys.Steps), max_shift = 2.05, base_shift = 0.95, stretch = true, terminal = 0.1, latent = ComfyGraph.Ref(ImgToVideo, 2) });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustom, new { model = model0, add_noise = true, noise_seed = seed, cfg = p.DblReq(WorkflowParamKeys.Cfg), positive = ComfyGraph.Ref(Conditioning, 0), negative = ComfyGraph.Ref(Conditioning, 1), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas = ComfyGraph.Ref(Scheduler, 0), latent_image = ComfyGraph.Ref(ImgToVideo, 2) });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
