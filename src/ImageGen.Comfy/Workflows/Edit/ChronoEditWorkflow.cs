using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// ChronoEdit-14B instruction image editor (NVIDIA). It's a Wan2.1-I2V backbone repurposed for editing: the source
/// image conditions a very short "trajectory" (a few frames) and we keep the LAST frame as the edited result
/// ("temporal reasoning"). Runs entirely on native ComfyUI nodes — no custom node. Reuses the Wan UMT5 text encoder
/// and the Wan 2.1 VAE, plus the standard CLIP-ViT-H clip-vision. A distilled LoRA enables the fast 20-step/CFG4 path.
/// Mirrors the official <c>image_chrono_edit_14B</c> template.
/// </summary>
public sealed class ChronoEditWorkflow : EditWorkflow<ChronoEditParams>
{
    public override string Name => "chronoedit";

    /// <summary>Wan's quality/motion negative (same default the Wan i2v path uses).</summary>
    private const string Negative =
        "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

    /// <summary>This subclass's own node ids (the shared head's Model/Clip/Vae/Source come from EditWorkflow.Nodes);
    /// values are the graph-local keys, preserved exactly so the emitted graph stays byte-identical.</summary>
    private const string ModelSampling = "20";
    private const string ScaleRope = "21";
    private const string ScaledSource = "11";
    private const string SourceSize = "15";
    private const string ClipVisionLoaderNode = "30";
    private const string ClipVisionEncodeNode = "31";
    private const string PositiveEncode = "13";
    private const string NegativeEncode = "12";
    private const string I2VConditioning = "14";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string LastFrame = "16";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(ChronoEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);   // 4=unet,5=clip(wan),6=vae(wan2.1),10=LoadImage
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);               // distilled LoRA (fast 20-step path)
        long seed = ComfyGraph.Seed(p.Seed);
        int len = p.Length;                                                            // ChronoEdit's short trajectory
        double budgetMp = 0.52;   // ChronoEdit's native ~0.5MP budget (720² ≈ 0.52MP) — always applied (the source is scaled to it)

        // Sampling fix-ups the template applies to the Wan model for ChronoEdit.
        g[ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = 5.0 };
        g[ScaleRope] = new ScaleROPE { Model = ModelSamplingSD3.Out(ModelSampling), ScaleX = 1.0, ShiftX = 0.0, ScaleY = 1.0, ShiftY = 0.0, ScaleT = 1.0, ShiftT = 0.0 };
        Output<Slot.Model> ksModel = ScaleROPE.Out(ScaleRope);

        // Source image, scaled to a ~0.5MP budget (preserves aspect; 720² ≈ 0.52MP), reused as both the i2v start
        // frame and the clip-vision input.
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = budgetMp, ResolutionSteps = 32 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[ClipVisionLoaderNode] = new CLIPVisionLoader { ClipName = p.ClipVision };
        g[ClipVisionEncodeNode] = new CLIPVisionEncode { ClipVision = CLIPVisionLoader.Out(ClipVisionLoaderNode), Image = ImageScaleToTotalPixels.Out(ScaledSource), Crop = "none" };

        g[PositiveEncode] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[NegativeEncode] = new CLIPTextEncode { Text = Negative, Clip = clip0 };

        // Wan2.1 i2v conditioning node: bakes the start image + clip-vision into pos/neg conditioning + the latent.
        g[I2VConditioning] = new WanImageToVideo
        {
            Positive = CLIPTextEncode.Out(PositiveEncode),
            Negative = CLIPTextEncode.Out(NegativeEncode),
            Vae = vae0,
            ClipVisionOutput = CLIPVisionEncode.Out(ClipVisionEncodeNode),
            Width = GetImageSize.WidthOut(SourceSize),
            Height = GetImageSize.HeightOut(SourceSize),
            Length = len,
            BatchSize = 1,
            StartImage = ImageScaleToTotalPixels.Out(ScaledSource),
        };
        g[Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = ksModel,
            Positive = WanImageToVideo.PositiveOut(I2VConditioning),
            Negative = WanImageToVideo.NegativeOut(I2VConditioning),
            LatentImage = WanImageToVideo.LatentOut(I2VConditioning),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        // Keep the LAST frame of the short trajectory as the edited still.
        g[LastFrame] = new ImageFromBatch { Image = VAEDecode.Out(Decode), BatchIndex = Math.Max(0, len - 1), Length = 1 };
        g[Save] = new SaveImage { Images = ImageFromBatch.Out(LastFrame), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>ChronoEdit-14B parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the sampler settings, the trajectory <c>length</c>, and the i2v <c>clip_vision</c>
/// tower. The <c>*Req</c> reads are <c>required</c>; <c>clip_vision</c> is a required model-ref (resolved to a filename
/// in the bag); <c>weight_dtype</c>/<c>clip_type</c> are nullable strings; <c>lora</c> is a nullable model-ref and
/// <c>lora_strength</c> a defaulted double (only read when a LoRA is set); <c>seed</c> is the app's single-sourced
/// seed (defaulted).</summary>
public sealed record ChronoEditParams
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
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipVision)]   public required string ClipVision { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)]         public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
