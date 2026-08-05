namespace ImageGen.Comfy;

/// <summary>Wan 2.2 TI2V-5B image-to-video: the source image is the first frame; output is an animated WEBP. The
/// text prompt drives the motion/scene.</summary>
public sealed class WanI2VWorkflow : EditWorkflowBase
{
    public override string Name => "wan22-ti2v-5b";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Wan VAE: 4× temporal compression → valid clip lengths are 4n+1 (mirrors the node's length step=4).</summary>
    public override FrameRule? FrameRule => new(1, 4);

    /// <summary>Flow shift. The Wan2.2 repo's ti2v_5B config runs 5.0; without an explicit node ComfyUI silently
    /// applies its own Wan default of 8.0, so the graph pins the reference value.</summary>
    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Shift, Type = ParamType.Double, Min = 1.0, Max = 12.0, Step = 0.1, Label = "Flow shift" },
    }).ToArray();

    /// <summary>This workflow's own node ids.</summary>
    private const string ModelSampling = "30";
    private const string ScaleSource = "11";
    private const string ImageSize = "15";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string Latent = "14";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        model0 = ComfyGraph.ApplyLora(wf, model0, p);   // optional anime-style LoRA (e.g. Flat Color) on the WAN model
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = model0, shift = p.DblReq(WorkflowParamKeys.Shift) });
        model0 = ComfyGraph.Ref(ModelSampling, 0);
        var seed = ComfyGraph.Seed(p);
        int len = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        double budgetMp = 0.9;   // Wan's native i2v megapixel budget — always applied (the source is scaled to it)
        wf[ScaleSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 32 });
        wf[ImageSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaleSource, 0) });
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clip0 });
        wf[Latent] = ComfyGraph.Node(ComfyNodeTypes.Wan22ImageToVideoLatent, new { vae = vae0, width = ComfyGraph.Ref(ImageSize, 0), height = ComfyGraph.Ref(ImageSize, 1), length = len, batch_size = 1, start_image = ComfyGraph.Ref(ScaleSource, 0) });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed,
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref(Positive, 0),
            negative = ComfyGraph.Ref(Negative, 0),
            latent_image = ComfyGraph.Ref(Latent, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
