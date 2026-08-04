//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>HunyuanVideo 1.5 image-to-video (480p cfg-distilled fp8). The model/clip/VAE come from the shared
/// LoadModel head (loader=unet, dual=true, clip_type="hunyuan_video_15" → UNETLoader + the Qwen2.5-VL/byT5
/// DualCLIPLoader + VAELoader). On top: ModelSamplingSD3 flow-shift, a SigCLIP vision encoder that conditions on
/// the source image (CLIPVisionEncode → HunyuanVideo15ImageToVideo's start_image/clip_vision_output), and a
/// BasicScheduler + SamplerCustomAdvanced sampling chain. The 7.8GB fp8 unet + 8.7GB Qwen encoder total ~16.5GB.
/// Uncensored base; animates anime natively. LoRA-aware via ApplyLora. Validated live (shift 7, cfg 1).</summary>
public sealed class HunyuanVideo15I2VWorkflow : EditWorkflowBase
{
    public override string Name => "hunyuanvideo15-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = "shift", Type = ParamType.Double, Default = 7.0, Min = 1.0, Max = 12.0, Label = "Flow shift" },
    }).Concat(HunyuanSr.Schema).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        model0 = ComfyGraph.ApplyLora(wf, model0, p);   // optional anime LoRA on the Hunyuan model
        wf["30"] = ComfyGraph.Node("ModelSamplingSD3", new { model = model0, shift = p.DblReq("shift") });
        object modelS = ComfyGraph.Ref("30", 0);
        var seed = ComfyGraph.Seed(p);
        int frames = p.IntReq("length");
        double fps = p.DblReq("fps");
        double budgetMp = 0.4;   // HunyuanVideo 1.5's native i2v megapixel budget — always applied (the source is scaled to it)
        wf["51"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 16 });
        wf["52"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("51", 0) });
        wf["40"] = ComfyGraph.Node("CLIPVisionLoader", new { clip_name = p.Model("clip_vision") });
        wf["41"] = ComfyGraph.Node("CLIPVisionEncode", new { clip_vision = ComfyGraph.Ref("40", 0), image = ComfyGraph.Ref("51", 0), crop = "center" });
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["12"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clip0 });
        wf["53"] = ComfyGraph.Node("HunyuanVideo15ImageToVideo", new { positive = ComfyGraph.Ref("13", 0), negative = ComfyGraph.Ref("12", 0), vae = vae0, width = ComfyGraph.Ref("52", 0), height = ComfyGraph.Ref("52", 1), length = frames, batch_size = 1, start_image = ComfyGraph.Ref("51", 0), clip_vision_output = ComfyGraph.Ref("41", 0) });
        wf["55"] = ComfyGraph.Node("BasicScheduler", new { model = modelS, scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")), steps = p.IntReq("steps"), denoise = 1.0 });
        wf["56"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        wf["57"] = ComfyGraph.Node("RandomNoise", new { noise_seed = seed });
        wf["58"] = ComfyGraph.Node("CFGGuider", new { model = modelS, positive = ComfyGraph.Ref("53", 0), negative = ComfyGraph.Ref("53", 1), cfg = p.DblReq("cfg") });
        wf["3"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("57", 0), guider = ComfyGraph.Ref("58", 0), sampler = ComfyGraph.Ref("56", 0), sigmas = ComfyGraph.Ref("55", 0), latent_image = ComfyGraph.Ref("53", 2) });
        // Optional super-resolution second pass (1080p). Conditioning is the raw text encode (13/12); the source
        // image (raw LoadImage 10) + SigCLIP vision (41) carry over as SR consistency cues. Returns node 3 unchanged when off.
        object outLatent = HunyuanSr.Refine(wf, p, ComfyGraph.Ref("3", 0), ComfyGraph.Ref("13", 0), ComfyGraph.Ref("12", 0), vae0, ComfyGraph.Ref("10", 0), ComfyGraph.Ref("41", 0), seed);
        wf["8"] = HunyuanSr.Enabled(p)
            ? ComfyGraph.Node("VAEDecodeTiled", new { samples = outLatent, vae = vae0, tile_size = 256, overlap = 64, temporal_size = 64, temporal_overlap = 8 })
            : ComfyGraph.Node("VAEDecode", new { samples = outLatent, vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
