using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>SDXL AnimateDiff i2v via img2img motion. Uses BASE SDXL — the <c>mm_sdxl_v10_beta</c> motion module
/// learned its temporal priors against base SDXL's feature space, so heavily-finetuned SDXL derivatives
/// (Pony/AutismMix lineage) run but produce color-noise instead of motion.</summary>
public sealed class SdxlAnimateDiffWorkflow : EditWorkflow<SdxlAnimateDiffParams>
{
    public override string Name => "sdxl-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    /// <summary>This workflow's own node ids.</summary>
    private const string ScaleSource = "11";
    private const string MotionLoad = "20";
    private const string ApplyMotion = "21";
    private const string EvolvedSampling = "22";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string Encode = "26";
    private const string RepeatLatent = "27";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(SdxlAnimateDiffParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double denoise = p.Denoise;
        double budgetMp = 0.6;   // SDXL AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.MotionModel;
        string beta = p.BetaSchedule;
        g[ScaleSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = budgetMp, ResolutionSteps = 64 };
        g[MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = mm };
        g[ApplyMotion] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(MotionLoad) };
        g[EvolvedSampling] = new ADE_UseEvolvedSampling { Model = model0, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(ApplyMotion) };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Encode] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(ScaleSource), Vae = vae0 };
        g[RepeatLatent] = new RepeatLatentBatch { Samples = VAEEncode.Out(Encode), Amount = frames };
        g[Sampler] = new KSampler { Seed = seed, Steps = p.Steps, Cfg = p.Cfg, SamplerName = ComfyGraph.MapSampler(p.Sampler), Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Denoise = denoise, Model = ADE_UseEvolvedSampling.Out(EvolvedSampling), Positive = CLIPTextEncode.Out(Positive), Negative = CLIPTextEncode.Out(Negative), LatentImage = RepeatLatentBatch.Out(RepeatLatent) };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>SDXL AnimateDiff i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the clip length + playback fps, the motion
/// module, the AnimateDiff <c>beta_schedule</c>, and the img2img <c>denoise</c> (source ↔ motion tradeoff). The
/// <c>*Req</c>/<c>Model()</c> reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings;
/// <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record SdxlAnimateDiffParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]      public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MotionModel)]  public required string MotionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BetaSchedule)] public required string BetaSchedule { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]        public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]          public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
