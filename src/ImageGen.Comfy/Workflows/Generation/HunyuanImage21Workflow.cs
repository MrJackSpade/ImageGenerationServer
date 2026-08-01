namespace ImageGen.Comfy;

/// <summary>
/// HunyuanImage 2.1 text→image (2K native). A diffusion-transformer image model with dual text encoders
/// (Qwen2.5-VL + ByT5-glyph, the SAME two files HunyuanVideo 1.5 already ships, reused by requirement id) and a
/// 32× VAE. Its own graph because it diverges from the shared txt2img base on two nodes: <c>ModelSamplingSD3</c>
/// (flow-shift 5, not AuraFlow) and <c>EmptyHunyuanImageLatent</c> (not the std/SD3/Flux2 latents the base offers).
/// The distilled build runs ~8 steps at low CFG (meanflow distillation); the standard build ~50 steps at CFG 3.5.
/// The refiner stage isn't implemented natively in ComfyUI yet, so it's intentionally omitted. ~17 GB fp8 unet +
/// the Qwen encoder → 24 GB tier (gated by the config's min_vram_mb).
/// </summary>
public sealed class HunyuanImage21Workflow : Txt2ImgWorkflowBase
{
    public override string Name => "hunyuanimage21";
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(new ParamSpec[]
    {
        new() { Key = "shift", Type = ParamType.Double, Default = 5.0, Min = 1.0, Max = 12.0, Label = "Flow shift" },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var enc = req.TextEncoders;
        int sw = p.Int("width", 2048), sh = p.Int("height", 2048);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);

        wf["4"] = ComfyGraph.DiffusionLoader(req.Checkpoint);
        wf["30"] = ComfyGraph.Node("ModelSamplingSD3", new { model = ComfyGraph.Ref("4", 0), shift = p.Dbl("shift", 5.0) });
        object model = ComfyGraph.ApplyLora(wf, ComfyGraph.Ref("30", 0), p);
        wf["20"] = ComfyGraph.Node("DualCLIPLoader", new { clip_name1 = enc.ElementAtOrDefault(0) ?? "", clip_name2 = enc.ElementAtOrDefault(1) ?? "", type = p.Str("clip_type") ?? "hunyuan_image", device = "default" });
        object clip = ComfyGraph.Ref("20", 0);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
        object vae = ComfyGraph.Ref("21", 0);

        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip });
        wf["5"] = ComfyGraph.Node("EmptyHunyuanImageLatent", new { width = w, height = h, batch_size = 1 });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 8),
            cfg = p.Dbl("cfg", 2.5),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model,
            positive = ComfyGraph.Ref("6", 0),
            negative = ComfyGraph.Ref("7", 0),
            latent_image = ComfyGraph.Ref("5", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
