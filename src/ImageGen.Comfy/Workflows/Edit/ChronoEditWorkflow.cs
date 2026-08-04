namespace ImageGen.Comfy;

/// <summary>
/// ChronoEdit-14B instruction image editor (NVIDIA). It's a Wan2.1-I2V backbone repurposed for editing: the source
/// image conditions a very short "trajectory" (a few frames) and we keep the LAST frame as the edited result
/// ("temporal reasoning"). Runs entirely on native ComfyUI nodes — no custom node. Reuses the Wan UMT5 text encoder
/// and the Wan 2.1 VAE, plus the standard CLIP-ViT-H clip-vision. A distilled LoRA enables the fast 20-step/CFG4 path.
/// Mirrors the official <c>image_chrono_edit_14B</c> template.
/// </summary>
public sealed class ChronoEditWorkflow : EditWorkflowBase
{
    public override string Name => "chronoedit";

    /// <summary>Wan's quality/motion negative (same default the Wan i2v path uses).</summary>
    private const string Negative =
        "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // 4=unet,5=clip(wan),6=vae(wan2.1),10=LoadImage
        model0 = ComfyGraph.ApplyLora(wf, model0, p);                                  // distilled LoRA (fast 20-step path)
        var seed = ComfyGraph.Seed(p);
        int len = p.IntReq("length");                                                  // ChronoEdit's short trajectory
        double budgetMp = 0.52;   // ChronoEdit's native ~0.5MP budget (720² ≈ 0.52MP) — always applied (the source is scaled to it)

        // Sampling fix-ups the template applies to the Wan model for ChronoEdit.
        wf["20"] = ComfyGraph.Node("ModelSamplingSD3", new { model = model0, shift = 5.0 });
        wf["21"] = ComfyGraph.Node("ScaleROPE", new { model = ComfyGraph.Ref("20", 0), scale_x = 1.0, shift_x = 0.0, scale_y = 1.0, shift_y = 0.0, scale_t = 1.0, shift_t = 0.0 });
        var ksModel = ComfyGraph.Ref("21", 0);

        // Source image, scaled to a ~0.5MP budget (preserves aspect; 720² ≈ 0.52MP), reused as both the i2v start
        // frame and the clip-vision input.
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 32 });
        wf["15"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["30"] = ComfyGraph.Node("CLIPVisionLoader", new { clip_name = p.Model("clip_vision") });
        wf["31"] = ComfyGraph.Node("CLIPVisionEncode", new { clip_vision = ComfyGraph.Ref("30", 0), image = ComfyGraph.Ref("11", 0), crop = "none" });

        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = Negative, clip = clip0 });

        // Wan2.1 i2v conditioning node: bakes the start image + clip-vision into pos/neg conditioning + the latent.
        wf["14"] = ComfyGraph.Node("WanImageToVideo", new
        {
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("12", 0),
            vae = vae0,
            clip_vision_output = ComfyGraph.Ref("31", 0),
            width = ComfyGraph.Ref("15", 0),
            height = ComfyGraph.Ref("15", 1),
            length = len,
            batch_size = 1,
            start_image = ComfyGraph.Ref("11", 0),
        });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed,
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = ksModel,
            positive = ComfyGraph.Ref("14", 0),
            negative = ComfyGraph.Ref("14", 1),
            latent_image = ComfyGraph.Ref("14", 2),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        // Keep the LAST frame of the short trajectory as the edited still.
        wf["16"] = ComfyGraph.Node("ImageFromBatch", new { image = ComfyGraph.Ref("8", 0), batch_index = Math.Max(0, len - 1), length = 1 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("16", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
