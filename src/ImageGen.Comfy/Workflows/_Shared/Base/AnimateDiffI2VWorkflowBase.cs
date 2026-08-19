using ImageGen.Application.Rendering;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// SD1.5 AnimateDiff image-to-video that actually animates the SOURCE: SparseCtrl pins frame 0 to the uploaded
/// image, IP-Adapter (PLUS) locks the subject's identity across every frame, and the motion module animates from
/// there (generated from an empty latent so motion is decoupled from source-fidelity — the img2img denoise
/// tradeoff is avoided). Validated in ComfyUI; subpar quality (SD1.5, distilled, 512px native, no hi-res yet) but
/// functional — frame 0 matches the source, the subject moves and stays recognizable. Two motion modules subclass
/// this: AnimateDiff-Lightning and AnimateLCM.
///
/// Requires the ComfyUI custom nodes IPAdapter_plus + AnimateDiff-Evolved + Advanced-ControlNet and the model
/// files (motion module, IP-Adapter PLUS, CLIP-ViT-H, SparseCtrl; AnimateLCM also an LCM LoRA) — all documented in
/// requirements.json. Node ids / wiring mirror the proven prototype exactly.
/// </summary>
public abstract class AnimateDiffI2VWorkflowBase : EditWorkflow<AnimateDiffI2VParams>
{
    public override bool NormalizesSourceResolution => true;
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt is a scene hint, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    /// <summary>AnimateDiff's i2v snap grid for the budget scale (64-px). The megapixel BUDGET is now the per-config
    /// <c>megapixels</c> control (#186), read off the params record — no longer a code const.</summary>
    private const int BudgetSteps = 64;

    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(AnimateDiffI2VParams p) => (p.Megapixels, BudgetSteps);

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema =
    [
        .. EditWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.LcmLora, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.SparsectrlStrength, Type = ParamType.Double, Step = 0.01 },
        new() { Key = WorkflowParamKeys.SparsectrlEnd, Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.IpadapterPreset, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.IpadapterWeight, Type = ParamType.Double, Min = 0.0, Max = 1.5, Step = 0.01, Label = "Identity strength" },
        VideoSizeSchema.Megapixels,
    ];

    protected override ComfyWorkflowGraph Build(AnimateDiffI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = p.Megapixels;   // the per-config i2v megapixel budget (the source is scaled to it)
        string beta = p.BetaSchedule;
        // A requirements-bound motion module wins; otherwise the config's motion_model slot. Refuse (don't emit a null
        // model_name) when neither names one — mirrors the old p.Model contract for this key.
        string motion =
            req.MotionModel is { } reqMotion && !string.IsNullOrWhiteSpace(reqMotion) ? reqMotion
            : p.MotionModel is { } mm && !string.IsNullOrWhiteSpace(mm) ? mm
            : throw new RenderValidationException(
                $"This configuration needs a model for '{WorkflowParamKeys.MotionModel}' and none is set. The configuration should name a slot "
                + "there, and this machine should have a file bound to it.");

        g[EditNodes.Model] = new CheckpointLoaderSimple { CkptName = req.RequiredCheckpoint() };
        Output<Slot.Model> baseModel = CheckpointLoaderSimple.ModelOut(EditNodes.Model);
        Output<Slot.Clip> clip0 = CheckpointLoaderSimple.ClipOut(EditNodes.Model);
        Output<Slot.Vae> vae0 = CheckpointLoaderSimple.VaeOut(EditNodes.Model);
        string? lcmLora = p.LcmLora;
        if (!string.IsNullOrWhiteSpace(lcmLora))   // AnimateLCM: apply the LCM LoRA to the base model to enable lcm sampling
        {
            g[Nodes.LcmLora] = new LoraLoaderModelOnly { Model = CheckpointLoaderSimple.ModelOut(EditNodes.Model), LoraName = lcmLora, StrengthModel = 1.0 };
            baseModel = LoraLoaderModelOnly.Out(Nodes.LcmLora);
        }

        g[EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("AnimateDiff image→video needs a source image, but none was provided.") };
        g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
        g[Nodes.Latent] = new EmptyLatentImageSized { Width = GetImageSize.WidthOut(Nodes.SourceSize), Height = GetImageSize.HeightOut(Nodes.SourceSize), BatchSize = frames };

        g[Nodes.MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = motion };
        g[Nodes.MotionApply] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(Nodes.MotionLoad) };
        g[Nodes.EvolvedSampling] = new ADE_UseEvolvedSampling { Model = baseModel, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(Nodes.MotionApply) };

        // IP-Adapter: UnifiedLoader auto-resolves the IP-Adapter PLUS model + CLIP-ViT-H from the preset, then apply
        // the SOURCE image so the subject's identity carries into every generated frame.
        g[Nodes.IpAdapterLoader] = new IPAdapterUnifiedLoader { Model = ADE_UseEvolvedSampling.Out(Nodes.EvolvedSampling), Preset = p.IpadapterPreset };
        g[Nodes.IpAdapterApply] = new IPAdapter { Model = IPAdapterUnifiedLoader.ModelOut(Nodes.IpAdapterLoader), Ipadapter = IPAdapterUnifiedLoader.IpadapterOut(Nodes.IpAdapterLoader), Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Weight = p.IpadapterWeight, StartAt = 0.0, EndAt = 1.0, WeightType = ComfyWidgets.IpAdapterWeight.Standard };

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };

        // SparseCtrl RGB: condition frame 0 on the source. Strength eased off after the early frames (end_percent)
        // so later frames are free to move instead of freezing on the source.
        g[Nodes.SparseCtrlLoader] = new ACN_SparseCtrlLoaderAdvanced { SparsectrlName = p.SparsectrlName, UseMotion = true, MotionStrength = 1.0, MotionScale = 1.0 };
        g[Nodes.SparseCtrlPreprocess] = new ACN_SparseCtrlRGBPreprocessor { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Vae = vae0, LatentSize = EmptyLatentImageSized.Out(Nodes.Latent) };
        g[Nodes.ControlNetApply] = new ControlNetApplyAdvanced
        {
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            ControlNet = ACN_SparseCtrlLoaderAdvanced.Out(Nodes.SparseCtrlLoader),
            Image = ACN_SparseCtrlRGBPreprocessor.Out(Nodes.SparseCtrlPreprocess),
            Strength = p.SparsectrlStrength,
            StartPercent = 0.0,
            EndPercent = p.SparsectrlEnd,
            Vae = vae0,
        };

        g[Nodes.Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = IPAdapter.Out(Nodes.IpAdapterApply),
            Positive = ControlNetApplyAdvanced.PositiveOut(Nodes.ControlNetApply),
            Negative = ControlNetApplyAdvanced.NegativeOut(Nodes.ControlNetApply),
            LatentImage = EmptyLatentImageSized.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 90, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}

/// <summary>This base's own nodes (Model "4" and Source "10" come from EditWorkflow.Nodes; here node "4" is the
/// CheckpointLoaderSimple and its outputs feed clip/vae directly).</summary>
file static class Nodes
{
    public const string LcmLora = "5";
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string Latent = "7";
    public const string MotionLoad = "20";
    public const string MotionApply = "21";
    public const string EvolvedSampling = "22";
    public const string IpAdapterLoader = "30";
    public const string IpAdapterApply = "31";
    public const string Positive = "13";
    public const string Negative = "12";
    public const string SparseCtrlLoader = "23";
    public const string SparseCtrlPreprocess = "24";
    public const string ControlNetApply = "25";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}

/// <summary>SD1.5 AnimateDiff i2v parameters (shared by the AnimateDiff-Lightning and AnimateLCM subclasses) — the
/// clip length + playback fps, the AnimateDiff <c>beta_schedule</c>, the motion module + optional LCM LoRA, the
/// SparseCtrl-RGB adapter + its strength/end knobs, and the IP-Adapter preset/weight. The <c>*Req</c>/<c>Model()</c>
/// reads are <c>required</c>; <c>motion_model</c> is a nullable model-ref (a requirements binding may supply it instead)
/// and <c>lcm_lora</c> a nullable model-ref (absent = no LoRA); <c>seed</c> is the app's single-sourced seed
/// (defaulted). This base does not use the shared LoadModel head — it loads a plain checkpoint — so <c>loader</c>/
/// <c>weight_dtype</c>/<c>clip_type</c> are not read.</summary>
public sealed record AnimateDiffI2VParams
{
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BetaSchedule)] public required string BetaSchedule { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MotionModel)] public string? MotionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LcmLora)] public string? LcmLora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlName)] public required string SparsectrlName { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlStrength)] public required double SparsectrlStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlEnd)] public required double SparsectrlEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.IpadapterPreset)] public required string IpadapterPreset { get; init; }
    [JsonPropertyName(WorkflowParamKeys.IpadapterWeight)]
    [Range(0.0, 1.5)] public required double IpadapterWeight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
