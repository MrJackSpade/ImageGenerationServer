namespace ImageGen.Comfy;

/// <summary>SD1.5 AnimateDiff + SparseCtrl-RGB: the source conditions frame 0 (faithful anime i2v).</summary>
public sealed class AnimateDiffSd15Workflow : EditWorkflowBase
{
    public override string Name => "animatediff-sd15";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq("length");
        double fps = p.DblReq("fps");
        double budgetMp = 0.26;   // SD1.5 AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.Model("motion_model");
        string beta = p.StrReq("beta_schedule");
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf["15"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["20"] = ComfyGraph.Node("ADE_LoadAnimateDiffModel", new { model_name = mm });
        wf["21"] = ComfyGraph.Node("ADE_ApplyAnimateDiffModelSimple", new { motion_model = ComfyGraph.Ref("20", 0) });
        wf["22"] = ComfyGraph.Node("ADE_UseEvolvedSampling", new { model = model0, beta_schedule = beta, m_models = ComfyGraph.Ref("21", 0) });
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clip0 });
        wf["7"] = ComfyGraph.Node("EmptyLatentImage", new { width = ComfyGraph.Ref("15", 0), height = ComfyGraph.Ref("15", 1), batch_size = frames });
        wf["23"] = ComfyGraph.Node("ACN_SparseCtrlLoaderAdvanced", new { sparsectrl_name = p.Model("sparsectrl_name"), use_motion = true, motion_strength = 1.0, motion_scale = 1.0 });
        wf["24"] = ComfyGraph.Node("ACN_SparseCtrlRGBPreprocessor", new { image = ComfyGraph.Ref("11", 0), vae = vae0, latent_size = ComfyGraph.Ref("7", 0) });
        wf["25"] = ComfyGraph.Node("ControlNetApplyAdvanced", new { positive = ComfyGraph.Ref("13", 0), negative = ComfyGraph.Ref("12", 0), control_net = ComfyGraph.Ref("23", 0), image = ComfyGraph.Ref("24", 0), strength = 1.0, start_percent = 0.0, end_percent = 1.0, vae = vae0 });
        wf["3"] = ComfyGraph.Node("KSampler", new { seed, steps = p.IntReq("steps"), cfg = p.DblReq("cfg"), sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")), scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")), denoise = 1.0, model = ComfyGraph.Ref("22", 0), positive = ComfyGraph.Ref("25", 0), negative = ComfyGraph.Ref("25", 1), latent_image = ComfyGraph.Ref("7", 0) });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
