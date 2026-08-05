namespace ImageGen.Comfy;

/// <summary>HunyuanVideo 1.5 image-to-video (480p cfg-distilled fp8). The model/clip/VAE come from the shared
/// LoadModel head (loader=unet, dual=true, clip_type="hunyuan_video_15" → UNETLoader + the Qwen2.5-VL/byT5
/// DualCLIPLoader + VAELoader). On top: ModelSamplingSD3 flow-shift, a SigCLIP vision encoder that conditions on
/// the source image (CLIPVisionEncode → HunyuanVideo15ImageToVideo's start_image/clip_vision_output), and a
/// BasicScheduler + SamplerCustomAdvanced sampling chain. The 7.8GB fp8 unet + 8.7GB Qwen encoder total ~16.5GB.
/// Uncensored base; animates anime natively. LoRA-aware via ApplyLora. Validated live (shift 7, cfg 1).</summary>
public sealed class HunyuanVideo15I2VWorkflow : EditWorkflowBase
{
    public override string Name => "hunyuanvideo15-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Shift, Type = ParamType.Double, Min = 1.0, Max = 12.0, Label = "Flow shift" },
    }).Concat(HunyuanSr.Schema).ToArray();

    /// <summary>Own node ids (the model/clip/vae/source head is the inherited <c>Nodes</c>).</summary>
    private const string ModelSampling = "30";
    private const string SourceScale = "51";
    private const string SourceSize = "52";
    private const string ClipVisionLoader = "40";
    private const string ClipVisionEncode = "41";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string ImageToVideo = "53";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Noise = "57";
    private const string Guider = "58";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        model0 = ComfyGraph.ApplyLora(wf, model0, p);   // optional anime LoRA on the Hunyuan model
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = model0, shift = p.DblReq(WorkflowParamKeys.Shift) });
        object modelS = ComfyGraph.Ref(ModelSampling, 0);
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        double budgetMp = 0.4;   // HunyuanVideo 1.5's native i2v megapixel budget — always applied (the source is scaled to it)
        wf[SourceScale] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 16 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(SourceScale, 0) });
        wf[ClipVisionLoader] = ComfyGraph.Node(ComfyNodeTypes.CLIPVisionLoader, new { clip_name = p.Model(WorkflowParamKeys.ClipVision) });
        wf[ClipVisionEncode] = ComfyGraph.Node(ComfyNodeTypes.CLIPVisionEncode, new { clip_vision = ComfyGraph.Ref(ClipVisionLoader, 0), image = ComfyGraph.Ref(SourceScale, 0), crop = "center" });
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clip0 });
        wf[ImageToVideo] = ComfyGraph.Node(ComfyNodeTypes.HunyuanVideo15ImageToVideo, new { positive = ComfyGraph.Ref(Positive, 0), negative = ComfyGraph.Ref(Negative, 0), vae = vae0, width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1), length = frames, batch_size = 1, start_image = ComfyGraph.Ref(SourceScale, 0), clip_vision_output = ComfyGraph.Ref(ClipVisionEncode, 0) });
        wf[Scheduler] = ComfyGraph.Node(ComfyNodeTypes.BasicScheduler, new { model = modelS, scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), steps = p.IntReq(WorkflowParamKeys.Steps), denoise = 1.0 });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = seed });
        wf[Guider] = ComfyGraph.Node(ComfyNodeTypes.CFGGuider, new { model = modelS, positive = ComfyGraph.Ref(ImageToVideo, 0), negative = ComfyGraph.Ref(ImageToVideo, 1), cfg = p.DblReq(WorkflowParamKeys.Cfg) });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Noise, 0), guider = ComfyGraph.Ref(Guider, 0), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas = ComfyGraph.Ref(Scheduler, 0), latent_image = ComfyGraph.Ref(ImageToVideo, 2) });
        // Optional super-resolution second pass (1080p). Conditioning is the raw text encode (Positive/Negative); the source
        // image (raw LoadImage Nodes.Source) + SigCLIP vision (ClipVisionEncode) carry over as SR consistency cues. Returns the sampler node unchanged when off.
        object outLatent = HunyuanSr.Refine(wf, p, ComfyGraph.Ref(Sampler, 0), ComfyGraph.Ref(Positive, 0), ComfyGraph.Ref(Negative, 0), vae0, ComfyGraph.Ref(Nodes.Source, 0), ComfyGraph.Ref(ClipVisionEncode, 0), seed);
        wf[Decode] = HunyuanSr.Enabled(p)
            ? ComfyGraph.Node(ComfyNodeTypes.VAEDecodeTiled, new { samples = outLatent, vae = vae0, tile_size = 256, overlap = 64, temporal_size = 64, temporal_overlap = 8 })
            : ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = outLatent, vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
