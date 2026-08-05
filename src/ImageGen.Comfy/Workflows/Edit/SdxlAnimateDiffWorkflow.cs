using System.ComponentModel.DataAnnotations;
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

    protected override ComfyWorkflowGraph Build(SdxlAnimateDiffParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double denoise = p.Denoise;
        double budgetMp = 0.6;   // SDXL AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.MotionModel;
        string beta = p.BetaSchedule;
        g[Nodes.ScaleSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 64 };
        g[Nodes.MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = mm };
        g[Nodes.ApplyMotion] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(Nodes.MotionLoad) };
        g[Nodes.EvolvedSampling] = new ADE_UseEvolvedSampling { Model = model0, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(Nodes.ApplyMotion) };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.Encode] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(Nodes.ScaleSource), Vae = vae0 };
        g[Nodes.RepeatLatent] = new RepeatLatentBatch { Samples = VAEEncode.Out(Nodes.Encode), Amount = frames };
        g[Nodes.Sampler] = new KSampler { Seed = seed, Steps = p.Steps, Cfg = p.Cfg, SamplerName = ComfyGraph.MapSampler(p.Sampler), Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Denoise = denoise, Model = ADE_UseEvolvedSampling.Out(Nodes.EvolvedSampling), Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), LatentImage = RepeatLatentBatch.Out(Nodes.RepeatLatent) };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}

/// <summary>This workflow's own node ids.</summary>
file static class Nodes
{
    public const string ScaleSource = "11";
    public const string MotionLoad = "20";
    public const string ApplyMotion = "21";
    public const string EvolvedSampling = "22";
    public const string Positive = "13";
    public const string Negative = "12";
    public const string Encode = "26";
    public const string RepeatLatent = "27";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
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
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(ParamBounds.DenoiseMin, ParamBounds.DenoiseMax)] public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MotionModel)]  public required string MotionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BetaSchedule)] public required string BetaSchedule { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
