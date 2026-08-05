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

    /// <summary>This subclass's own node ids (the shared head's Model/Clip/Vae/Source come from EditWorkflowBase.Nodes);
    /// values are the graph-local keys, preserved exactly so the emitted graph stays byte-identical.</summary>
    private const string ModelSampling = "20";
    private const string ScaleRope = "21";
    private const string ScaledSource = "11";
    private const string SourceSize = "15";
    private const string ClipVisionLoader = "30";
    private const string ClipVisionEncode = "31";
    private const string PositiveEncode = "13";
    private const string NegativeEncode = "12";
    private const string I2VConditioning = "14";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string LastFrame = "16";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);   // 4=unet,5=clip(wan),6=vae(wan2.1),10=LoadImage
        model0 = ComfyGraph.ApplyLora(wf, model0, p);                                  // distilled LoRA (fast 20-step path)
        long seed = ComfyGraph.Seed(p);
        int len = p.IntReq(WorkflowParamKeys.Length);                                  // ChronoEdit's short trajectory
        double budgetMp = 0.52;   // ChronoEdit's native ~0.5MP budget (720² ≈ 0.52MP) — always applied (the source is scaled to it)

        // Sampling fix-ups the template applies to the Wan model for ChronoEdit.
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = model0, shift = 5.0 });
        wf[ScaleRope] = ComfyGraph.Node(ComfyNodeTypes.ScaleROPE, new { model = ComfyGraph.Ref(ModelSampling, 0), scale_x = 1.0, shift_x = 0.0, scale_y = 1.0, shift_y = 0.0, scale_t = 1.0, shift_t = 0.0 });
        object ksModel = ComfyGraph.Ref(ScaleRope, 0);

        // Source image, scaled to a ~0.5MP budget (preserves aspect; 720² ≈ 0.52MP), reused as both the i2v start
        // frame and the clip-vision input.
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 32 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[ClipVisionLoader] = ComfyGraph.Node(ComfyNodeTypes.CLIPVisionLoader, new { clip_name = p.Model(WorkflowParamKeys.ClipVision) });
        wf[ClipVisionEncode] = ComfyGraph.Node(ComfyNodeTypes.CLIPVisionEncode, new { clip_vision = ComfyGraph.Ref(ClipVisionLoader, 0), image = ComfyGraph.Ref(ScaledSource, 0), crop = "none" });

        wf[PositiveEncode] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[NegativeEncode] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = Negative, clip = clip0 });

        // Wan2.1 i2v conditioning node: bakes the start image + clip-vision into pos/neg conditioning + the latent.
        wf[I2VConditioning] = ComfyGraph.Node(ComfyNodeTypes.WanImageToVideo, new
        {
            positive = ComfyGraph.Ref(PositiveEncode, 0),
            negative = ComfyGraph.Ref(NegativeEncode, 0),
            vae = vae0,
            clip_vision_output = ComfyGraph.Ref(ClipVisionEncode, 0),
            width = ComfyGraph.Ref(SourceSize, 0),
            height = ComfyGraph.Ref(SourceSize, 1),
            length = len,
            batch_size = 1,
            start_image = ComfyGraph.Ref(ScaledSource, 0),
        });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed,
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = ksModel,
            positive = ComfyGraph.Ref(I2VConditioning, 0),
            negative = ComfyGraph.Ref(I2VConditioning, 1),
            latent_image = ComfyGraph.Ref(I2VConditioning, 2),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        // Keep the LAST frame of the short trajectory as the edited still.
        wf[LastFrame] = ComfyGraph.Node(ComfyNodeTypes.ImageFromBatch, new { image = ComfyGraph.Ref(Decode, 0), batch_index = Math.Max(0, len - 1), length = 1 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(LastFrame, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
