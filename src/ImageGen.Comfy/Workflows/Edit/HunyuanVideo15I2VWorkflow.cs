using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

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

    protected override ComfyWorkflowGraph Build(HunyuanVideo15I2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime LoRA on the Hunyuan model
        g[ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = p.Shift };
        Output<Slot.Model> modelS = ModelSamplingSD3.Out(ModelSampling);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.4;   // HunyuanVideo 1.5's native i2v megapixel budget — always applied (the source is scaled to it)
        g[SourceScale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 16 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(SourceScale) };
        g[ClipVisionLoader] = new CLIPVisionLoader { ClipName = p.ClipVision };
        g[ClipVisionEncode] = new CLIPVisionEncode { ClipVision = CLIPVisionLoader.Out(ClipVisionLoader), Image = ImageScaleToTotalPixels.Out(SourceScale), Crop = ComfyWidgets.Crop.Center };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[ImageToVideo] = new HunyuanVideo15ImageToVideo
        {
            Positive = CLIPTextEncode.Out(Positive),
            Negative = CLIPTextEncode.Out(Negative),
            Vae = vae0,
            Width = GetImageSize.WidthOut(SourceSize),
            Height = GetImageSize.HeightOut(SourceSize),
            Length = frames,
            BatchSize = 1,
            StartImage = ImageScaleToTotalPixels.Out(SourceScale),
            ClipVisionOutput = CLIPVisionEncode.Out(ClipVisionEncode),
        };
        g[Scheduler] = new BasicScheduler { Model = modelS, Scheduler = scheduler, Steps = p.Steps, Denoise = 1.0 };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = sampler };
        g[Noise] = new RandomNoise { NoiseSeed = seed };
        g[Guider] = new CFGGuider { Model = modelS, Positive = HunyuanVideo15ImageToVideo.PositiveOut(ImageToVideo), Negative = HunyuanVideo15ImageToVideo.NegativeOut(ImageToVideo), Cfg = p.RequiredCfg() };
        g[Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Noise), Guider = CFGGuider.Out(Guider), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = BasicScheduler.Out(Scheduler), LatentImage = HunyuanVideo15ImageToVideo.LatentOut(ImageToVideo) };
        // Optional super-resolution second pass (1080p). Conditioning is the raw text encode (Positive/Negative); the source
        // image (raw LoadImage Nodes.Source) + SigCLIP vision (ClipVisionEncode) carry over as SR consistency cues. Returns the sampler node unchanged when off.
        Output<Slot.Latent> outLatent = HunyuanSr.Refine(g, p, SamplerCustomAdvanced.Out(Sampler), CLIPTextEncode.Out(Positive), CLIPTextEncode.Out(Negative), vae0, LoadImage.ImageOut(Nodes.Source), CLIPVisionEncode.Out(ClipVisionEncode), sampler, scheduler, seed);
        g[Decode] = HunyuanSr.Enabled(p)
            ? new VAEDecodeTiled { Samples = outLatent, Vae = vae0, TileSize = 256, Overlap = 64, TemporalSize = 64, TemporalOverlap = 8 }
            : new VAEDecode { Samples = outLatent, Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = new Output<Slot.Image>(Decode, 0), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}

/// <summary>HunyuanVideo 1.5 image→video parameters — the shared loader-head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings + flow <c>shift</c>, the SigCLIP vision encoder
/// (<c>clip_vision</c>, a resolved model ref), the clip <c>length</c> + playback <c>fps</c>, an optional preset LoRA, and
/// the optional super-resolution second pass exposed through <see cref="IHunyuanSrParams"/> (all <c>sr_*</c> nullable —
/// a non-SR config supplies none). The <c>*Req</c>/head reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c>
/// are nullable, <c>lora</c> a nullable model-ref with a defaulted <c>lora_strength</c>, <c>seed</c> the single-sourced
/// seed. <c>cfg</c> is nullable-with-throw (mirrors the shared txt2img contract; always present in the i2v configs).</summary>
public sealed record HunyuanVideo15I2VParams : IHunyuanSrParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]
    [AllowNullable("null = the config didn't set CFG; RequiredCfg() refuses an absent value (this real-CFG guider always has it in the i2v configs), distinct from a real 0")] public double? Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]
    [Range(1.0, 12.0)]                                 public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipVision)]   public required string ClipVision { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)]         public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sr)]           public bool Sr { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrModel)]      public string? SrModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrUpsampler)]  public string? SrUpsampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrWidth)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public int? SrWidth { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrHeight)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public int? SrHeight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrSteps)]
    [Range(1, 50)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public int? SrSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrDenoise)]
    [Range(ParamBounds.DenoiseMin, ParamBounds.DenoiseMax)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public double? SrDenoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrNoiseAug)]
    [Range(0.0, 1.0)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public double? SrNoiseAug { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrCfg)]
    [Range(1.0, 12.0)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public double? SrCfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrShift)]
    [Range(1.0, 12.0)]
    [AllowNullable("null = the config didn't enable/parameterize the super-resolution pass; the SR node input is emitted only when set, distinct from a real 0")] public double? SrShift { get; init; }

    /// <summary>CFG, required by this graph's real-CFG guider — the base's nullable <c>cfg</c>, or a refusal naming it
    /// (the typed form of <c>DblReq(cfg)</c>).</summary>
    public double RequiredCfg() => Cfg ?? throw new RenderValidationException(
        $"This configuration needs a value for '{WorkflowParamKeys.Cfg}' and none is set. It must supply one — there is no default.");
}
