namespace ImageGen.Comfy;

/// <summary>SDXL AnimateDiff i2v via img2img motion. Uses BASE SDXL — the <c>mm_sdxl_v10_beta</c> motion module
/// learned its temporal priors against base SDXL's feature space, so heavily-finetuned SDXL derivatives
/// (Pony/AutismMix lineage) run but produce color-noise instead of motion. Exact lift of the old
/// <c>animatediff_sdxl</c> branch, repointed to base SDXL.</summary>
public sealed class SdxlAnimateDiffWorkflow : EditWorkflowBase
{
    public override string Name => "sdxl-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    private const string AnimeNegative =
        "worst quality, low quality, blurry, deformed, bad anatomy, extra limbs, watermark, text, realistic, photorealistic, 3d";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        int frames = p.Int("length") > 0 ? p.Int("length") : 16;
        double fps = p.Dbl("fps") > 0 ? p.Dbl("fps") : 8;
        double denoise = p.Dbl("denoise") > 0 ? p.Dbl("denoise") : 0.65;
        double budgetMp = (p.Int("width") > 0 && p.Int("height") > 0) ? (p.Int("width") * (double)p.Int("height")) / 1_000_000.0 : 0.6;
        string mm = p.Model("motion_model");
        // SDXL AnimateDiff REQUIRES the SDXL schedule; autoselect picking otherwise is the color-smear/no-motion bug.
        string beta = string.IsNullOrWhiteSpace(p.Str("beta_schedule")) ? "linear (AnimateDiff-SDXL)" : p.Str("beta_schedule")!;
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 64 });
        wf["20"] = ComfyGraph.Node("ADE_LoadAnimateDiffModel", new { model_name = mm });
        wf["21"] = ComfyGraph.Node("ADE_ApplyAnimateDiffModelSimple", new { motion_model = ComfyGraph.Ref("20", 0) });
        wf["22"] = ComfyGraph.Node("ADE_UseEvolvedSampling", new { model = model0, beta_schedule = beta, m_models = ComfyGraph.Ref("21", 0) });
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = AnimeNegative, clip = clip0 });
        wf["26"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("11", 0), vae = vae0 });
        wf["27"] = ComfyGraph.Node("RepeatLatentBatch", new { samples = ComfyGraph.Ref("26", 0), amount = frames });
        wf["3"] = ComfyGraph.Node("KSampler", new { seed, steps = p.Int("steps", 20), cfg = p.Dbl("cfg", 1), sampler_name = ComfyGraph.MapSampler(p.Str("sampler")), scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")), denoise, model = ComfyGraph.Ref("22", 0), positive = ComfyGraph.Ref("13", 0), negative = ComfyGraph.Ref("12", 0), latent_image = ComfyGraph.Ref("27", 0) });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
