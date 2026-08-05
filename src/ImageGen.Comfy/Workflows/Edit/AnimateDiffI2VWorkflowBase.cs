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
public abstract class AnimateDiffI2VWorkflowBase : EditWorkflowBase
{
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt is a scene hint, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.LcmLora, Type = ParamType.String, IsModelRef = true },                 // null = no LoRA (Lightning); set = AnimateLCM
        // sparsectrl_name is inherited from SharedSchema now (IsModelRef, no Default — a default there would be a
        // filename sitting where a slot id belongs). Only the strength/end knobs are AnimateDiff-i2v-specific.
        new() { Key = WorkflowParamKeys.SparsectrlStrength, Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.SparsectrlEnd, Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.IpadapterPreset, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.IpadapterWeight, Type = ParamType.Double, Min = 0.0, Max = 1.5, Label = "Identity strength" },
    }).ToArray();

    /// <summary>This base's own nodes (Model "4" and Source "10" come from EditWorkflowBase.Nodes; here node "4" is the
    /// CheckpointLoaderSimple and its outputs feed clip/vae directly).</summary>
    private const string LcmLora = "5";
    private const string ScaledSource = "11";
    private const string SourceSize = "15";
    private const string Latent = "7";
    private const string MotionLoad = "20";
    private const string MotionApply = "21";
    private const string EvolvedSampling = "22";
    private const string IpAdapterLoader = "30";
    private const string IpAdapter = "31";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string SparseCtrlLoader = "23";
    private const string SparseCtrlPreprocess = "24";
    private const string ControlNetApply = "25";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        double budgetMp = 0.39;   // AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        var beta = p.StrReq(WorkflowParamKeys.BetaSchedule);
        var motion = !string.IsNullOrWhiteSpace(req.MotionModel) ? req.MotionModel : p.Model(WorkflowParamKeys.MotionModel);

        wf[Nodes.Model] = ComfyGraph.Node(ComfyNodeTypes.CheckpointLoaderSimple, new { ckpt_name = req.RequiredCheckpoint() });
        object baseModel = ComfyGraph.Ref(Nodes.Model, 0);
        var lcmLora = p.Str(WorkflowParamKeys.LcmLora);
        if (!string.IsNullOrWhiteSpace(lcmLora))   // AnimateLCM: apply the LCM LoRA to the base model to enable lcm sampling
        {
            wf[LcmLora] = ComfyGraph.Node(ComfyNodeTypes.LoraLoaderModelOnly, new { model = ComfyGraph.Ref(Nodes.Model, 0), lora_name = lcmLora, strength_model = 1.0 });
            baseModel = ComfyGraph.Ref(LcmLora, 0);
        }

        wf[Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("AnimateDiff image→video needs a source image, but none was provided.") });
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptyLatentImage, new { width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1), batch_size = frames });

        wf[MotionLoad] = ComfyGraph.Node(ComfyNodeTypes.ADE_LoadAnimateDiffModel, new { model_name = motion });
        wf[MotionApply] = ComfyGraph.Node(ComfyNodeTypes.ADE_ApplyAnimateDiffModelSimple, new { motion_model = ComfyGraph.Ref(MotionLoad, 0) });
        wf[EvolvedSampling] = ComfyGraph.Node(ComfyNodeTypes.ADE_UseEvolvedSampling, new { model = baseModel, beta_schedule = beta, m_models = ComfyGraph.Ref(MotionApply, 0) });

        // IP-Adapter: UnifiedLoader auto-resolves the IP-Adapter PLUS model + CLIP-ViT-H from the preset, then apply
        // the SOURCE image so the subject's identity carries into every generated frame.
        wf[IpAdapterLoader] = ComfyGraph.Node(ComfyNodeTypes.IPAdapterUnifiedLoader, new { model = ComfyGraph.Ref(EvolvedSampling, 0), preset = p.StrReq(WorkflowParamKeys.IpadapterPreset) });
        wf[IpAdapter] = ComfyGraph.Node(ComfyNodeTypes.IPAdapter, new { model = ComfyGraph.Ref(IpAdapterLoader, 0), ipadapter = ComfyGraph.Ref(IpAdapterLoader, 1), image = ComfyGraph.Ref(ScaledSource, 0), weight = p.DblReq(WorkflowParamKeys.IpadapterWeight), start_at = 0.0, end_at = 1.0, weight_type = "standard" });

        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = ComfyGraph.Ref(Nodes.Model, 1) });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = ComfyGraph.Ref(Nodes.Model, 1) });

        // SparseCtrl RGB: condition frame 0 on the source. Strength eased off after the early frames (end_percent)
        // so later frames are free to move instead of freezing on the source.
        wf[SparseCtrlLoader] = ComfyGraph.Node(ComfyNodeTypes.ACN_SparseCtrlLoaderAdvanced, new { sparsectrl_name = p.Model(WorkflowParamKeys.SparsectrlName), use_motion = true, motion_strength = 1.0, motion_scale = 1.0 });
        wf[SparseCtrlPreprocess] = ComfyGraph.Node(ComfyNodeTypes.ACN_SparseCtrlRGBPreprocessor, new { image = ComfyGraph.Ref(ScaledSource, 0), vae = ComfyGraph.Ref(Nodes.Model, 2), latent_size = ComfyGraph.Ref(Latent, 0) });
        wf[ControlNetApply] = ComfyGraph.Node(ComfyNodeTypes.ControlNetApplyAdvanced, new
        {
            positive = ComfyGraph.Ref(Positive, 0),
            negative = ComfyGraph.Ref(Negative, 0),
            control_net = ComfyGraph.Ref(SparseCtrlLoader, 0),
            image = ComfyGraph.Ref(SparseCtrlPreprocess, 0),
            strength = p.DblReq(WorkflowParamKeys.SparsectrlStrength),
            start_percent = 0.0,
            end_percent = p.DblReq(WorkflowParamKeys.SparsectrlEnd),
            vae = ComfyGraph.Ref(Nodes.Model, 2),
        });

        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed,
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = ComfyGraph.Ref(IpAdapter, 0),
            positive = ComfyGraph.Ref(ControlNetApply, 0),
            negative = ComfyGraph.Ref(ControlNetApply, 1),
            latent_image = ComfyGraph.Ref(Latent, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = ComfyGraph.Ref(Nodes.Model, 2) });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 90, method = "default" });
        return wf;
    }
}
