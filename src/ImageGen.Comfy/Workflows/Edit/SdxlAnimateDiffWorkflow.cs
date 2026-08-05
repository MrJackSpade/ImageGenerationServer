namespace ImageGen.Comfy;

/// <summary>SDXL AnimateDiff i2v via img2img motion. Uses BASE SDXL — the <c>mm_sdxl_v10_beta</c> motion module
/// learned its temporal priors against base SDXL's feature space, so heavily-finetuned SDXL derivatives
/// (Pony/AutismMix lineage) run but produce color-noise instead of motion.</summary>
public sealed class SdxlAnimateDiffWorkflow : EditWorkflowBase
{
    public override string Name => "sdxl-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    /// <summary>This workflow's own node ids.</summary>
    private const string ScaleSource = "11";
    private const string MotionModel = "20";
    private const string ApplyMotion = "21";
    private const string EvolvedSampling = "22";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string Encode = "26";
    private const string RepeatLatent = "27";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);
        long seed = ComfyGraph.Seed(p);
        int frames = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        double denoise = p.DblReq(WorkflowParamKeys.Denoise);
        double budgetMp = 0.6;   // SDXL AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.Model(WorkflowParamKeys.MotionModel);
        string beta = p.StrReq(WorkflowParamKeys.BetaSchedule);
        wf[ScaleSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf[MotionModel] = ComfyGraph.Node(ComfyNodeTypes.ADE_LoadAnimateDiffModel, new { model_name = mm });
        wf[ApplyMotion] = ComfyGraph.Node(ComfyNodeTypes.ADE_ApplyAnimateDiffModelSimple, new { motion_model = ComfyGraph.Ref(MotionModel, 0) });
        wf[EvolvedSampling] = ComfyGraph.Node(ComfyNodeTypes.ADE_UseEvolvedSampling, new { model = model0, beta_schedule = beta, m_models = ComfyGraph.Ref(ApplyMotion, 0) });
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clip0 });
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(ScaleSource, 0), vae = vae0 });
        wf[RepeatLatent] = ComfyGraph.Node(ComfyNodeTypes.RepeatLatentBatch, new { samples = ComfyGraph.Ref(Encode, 0), amount = frames });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new { seed, steps = p.IntReq(WorkflowParamKeys.Steps), cfg = p.DblReq(WorkflowParamKeys.Cfg), sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)), scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), denoise, model = ComfyGraph.Ref(EvolvedSampling, 0), positive = ComfyGraph.Ref(Positive, 0), negative = ComfyGraph.Ref(Negative, 0), latent_image = ComfyGraph.Ref(RepeatLatent, 0) });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
