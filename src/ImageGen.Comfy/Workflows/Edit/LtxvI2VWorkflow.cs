namespace ImageGen.Comfy;

/// <summary>LTX-Video image-to-video: fast distilled model; source conditions frame 0. LTX has no CLIP in the
/// checkpoint — it loads an external T5. Exact lift of the old <c>ltxv_i2v</c> branch.</summary>
public sealed class LtxvI2VWorkflow : EditWorkflowBase
{
    public override string Name => "ltxv-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the node's length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out _, out var vae0);
        model0 = ComfyGraph.ApplyLora(wf, model0, p);   // optional anime-style LoRA on the LTX model
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq("length");
        double fps = p.DblReq("fps");
        // LTX loads its own external T5 (clip_type "ltxv").
        wf["50"] = ComfyGraph.Node("CLIPLoader", new { clip_name = req.TextEncoder(0), type = "ltxv", device = "default" });
        object ltxClip = ComfyGraph.Ref("50", 0);
        double budgetMp = 0.39;   // LTX's native i2v megapixel budget — always applied (the source is scaled to it)
        wf["51"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 32 });
        wf["52"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("51", 0) });
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = ltxClip });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = ltxClip });
        wf["53"] = ComfyGraph.Node("LTXVImgToVideo", new { positive = ComfyGraph.Ref("13", 0), negative = ComfyGraph.Ref("12", 0), vae = vae0, image = ComfyGraph.Ref("51", 0), width = ComfyGraph.Ref("52", 0), height = ComfyGraph.Ref("52", 1), length = frames, batch_size = 1, strength = 1.0 });
        wf["54"] = ComfyGraph.Node("LTXVConditioning", new { positive = ComfyGraph.Ref("53", 0), negative = ComfyGraph.Ref("53", 1), frame_rate = fps });
        wf["55"] = ComfyGraph.Node("LTXVScheduler", new { steps = p.IntReq("steps"), max_shift = 2.05, base_shift = 0.95, stretch = true, terminal = 0.1, latent = ComfyGraph.Ref("53", 2) });
        wf["56"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        wf["3"] = ComfyGraph.Node("SamplerCustom", new { model = model0, add_noise = true, noise_seed = seed, cfg = p.DblReq("cfg"), positive = ComfyGraph.Ref("54", 0), negative = ComfyGraph.Ref("54", 1), sampler = ComfyGraph.Ref("56", 0), sigmas = ComfyGraph.Ref("55", 0), latent_image = ComfyGraph.Ref("53", 2) });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
