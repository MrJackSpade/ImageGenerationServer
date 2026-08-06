namespace ImageGen.Comfy.Edit.HunyuanVideo15I2V;

/// <summary>HunyuanVideo 1.5 image-to-video (480p cfg-distilled fp8). The model/clip/VAE come from the shared
/// LoadModel head (loader=unet, dual=true, clip_type="hunyuan_video_15" → UNETLoader + the Qwen2.5-VL/byT5
/// DualCLIPLoader + VAELoader). On top: ModelSamplingSD3 flow-shift, a SigCLIP vision encoder that conditions on
/// the source image (CLIPVisionEncode → HunyuanVideo15ImageToVideo's start_image/clip_vision_output), and a
/// BasicScheduler + SamplerCustomAdvanced sampling chain. The 7.8GB fp8 unet + 8.7GB Qwen encoder total ~16.5GB.
/// Uncensored base; animates anime natively. LoRA-aware via ApplyLora. Validated live (shift 7, cfg 1).</summary>
public sealed class HunyuanVideo15I2VWorkflow : EditWorkflow<HunyuanVideo15I2VParams>
{
    public override string Name => "hunyuanvideo15-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = EditWorkflowBase.SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Shift, Type = ParamType.Double, Min = 1.0, Max = 12.0, Label = "Flow shift" },
    }).Concat(HunyuanSr.Schema).ToArray();

    protected override ComfyWorkflowGraph Build(HunyuanVideo15I2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime LoRA on the Hunyuan model
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = p.Shift };
        Output<Slot.Model> modelS = ModelSamplingSD3.Out(Nodes.ModelSampling);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.4;   // HunyuanVideo 1.5's native i2v megapixel budget — always applied (the source is scaled to it)
        g[Nodes.SourceScale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 16 };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.SourceScale) };
        g[Nodes.ClipVisionLoader] = new CLIPVisionLoader { ClipName = p.ClipVision };
        g[Nodes.ClipVisionEncode] = new CLIPVisionEncode { ClipVision = CLIPVisionLoader.Out(Nodes.ClipVisionLoader), Image = ImageScaleToTotalPixels.Out(Nodes.SourceScale), Crop = ComfyWidgets.Crop.Center };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.ImageToVideo] = new HunyuanVideo15ImageToVideo
        {
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            Vae = vae0,
            Width = GetImageSize.WidthOut(Nodes.SourceSize),
            Height = GetImageSize.HeightOut(Nodes.SourceSize),
            Length = frames,
            BatchSize = 1,
            StartImage = ImageScaleToTotalPixels.Out(Nodes.SourceScale),
            ClipVisionOutput = CLIPVisionEncode.Out(Nodes.ClipVisionEncode),
        };
        g[Nodes.Scheduler] = new BasicScheduler { Model = modelS, Scheduler = scheduler, Steps = p.Steps, Denoise = 1.0 };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = sampler };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[Nodes.Guider] = new CFGGuider { Model = modelS, Positive = HunyuanVideo15ImageToVideo.PositiveOut(Nodes.ImageToVideo), Negative = HunyuanVideo15ImageToVideo.NegativeOut(Nodes.ImageToVideo), Cfg = p.RequiredCfg() };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Nodes.Noise), Guider = CFGGuider.Out(Nodes.Guider), Sampler = KSamplerSelect.Out(Nodes.SamplerSelect), Sigmas = BasicScheduler.Out(Nodes.Scheduler), LatentImage = HunyuanVideo15ImageToVideo.LatentOut(Nodes.ImageToVideo) };
        // Optional super-resolution second pass (1080p) — present iff this is the SR contract. Conditioning is the raw
        // text encode (Positive/Negative); the source image (raw LoadImage EditNodes.Source) + SigCLIP vision
        // (ClipVisionEncode) carry over as SR consistency cues.
        IHunyuanSrPass? srPass = HunyuanSr.PassOf(p);
        Output<Slot.Latent> outLatent = srPass is null
            ? SamplerCustomAdvanced.Out(Nodes.Sampler)
            : HunyuanSr.Refine(g, srPass, SamplerCustomAdvanced.Out(Nodes.Sampler), CLIPTextEncode.Out(Nodes.Positive), CLIPTextEncode.Out(Nodes.Negative), vae0, LoadImage.ImageOut(EditNodes.Source), CLIPVisionEncode.Out(Nodes.ClipVisionEncode), sampler, scheduler, seed);
        g[Nodes.Decode] = srPass is not null
            ? new VAEDecodeTiled { Samples = outLatent, Vae = vae0, TileSize = 256, Overlap = 64, TemporalSize = 64, TemporalOverlap = 8 }
            : new VAEDecode { Samples = outLatent, Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = new Output<Slot.Image>(Nodes.Decode, 0), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
