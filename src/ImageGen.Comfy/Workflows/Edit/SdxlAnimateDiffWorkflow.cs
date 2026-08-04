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

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq("length");
        double fps = p.DblReq("fps");
        double denoise = p.DblReq("denoise");
        double budgetMp = 0.6;   // SDXL AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.Model("motion_model");
        string beta = p.StrReq("beta_schedule");
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf["20"] = ComfyGraph.Node("ADE_LoadAnimateDiffModel", new { model_name = mm });
        wf["21"] = ComfyGraph.Node("ADE_ApplyAnimateDiffModelSimple", new { motion_model = ComfyGraph.Ref("20", 0) });
        wf["22"] = ComfyGraph.Node("ADE_UseEvolvedSampling", new { model = model0, beta_schedule = beta, m_models = ComfyGraph.Ref("21", 0) });
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clip0 });
        wf["26"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("11", 0), vae = vae0 });
        wf["27"] = ComfyGraph.Node("RepeatLatentBatch", new { samples = ComfyGraph.Ref("26", 0), amount = frames });
        wf["3"] = ComfyGraph.Node("KSampler", new { seed, steps = p.IntReq("steps"), cfg = p.DblReq("cfg"), sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")), scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")), denoise, model = ComfyGraph.Ref("22", 0), positive = ComfyGraph.Ref("13", 0), negative = ComfyGraph.Ref("12", 0), latent_image = ComfyGraph.Ref("27", 0) });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
