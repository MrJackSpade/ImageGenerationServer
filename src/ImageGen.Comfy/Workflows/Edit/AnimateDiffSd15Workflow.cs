using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>SD1.5 AnimateDiff + SparseCtrl-RGB: the source conditions frame 0 (faithful anime i2v).</summary>
public sealed class AnimateDiffSd15Workflow : EditWorkflow<AnimateDiffSd15Params>
{
    public override string Name => "animatediff-sd15";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    /// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflow.Nodes).</summary>
    private const string ScaledSource = "11";
    private const string SourceSize = "15";
    private const string MotionLoad = "20";
    private const string MotionApply = "21";
    private const string EvolvedSampling = "22";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string Latent = "7";
    private const string SparseCtrlLoader = "23";
    private const string SparseCtrlPreprocess = "24";
    private const string ControlNetApply = "25";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(AnimateDiffSd15Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.26;   // SD1.5 AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.MotionModel;
        string beta = p.BetaSchedule;
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = budgetMp, ResolutionSteps = 64 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = mm };
        g[MotionApply] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(MotionLoad) };
        g[EvolvedSampling] = new ADE_UseEvolvedSampling { Model = model0, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(MotionApply) };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Latent] = new EmptyLatentImageSized { Width = GetImageSize.WidthOut(SourceSize), Height = GetImageSize.HeightOut(SourceSize), BatchSize = frames };
        g[SparseCtrlLoader] = new ACN_SparseCtrlLoaderAdvanced { SparsectrlName = p.SparsectrlName, UseMotion = true, MotionStrength = 1.0, MotionScale = 1.0 };
        g[SparseCtrlPreprocess] = new ACN_SparseCtrlRGBPreprocessor { Image = ImageScaleToTotalPixels.Out(ScaledSource), Vae = vae0, LatentSize = EmptyLatentImageSized.Out(Latent) };
        g[ControlNetApply] = new ControlNetApplyAdvanced { Positive = CLIPTextEncode.Out(Positive), Negative = CLIPTextEncode.Out(Negative), ControlNet = ACN_SparseCtrlLoaderAdvanced.Out(SparseCtrlLoader), Image = ACN_SparseCtrlRGBPreprocessor.Out(SparseCtrlPreprocess), Strength = 1.0, StartPercent = 0.0, EndPercent = 1.0, Vae = vae0 };
        g[Sampler] = new KSampler { Seed = seed, Steps = p.Steps, Cfg = p.Cfg, SamplerName = ComfyGraph.MapSampler(p.Sampler), Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Denoise = 1.0, Model = ADE_UseEvolvedSampling.Out(EvolvedSampling), Positive = ControlNetApplyAdvanced.PositiveOut(ControlNetApply), Negative = ControlNetApplyAdvanced.NegativeOut(ControlNetApply), LatentImage = EmptyLatentImageSized.Out(Latent) };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>SD1.5 AnimateDiff i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the clip length + playback fps, the motion
/// module, the AnimateDiff <c>beta_schedule</c>, and the SparseCtrl-RGB adapter. The <c>*Req</c>/<c>Model()</c> reads
/// are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings; <c>seed</c> is the app's
/// single-sourced seed (defaulted).</summary>
public sealed record AnimateDiffSd15Params
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]        public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]   public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]      public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]        public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]           public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MotionModel)]   public required string MotionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BetaSchedule)]  public required string BetaSchedule { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlName)] public required string SparsectrlName { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]     public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]       public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]     public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]          public long Seed { get; init; }
}
