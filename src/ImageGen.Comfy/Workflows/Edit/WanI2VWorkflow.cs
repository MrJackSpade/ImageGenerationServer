using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Wan 2.2 TI2V-5B image-to-video: the source image is the first frame; output is an animated WEBP. The
/// text prompt drives the motion/scene.</summary>
public sealed class WanI2VWorkflow : EditWorkflow<WanI2VParams>
{
    public override string Name => "wan22-ti2v-5b";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Wan VAE: 4× temporal compression → valid clip lengths are 4n+1 (mirrors the node's length step=4).</summary>
    public override FrameRule? FrameRule => new(1, 4);

    /// <summary>Flow shift. The Wan2.2 repo's ti2v_5B config runs 5.0; without an explicit node ComfyUI silently
    /// applies its own Wan default of 8.0, so the graph pins the reference value.</summary>
    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = EditWorkflowBase.SharedSchema.Concat(new ParamSpec[]
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

    protected override ComfyWorkflowGraph Build(WanI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime-style LoRA (e.g. Flat Color) on the WAN model
        g[ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = p.Shift };
        model0 = ModelSamplingSD3.Out(ModelSampling);
        long seed = ComfyGraph.Seed(p.Seed);
        int len = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.9;   // Wan's native i2v megapixel budget — always applied (the source is scaled to it)
        g[ScaleSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = budgetMp, ResolutionSteps = 32 };
        g[ImageSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaleSource) };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Latent] = new Wan22ImageToVideoLatent { Vae = vae0, Width = GetImageSize.WidthOut(ImageSize), Height = GetImageSize.HeightOut(ImageSize), Length = len, BatchSize = 1, StartImage = ImageScaleToTotalPixels.Out(ScaleSource) };
        g[Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = CLIPTextEncode.Out(Positive),
            Negative = CLIPTextEncode.Out(Negative),
            LatentImage = Wan22ImageToVideoLatent.Out(Latent),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>Wan 2.2 TI2V-5B i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the flow <c>shift</c>, the clip length +
/// playback fps, and the optional preset LoRA. The <c>*Req</c> reads are <c>required</c>; <c>weight_dtype</c>/
/// <c>clip_type</c> are nullable strings; <c>lora</c> is a nullable model-ref and <c>lora_strength</c> a defaulted
/// double (only read when a LoRA is set); <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record WanI2VParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]
    [Range(1.0, 12.0)]                                 public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)]         public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
