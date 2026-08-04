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
        new() { Key = "lcm_lora", Type = ParamType.String, IsModelRef = true },                 // null = no LoRA (Lightning); set = AnimateLCM
        // sparsectrl_name is inherited from SharedSchema now (IsModelRef, no Default — a default there would be a
        // filename sitting where a slot id belongs). Only the strength/end knobs are AnimateDiff-i2v-specific.
        new() { Key = "sparsectrl_strength", Type = ParamType.Double },
        new() { Key = "sparsectrl_end", Type = ParamType.Double },
        new() { Key = "ipadapter_preset", Type = ParamType.String },
        new() { Key = "ipadapter_weight", Type = ParamType.Double, Min = 0.0, Max = 1.5, Label = "Identity strength" },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq("length");
        double fps = p.DblReq("fps");
        double budgetMp = 0.39;   // AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        var beta = p.StrReq("beta_schedule");
        var motion = !string.IsNullOrWhiteSpace(req.MotionModel) ? req.MotionModel : p.Model("motion_model");

        wf["4"] = ComfyGraph.Node("CheckpointLoaderSimple", new { ckpt_name = req.RequiredCheckpoint() });
        object baseModel = ComfyGraph.Ref("4", 0);
        var lcmLora = p.Str("lcm_lora");
        if (!string.IsNullOrWhiteSpace(lcmLora))   // AnimateLCM: apply the LCM LoRA to the base model to enable lcm sampling
        {
            wf["5"] = ComfyGraph.Node("LoraLoaderModelOnly", new { model = ComfyGraph.Ref("4", 0), lora_name = lcmLora, strength_model = 1.0 });
            baseModel = ComfyGraph.Ref("5", 0);
        }

        wf["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("AnimateDiff image→video needs a source image, but none was provided.") });
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf["15"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["7"] = ComfyGraph.Node("EmptyLatentImage", new { width = ComfyGraph.Ref("15", 0), height = ComfyGraph.Ref("15", 1), batch_size = frames });

        wf["20"] = ComfyGraph.Node("ADE_LoadAnimateDiffModel", new { model_name = motion });
        wf["21"] = ComfyGraph.Node("ADE_ApplyAnimateDiffModelSimple", new { motion_model = ComfyGraph.Ref("20", 0) });
        wf["22"] = ComfyGraph.Node("ADE_UseEvolvedSampling", new { model = baseModel, beta_schedule = beta, m_models = ComfyGraph.Ref("21", 0) });

        // IP-Adapter: UnifiedLoader auto-resolves the IP-Adapter PLUS model + CLIP-ViT-H from the preset, then apply
        // the SOURCE image so the subject's identity carries into every generated frame.
        wf["30"] = ComfyGraph.Node("IPAdapterUnifiedLoader", new { model = ComfyGraph.Ref("22", 0), preset = p.StrReq("ipadapter_preset") });
        wf["31"] = ComfyGraph.Node("IPAdapter", new { model = ComfyGraph.Ref("30", 0), ipadapter = ComfyGraph.Ref("30", 1), image = ComfyGraph.Ref("11", 0), weight = p.DblReq("ipadapter_weight"), start_at = 0.0, end_at = 1.0, weight_type = "standard" });

        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = ComfyGraph.Ref("4", 1) });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = ComfyGraph.Ref("4", 1) });

        // SparseCtrl RGB: condition frame 0 on the source. Strength eased off after the early frames (end_percent)
        // so later frames are free to move instead of freezing on the source.
        wf["23"] = ComfyGraph.Node("ACN_SparseCtrlLoaderAdvanced", new { sparsectrl_name = p.Model("sparsectrl_name"), use_motion = true, motion_strength = 1.0, motion_scale = 1.0 });
        wf["24"] = ComfyGraph.Node("ACN_SparseCtrlRGBPreprocessor", new { image = ComfyGraph.Ref("11", 0), vae = ComfyGraph.Ref("4", 2), latent_size = ComfyGraph.Ref("7", 0) });
        wf["25"] = ComfyGraph.Node("ControlNetApplyAdvanced", new
        {
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("12", 0),
            control_net = ComfyGraph.Ref("23", 0),
            image = ComfyGraph.Ref("24", 0),
            strength = p.DblReq("sparsectrl_strength"),
            start_percent = 0.0,
            end_percent = p.DblReq("sparsectrl_end"),
            vae = ComfyGraph.Ref("4", 2),
        });

        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed,
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = ComfyGraph.Ref("31", 0),
            positive = ComfyGraph.Ref("25", 0),
            negative = ComfyGraph.Ref("25", 1),
            latent_image = ComfyGraph.Ref("7", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = ComfyGraph.Ref("4", 2) });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 90, method = "default" });
        return wf;
    }
}
