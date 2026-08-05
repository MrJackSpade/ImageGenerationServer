namespace ImageGen.Comfy;

/// <summary>
/// 24GB-tier generation models whose graph is NOT the plain single-CLIPLoader txt2img topology, so each gets its own
/// Build (over the shared Txt2Img parameter menu + emit primitives). All three gate to 24GB via their config's
/// min_vram_mb. Node ids follow the txt2img convention (4=model, 20=clip, 21=vae, 11=model-sampling, 6/7=encode,
/// 5=latent, 3=sampler, 8=decode, 9=save). Wired from the official ComfyUI example workflows; smoke-test on the box.
/// </summary>
file static class HighVram
{
    /// <summary>The loader block's node ids, named by role (values preserved: 4=model, 21=vae).</summary>
    private static class Nodes
    {
        public const string Model = "4";
        public const string Vae = "21";
    }

    /// <summary>The model loader block (UNETLoader / UnetLoaderGGUF / CheckpointLoaderSimple), returning model+vae refs.</summary>
    public static (object model, object vae) LoadDiffusion(Dictionary<string, object> wf, ParamValues p, ResolvedRequirements req)
    {
        LoaderKind loader = p.Loader();
        if (loader == LoaderKind.Checkpoint)
        {
            wf[Nodes.Model] = ComfyGraph.Node(ComfyNodeTypes.CheckpointLoaderSimple, new { ckpt_name = req.RequiredCheckpoint() });
            return (ComfyGraph.Ref(Nodes.Model, 0), ComfyGraph.Ref(Nodes.Model, 2));   // model, (clip unused), vae
        }
        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        return (ComfyGraph.Ref(Nodes.Model, 0), ComfyGraph.Ref(Nodes.Vae, 0));
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
        Dictionary<string, object> wf = new Dictionary<string, object>();
        (object? model0, object? vae0) = HighVram.LoadDiffusion(wf, p, req);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.QuadrupleCLIPLoader, new
        {
            clip_name1 = req.TextEncoder(0),
            clip_name2 = req.TextEncoder(1),
            clip_name3 = req.TextEncoder(2),
            clip_name4 = req.TextEncoder(3),
        });
        object clipSrc = ComfyGraph.Ref(Nodes.Clip, 0);
        wf[Nodes.ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = model0, shift = p.DblReq(WorkflowParamKeys.Shift) });
        object modelSrc = ComfyGraph.Ref(Nodes.ModelSampling, 0);

        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clipSrc });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clipSrc });
        wf[Nodes.Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptySD3LatentImage, new { width = w, height = h, batch_size = 1 });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = modelSrc,
            positive = ComfyGraph.Ref(Nodes.Positive, 0),
            negative = ComfyGraph.Ref(Nodes.Negative, 0),
            latent_image = ComfyGraph.Ref(Nodes.Latent, 0),
        });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = vae0 });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
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
        Dictionary<string, object> wf = new Dictionary<string, object>();
        wf[Nodes.Model] = ComfyGraph.Node(ComfyNodeTypes.CheckpointLoaderSimple, new { ckpt_name = req.RequiredCheckpoint() });
        object model0 = ComfyGraph.Ref(Nodes.Model, 0), vae0 = ComfyGraph.Ref(Nodes.Model, 2);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.TripleCLIPLoader, new
        {
            clip_name1 = req.TextEncoder(0),
            clip_name2 = req.TextEncoder(1),
            clip_name3 = req.TextEncoder(2),
        });
        object clipSrc = ComfyGraph.Ref(Nodes.Clip, 0);

        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clipSrc });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clipSrc });
        wf[Nodes.Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptySD3LatentImage, new { width = w, height = h, batch_size = 1 });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref(Nodes.Positive, 0),
            negative = ComfyGraph.Ref(Nodes.Negative, 0),
            latent_image = ComfyGraph.Ref(Nodes.Latent, 0),
        });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = vae0 });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
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

    /// <summary>Chroma's only extra node (reuses the inherited txt2img <c>Nodes.*</c>).</summary>
    private const string T5TokenizerOptions = "22";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        (object? model0, object? vae0) = HighVram.LoadDiffusion(wf, p, req);
        string clipName = req.TextEncoder(0);
        wf[Nodes.Clip] = ComfyGraph.IsGguf(clipName)
            ? ComfyGraph.Node(ComfyNodeTypes.CLIPLoaderGGUF, new { clip_name = clipName, type = "chroma" })
            : ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = clipName, type = "chroma", device = "default" });
        // Chroma needs T5 min-padding disabled (the official graph inserts T5TokenizerOptions before the encodes).
        wf[T5TokenizerOptions] = ComfyGraph.Node(ComfyNodeTypes.T5TokenizerOptions, new { clip = ComfyGraph.Ref(Nodes.Clip, 0), min_padding = 0, min_length = 0 });
        object clipSrc = ComfyGraph.Ref(T5TokenizerOptions, 0);
        wf[Nodes.ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingAuraFlow, new { model = model0, shift = p.DblReq(WorkflowParamKeys.Shift) });
        object modelSrc = ComfyGraph.Ref(Nodes.ModelSampling, 0);

        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clipSrc });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clipSrc });
        wf[Nodes.Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptySD3LatentImage, new { width = w, height = h, batch_size = 1 });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = modelSrc,
            positive = ComfyGraph.Ref(Nodes.Positive, 0),
            negative = ComfyGraph.Ref(Nodes.Negative, 0),
            latent_image = ComfyGraph.Ref(Nodes.Latent, 0),
        });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = vae0 });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
