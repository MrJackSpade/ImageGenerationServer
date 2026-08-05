using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

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
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt is a scene hint, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = EditWorkflowBase.SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.LcmLora, Type = ParamType.String, IsModelRef = true },                 // null = no LoRA (Lightning); set = AnimateLCM
        // sparsectrl_name is inherited from SharedSchema now (IsModelRef, no Default — a default there would be a
        // filename sitting where a slot id belongs). Only the strength/end knobs are AnimateDiff-i2v-specific.
        new() { Key = WorkflowParamKeys.SparsectrlStrength, Type = ParamType.Double, Step = 0.01 },
        new() { Key = WorkflowParamKeys.SparsectrlEnd, Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.IpadapterPreset, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.IpadapterWeight, Type = ParamType.Double, Min = 0.0, Max = 1.5, Step = 0.01, Label = "Identity strength" },
    }).ToArray();

    /// <summary>This base's own nodes (Model "4" and Source "10" come from EditWorkflow.Nodes; here node "4" is the
    /// CheckpointLoaderSimple and its outputs feed clip/vae directly).</summary>
    private const string LcmLora = "5";
    private const string ScaledSource = "11";
    private const string SourceSize = "15";
    private const string Latent = "7";
    private const string MotionLoad = "20";
    private const string MotionApply = "21";
    private const string EvolvedSampling = "22";
    private const string IpAdapterLoader = "30";
    private const string IpAdapterApply = "31";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string SparseCtrlLoader = "23";
    private const string SparseCtrlPreprocess = "24";
    private const string ControlNetApply = "25";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(AnimateDiffI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.39;   // AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string beta = p.BetaSchedule;
        // A requirements-bound motion module wins; otherwise the config's motion_model slot. Refuse (don't emit a null
        // model_name) when neither names one — mirrors the old p.Model contract for this key.
        string motion =
            req.MotionModel is { } reqMotion && !string.IsNullOrWhiteSpace(reqMotion) ? reqMotion
            : p.MotionModel is { } mm && !string.IsNullOrWhiteSpace(mm) ? mm
            : throw new RenderValidationException(
                $"This configuration needs a model for '{WorkflowParamKeys.MotionModel}' and none is set. The configuration should name a slot "
                + "there, and this machine should have a file bound to it.");

        g[Nodes.Model] = new CheckpointLoaderSimple { CkptName = req.RequiredCheckpoint() };
        Output<Slot.Model> baseModel = CheckpointLoaderSimple.ModelOut(Nodes.Model);
        Output<Slot.Clip> clip0 = CheckpointLoaderSimple.ClipOut(Nodes.Model);
        Output<Slot.Vae> vae0 = CheckpointLoaderSimple.VaeOut(Nodes.Model);
        string? lcmLora = p.LcmLora;
        if (!string.IsNullOrWhiteSpace(lcmLora))   // AnimateLCM: apply the LCM LoRA to the base model to enable lcm sampling
        {
            g[LcmLora] = new LoraLoaderModelOnly { Model = CheckpointLoaderSimple.ModelOut(Nodes.Model), LoraName = lcmLora, StrengthModel = 1.0 };
            baseModel = LoraLoaderModelOnly.Out(LcmLora);
        }

        g[Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("AnimateDiff image→video needs a source image, but none was provided.") };
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 64 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[Latent] = new EmptyLatentImageSized { Width = GetImageSize.WidthOut(SourceSize), Height = GetImageSize.HeightOut(SourceSize), BatchSize = frames };

        g[MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = motion };
        g[MotionApply] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(MotionLoad) };
        g[EvolvedSampling] = new ADE_UseEvolvedSampling { Model = baseModel, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(MotionApply) };

        // IP-Adapter: UnifiedLoader auto-resolves the IP-Adapter PLUS model + CLIP-ViT-H from the preset, then apply
        // the SOURCE image so the subject's identity carries into every generated frame.
        g[IpAdapterLoader] = new IPAdapterUnifiedLoader { Model = ADE_UseEvolvedSampling.Out(EvolvedSampling), Preset = p.IpadapterPreset };
        g[IpAdapterApply] = new IPAdapter { Model = IPAdapterUnifiedLoader.ModelOut(IpAdapterLoader), Ipadapter = IPAdapterUnifiedLoader.IpadapterOut(IpAdapterLoader), Image = ImageScaleToTotalPixels.Out(ScaledSource), Weight = p.IpadapterWeight, StartAt = 0.0, EndAt = 1.0, WeightType = ComfyWidgets.IpAdapterWeight.Standard };

        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };

        // SparseCtrl RGB: condition frame 0 on the source. Strength eased off after the early frames (end_percent)
        // so later frames are free to move instead of freezing on the source.
        g[SparseCtrlLoader] = new ACN_SparseCtrlLoaderAdvanced { SparsectrlName = p.SparsectrlName, UseMotion = true, MotionStrength = 1.0, MotionScale = 1.0 };
        g[SparseCtrlPreprocess] = new ACN_SparseCtrlRGBPreprocessor { Image = ImageScaleToTotalPixels.Out(ScaledSource), Vae = vae0, LatentSize = EmptyLatentImageSized.Out(Latent) };
        g[ControlNetApply] = new ControlNetApplyAdvanced
        {
            Positive = CLIPTextEncode.Out(Positive),
            Negative = CLIPTextEncode.Out(Negative),
            ControlNet = ACN_SparseCtrlLoaderAdvanced.Out(SparseCtrlLoader),
            Image = ACN_SparseCtrlRGBPreprocessor.Out(SparseCtrlPreprocess),
            Strength = p.SparsectrlStrength,
            StartPercent = 0.0,
            EndPercent = p.SparsectrlEnd,
            Vae = vae0,
        };

        g[Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = IPAdapter.Out(IpAdapterApply),
            Positive = ControlNetApplyAdvanced.PositiveOut(ControlNetApply),
            Negative = ControlNetApplyAdvanced.NegativeOut(ControlNetApply),
            LatentImage = EmptyLatentImageSized.Out(Latent),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 90, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
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
    [JsonPropertyName(WorkflowParamKeys.Length)]             public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]                public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BetaSchedule)]       public required string BetaSchedule { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MotionModel)]        public string? MotionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LcmLora)]            public string? LcmLora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlName)]     public required string SparsectrlName { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlStrength)] public required double SparsectrlStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SparsectrlEnd)]      public required double SparsectrlEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.IpadapterPreset)]    public required string IpadapterPreset { get; init; }
    [JsonPropertyName(WorkflowParamKeys.IpadapterWeight)]
    [Range(0.0, 1.5)]                                        public required double IpadapterWeight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]      public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]          public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]            public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]          public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]               public long Seed { get; init; }
}
