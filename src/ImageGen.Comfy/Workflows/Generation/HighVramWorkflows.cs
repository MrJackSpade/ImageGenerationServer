namespace ImageGen.Comfy;

/// <summary>
/// 24GB-tier generation models whose graph is NOT the plain single-CLIPLoader txt2img topology, so each gets its own
/// Build (over the shared Txt2Img parameter menu + emit primitives). All three gate to 24GB via their config's
/// min_vram_mb. Node ids follow the txt2img convention (4=model, 20=clip, 21=vae, 11=model-sampling, 6/7=encode,
/// 5=latent, 3=sampler, 8=decode, 9=save). Wired from the official ComfyUI example workflows; smoke-test on the box.
/// </summary>
file static class HighVram
{
    /// <summary>The model loader block (UNETLoader / UnetLoaderGGUF / CheckpointLoaderSimple), returning model+vae refs.</summary>
    public static (object model, object vae) LoadDiffusion(Dictionary<string, object> wf, ParamValues p, ResolvedRequirements req)
    {
        var loader = p.Str("loader") ?? "unet";
        if (loader == "checkpoint")
        {
            wf["4"] = ComfyGraph.Node("CheckpointLoaderSimple", new { ckpt_name = req.Checkpoint });
            return (ComfyGraph.Ref("4", 0), ComfyGraph.Ref("4", 2));   // model, (clip unused), vae
        }
        wf["4"] = ComfyGraph.DiffusionLoader(req.Checkpoint);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
        return (ComfyGraph.Ref("4", 0), ComfyGraph.Ref("21", 0));
    }
}

/// <summary>HiDream-I1 (Full/Dev/Fast): a 17B MoE DiT fed by FOUR text encoders via QuadrupleCLIPLoader
/// (clip_l → clip_g → t5xxl → llama-3.1-8b, in that order), with ModelSamplingSD3 flow-shift then a plain KSampler.
/// One workflow; the Full/Dev/Fast configs differ only by file + shift/steps/cfg/sampler. Wired from the official
/// hidream_i1_*.json templates.</summary>
public sealed class HiDreamWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "hidream";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var (model0, vae0) = HighVram.LoadDiffusion(wf, p, req);
        var enc = req.TextEncoders;
        wf["20"] = ComfyGraph.Node("QuadrupleCLIPLoader", new
        {
            clip_name1 = enc.ElementAtOrDefault(0) ?? "",
            clip_name2 = enc.ElementAtOrDefault(1) ?? "",
            clip_name3 = enc.ElementAtOrDefault(2) ?? "",
            clip_name4 = enc.ElementAtOrDefault(3) ?? "",
        });
        object clipSrc = ComfyGraph.Ref("20", 0);
        wf["11"] = ComfyGraph.Node("ModelSamplingSD3", new { model = model0, shift = p.Dbl("shift", 3.0) });
        object modelSrc = ComfyGraph.Ref("11", 0);

        int sw = p.Int("width", 1024), sh = p.Int("height", 1024);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clipSrc });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clipSrc });
        wf["5"] = ComfyGraph.Node("EmptySD3LatentImage", new { width = w, height = h, batch_size = 1 });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 28),
            cfg = p.Dbl("cfg", 5),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model = modelSrc,
            positive = ComfyGraph.Ref("6", 0),
            negative = ComfyGraph.Ref("7", 0),
            latent_image = ComfyGraph.Ref("5", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}

/// <summary>SD3.5 Large / Large-Turbo loaded as the diffusion-only checkpoint (CheckpointLoaderSimple gives MODEL +
/// VAE; it carries no CLIP) with the three encoders supplied externally via TripleCLIPLoader (clip_l → clip_g →
/// t5xxl). No ModelSamplingSD3 (the official sd3 t2i graph wires the checkpoint MODEL straight to KSampler). One
/// workflow; the Large vs Turbo configs differ only by file + steps/cfg. Wired from the official sd3.5 text-encoders
/// example workflow.</summary>
public sealed class Sd35TripleClipWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "sd35-large-tri";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        wf["4"] = ComfyGraph.Node("CheckpointLoaderSimple", new { ckpt_name = req.Checkpoint });
        object model0 = ComfyGraph.Ref("4", 0), vae0 = ComfyGraph.Ref("4", 2);
        var enc = req.TextEncoders;
        wf["20"] = ComfyGraph.Node("TripleCLIPLoader", new
        {
            clip_name1 = enc.ElementAtOrDefault(0) ?? "",
            clip_name2 = enc.ElementAtOrDefault(1) ?? "",
            clip_name3 = enc.ElementAtOrDefault(2) ?? "",
        });
        object clipSrc = ComfyGraph.Ref("20", 0);

        int sw = p.Int("width", 1024), sh = p.Int("height", 1024);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clipSrc });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clipSrc });
        wf["5"] = ComfyGraph.Node("EmptySD3LatentImage", new { width = w, height = h, batch_size = 1 });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 20),
            cfg = p.Dbl("cfg", 4.5),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref("6", 0),
            negative = ComfyGraph.Ref("7", 0),
            latent_image = ComfyGraph.Ref("5", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}

/// <summary>Chroma1-HD: an 8.9B FLUX.1-schnell-derived DiT prompted with T5-XXL only (no CLIP-L). A single
/// CLIPLoader(type "chroma"), a T5TokenizerOptions(min_padding 0) pass that Chroma needs, ModelSamplingAuraFlow
/// flow-shift 1.0, then a plain KSampler at real CFG with a genuine negative prompt. Wired from the official
/// Chroma1-HD T2I workflow.</summary>
public sealed class ChromaWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "chroma";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var (model0, vae0) = HighVram.LoadDiffusion(wf, p, req);
        var clipName = req.TextEncoders.ElementAtOrDefault(0) ?? "";
        wf["20"] = clipName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? ComfyGraph.Node("CLIPLoaderGGUF", new { clip_name = clipName, type = p.Str("clip_type") ?? "chroma" })
            : ComfyGraph.Node("CLIPLoader", new { clip_name = clipName, type = p.Str("clip_type") ?? "chroma", device = "default" });
        // Chroma needs T5 min-padding disabled (the official graph inserts T5TokenizerOptions before the encodes).
        wf["22"] = ComfyGraph.Node("T5TokenizerOptions", new { clip = ComfyGraph.Ref("20", 0), min_padding = 0, min_length = 0 });
        object clipSrc = ComfyGraph.Ref("22", 0);
        wf["11"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = model0, shift = p.Dbl("shift", 1.0) });
        object modelSrc = ComfyGraph.Ref("11", 0);

        int sw = p.Int("width", 1024), sh = p.Int("height", 1024);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clipSrc });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? ComfyGraph.DefaultNegative, clip = clipSrc });
        wf["5"] = ComfyGraph.Node("EmptySD3LatentImage", new { width = w, height = h, batch_size = 1 });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 26),
            cfg = p.Dbl("cfg", 3.8),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model = modelSrc,
            positive = ComfyGraph.Ref("6", 0),
            negative = ComfyGraph.Ref("7", 0),
            latent_image = ComfyGraph.Ref("5", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
