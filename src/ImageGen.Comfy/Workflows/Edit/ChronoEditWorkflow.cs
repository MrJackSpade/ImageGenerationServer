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

    protected override ComfyWorkflowGraph Build(ChronoEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // 4=unet,5=clip(wan),6=vae(wan2.1),10=LoadImage
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);               // distilled LoRA (fast 20-step path)
        long seed = ComfyGraph.Seed(p.Seed);
        int len = p.Length;                                                            // ChronoEdit's short trajectory
        double budgetMp = 0.52;   // ChronoEdit's native ~0.5MP budget (720² ≈ 0.52MP) — always applied (the source is scaled to it)

        // Sampling fix-ups the template applies to the Wan model for ChronoEdit.
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = 5.0 };
        g[Nodes.ScaleRope] = new ScaleROPE { Model = ModelSamplingSD3.Out(Nodes.ModelSampling), ScaleX = 1.0, ShiftX = 0.0, ScaleY = 1.0, ShiftY = 0.0, ScaleT = 1.0, ShiftT = 0.0 };
        Output<Slot.Model> ksModel = ScaleROPE.Out(Nodes.ScaleRope);

        // Source image, scaled to a ~0.5MP budget (preserves aspect; 720² ≈ 0.52MP), reused as both the i2v start
        // frame and the clip-vision input.
        g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 32 };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
        g[Nodes.ClipVisionLoaderNode] = new CLIPVisionLoader { ClipName = p.ClipVision };
        g[Nodes.ClipVisionEncodeNode] = new CLIPVisionEncode { ClipVision = CLIPVisionLoader.Out(Nodes.ClipVisionLoaderNode), Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Crop = ComfyWidgets.Crop.None };

        g[Nodes.PositiveEncode] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.NegativeEncode] = new CLIPTextEncode { Text = Nodes.Negative, Clip = clip0 };

        // Wan2.1 i2v conditioning node: bakes the start image + clip-vision into pos/neg conditioning + the latent.
        g[Nodes.I2VConditioning] = new WanImageToVideo
        {
            Positive = CLIPTextEncode.Out(Nodes.PositiveEncode),
            Negative = CLIPTextEncode.Out(Nodes.NegativeEncode),
            Vae = vae0,
            ClipVisionOutput = CLIPVisionEncode.Out(Nodes.ClipVisionEncodeNode),
            Width = GetImageSize.WidthOut(Nodes.SourceSize),
            Height = GetImageSize.HeightOut(Nodes.SourceSize),
            Length = len,
            BatchSize = 1,
            StartImage = ImageScaleToTotalPixels.Out(Nodes.ScaledSource),
        };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = ksModel,
            Positive = WanImageToVideo.PositiveOut(Nodes.I2VConditioning),
            Negative = WanImageToVideo.NegativeOut(Nodes.I2VConditioning),
            LatentImage = WanImageToVideo.LatentOut(Nodes.I2VConditioning),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        // Keep the LAST frame of the short trajectory as the edited still.
        g[Nodes.LastFrame] = new ImageFromBatch { Image = VAEDecode.Out(Nodes.Decode), BatchIndex = Math.Max(0, len - 1), Length = 1 };
        g[Nodes.Save] = new SaveImage { Images = ImageFromBatch.Out(Nodes.LastFrame), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>This subclass's own node ids (the shared head's Model/Clip/Vae/Source come from EditWorkflow.Nodes);
/// values are the graph-local keys, preserved exactly so the emitted graph stays byte-identical. <c>Negative</c> is
/// Wan's quality/motion negative prompt (same default the Wan i2v path uses).</summary>
file static class Nodes
{
    public const string Negative =
        "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
    public const string ModelSampling = "20";
    public const string ScaleRope = "21";
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string ClipVisionLoaderNode = "30";
    public const string ClipVisionEncodeNode = "31";
    public const string PositiveEncode = "13";
    public const string NegativeEncode = "12";
    public const string I2VConditioning = "14";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string LastFrame = "16";
    public const string Save = "9";
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
