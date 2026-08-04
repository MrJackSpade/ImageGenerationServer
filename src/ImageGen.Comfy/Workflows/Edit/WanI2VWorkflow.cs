//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>Wan 2.2 TI2V-5B image-to-video: the source image is the first frame; output is an animated WEBP. The
/// text prompt drives the motion/scene.</summary>
public sealed class WanI2VWorkflow : EditWorkflowBase
{
    public override string Name => "wan22-ti2v-5b";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Wan VAE: 4× temporal compression → valid clip lengths are 4n+1 (mirrors the node's length step=4).</summary>
    public override FrameRule? FrameRule => new(1, 4);

    /// <summary>Flow shift. The Wan2.2 repo's ti2v_5B config runs 5.0; without an explicit node ComfyUI silently
    /// applies its own Wan default of 8.0, so the graph pins the reference value.</summary>
    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = "shift", Type = ParamType.Double, Default = 5.0, Min = 1.0, Max = 12.0, Step = 0.1, Label = "Flow shift" },
    }).ToArray();

    /// <summary>Wan's standard quality/motion negative (the ComfyUI native Wan template default).</summary>
    private const string WanNegative =
        "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        model0 = ComfyGraph.ApplyLora(wf, model0, p);   // optional anime-style LoRA (e.g. Flat Color) on the WAN model
        wf["30"] = ComfyGraph.Node("ModelSamplingSD3", new { model = model0, shift = p.Dbl("shift", 5.0) });
        model0 = ComfyGraph.Ref("30", 0);
        var seed = ComfyGraph.Seed(p);
        int len = p.Int("length") > 0 ? p.Int("length") : 121;     // Wan22ImageToVideoLatent length is 4k+1
        double fps = p.Dbl("fps") > 0 ? p.Dbl("fps") : 24;
        double budgetMp = (p.Int("width") > 0 && p.Int("height") > 0) ? (p.Int("width") * (double)p.Int("height")) / 1_000_000.0 : 0.9;
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 32 });
        wf["15"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = WanNegative, clip = clip0 });
        wf["14"] = ComfyGraph.Node("Wan22ImageToVideoLatent", new { vae = vae0, width = ComfyGraph.Ref("15", 0), height = ComfyGraph.Ref("15", 1), length = len, batch_size = 1, start_image = ComfyGraph.Ref("11", 0) });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed,
            steps = p.Int("steps", 20),
            cfg = p.Dbl("cfg", 1),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("12", 0),
            latent_image = ComfyGraph.Ref("14", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
