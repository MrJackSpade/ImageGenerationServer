namespace ImageGen.Comfy;

/// <summary>SD1.5 AnimateDiff + SparseCtrl-RGB: the source conditions frame 0 (faithful anime i2v).</summary>
public sealed class AnimateDiffSd15Workflow : EditWorkflowBase
{
    public override string Name => "animatediff-sd15";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    /// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflowBase.Nodes).</summary>
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

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        double budgetMp = 0.26;   // SD1.5 AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.Model(WorkflowParamKeys.MotionModel);
        string beta = p.StrReq(WorkflowParamKeys.BetaSchedule);
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[MotionLoad] = ComfyGraph.Node(ComfyNodeTypes.ADE_LoadAnimateDiffModel, new { model_name = mm });
        wf[MotionApply] = ComfyGraph.Node(ComfyNodeTypes.ADE_ApplyAnimateDiffModelSimple, new { motion_model = ComfyGraph.Ref(MotionLoad, 0) });
        wf[EvolvedSampling] = ComfyGraph.Node(ComfyNodeTypes.ADE_UseEvolvedSampling, new { model = model0, beta_schedule = beta, m_models = ComfyGraph.Ref(MotionApply, 0) });
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clip0 });
        wf[Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptyLatentImage, new { width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1), batch_size = frames });
        wf[SparseCtrlLoader] = ComfyGraph.Node(ComfyNodeTypes.ACN_SparseCtrlLoaderAdvanced, new { sparsectrl_name = p.Model(WorkflowParamKeys.SparsectrlName), use_motion = true, motion_strength = 1.0, motion_scale = 1.0 });
        wf[SparseCtrlPreprocess] = ComfyGraph.Node(ComfyNodeTypes.ACN_SparseCtrlRGBPreprocessor, new { image = ComfyGraph.Ref(ScaledSource, 0), vae = vae0, latent_size = ComfyGraph.Ref(Latent, 0) });
        wf[ControlNetApply] = ComfyGraph.Node(ComfyNodeTypes.ControlNetApplyAdvanced, new { positive = ComfyGraph.Ref(Positive, 0), negative = ComfyGraph.Ref(Negative, 0), control_net = ComfyGraph.Ref(SparseCtrlLoader, 0), image = ComfyGraph.Ref(SparseCtrlPreprocess, 0), strength = 1.0, start_percent = 0.0, end_percent = 1.0, vae = vae0 });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new { seed, steps = p.IntReq(WorkflowParamKeys.Steps), cfg = p.DblReq(WorkflowParamKeys.Cfg), sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)), scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), denoise = 1.0, model = ComfyGraph.Ref(EvolvedSampling, 0), positive = ComfyGraph.Ref(ControlNetApply, 0), negative = ComfyGraph.Ref(ControlNetApply, 1), latent_image = ComfyGraph.Ref(Latent, 0) });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
