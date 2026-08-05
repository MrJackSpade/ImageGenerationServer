using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// 24GB-tier generation models whose graph is NOT the plain single-CLIPLoader txt2img topology, so each gets its own
/// Build (over the shared Txt2Img parameter menu + typed nodes). All three gate to 24GB via their config's
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

    /// <summary>The model loader block (UNETLoader / UnetLoaderGGUF / CheckpointLoaderSimple), returning typed
    /// model+vae refs.</summary>
    public static (Output<Slot.Model> model, Output<Slot.Vae> vae) LoadDiffusion(ComfyWorkflowGraph g, Txt2ImgParams p, ResolvedRequirements req)
    {
        LoaderKind loader = LoaderKinds.Parse(p.RequiredLoader());
        if (loader == LoaderKind.Checkpoint)
        {
            g[Nodes.Model] = new CheckpointLoaderSimple { CkptName = req.RequiredCheckpoint() };
            return (CheckpointLoaderSimple.ModelOut(Nodes.Model), CheckpointLoaderSimple.VaeOut(Nodes.Model));   // model, (clip unused), vae
        }
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        return (UNETLoader.ModelOut(Nodes.Model), VAELoader.VaeOut(Nodes.Vae));
    }
}

/// <summary>HiDream's flow-shift knob (ModelSamplingSD3).</summary>
public sealed record HiDreamParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)] public required double Shift { get; init; }
}

/// <summary>HiDream-I1 (Full/Dev/Fast): a 17B MoE DiT fed by FOUR text encoders via QuadrupleCLIPLoader
/// (clip_l → clip_g → t5xxl → llama-3.1-8b, in that order), with ModelSamplingSD3 flow-shift then a plain KSampler.
/// One workflow; the Full/Dev/Fast configs differ only by file + shift/steps/cfg/sampler. Wired from the official
/// hidream_i1_*.json templates.</summary>
public sealed class HiDreamWorkflow : Txt2ImgWorkflow<HiDreamParams>
{
    public override string Name => "hidream";

    protected override ComfyWorkflowGraph Build(HiDreamParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        (Output<Slot.Model> model0, Output<Slot.Vae> vae0) = HighVram.LoadDiffusion(g, p, req);
        g[Nodes.Clip] = new QuadrupleCLIPLoader
        {
            ClipName1 = req.TextEncoder(0),
            ClipName2 = req.TextEncoder(1),
            ClipName3 = req.TextEncoder(2),
            ClipName4 = req.TextEncoder(3),
        };
        Output<Slot.Clip> clipSrc = QuadrupleCLIPLoader.ClipOut(Nodes.Clip);
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = p.Shift };
        Output<Slot.Model> modelSrc = ModelSamplingSD3.Out(Nodes.ModelSampling);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clipSrc };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clipSrc };
        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptySD3LatentImage) { Width = w, Height = h, BatchSize = 1 };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = modelSrc,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = "forgemcp" };
        return g;
    }
}

/// <summary>SD3.5 Large / Large-Turbo loaded as the diffusion-only checkpoint (CheckpointLoaderSimple gives MODEL +
/// VAE; it carries no CLIP) with the three encoders supplied externally via TripleCLIPLoader (clip_l → clip_g →
/// t5xxl). No ModelSamplingSD3 (the official sd3 t2i graph wires the checkpoint MODEL straight to KSampler). One
/// workflow; the Large vs Turbo configs differ only by file + steps/cfg. Wired from the official sd3.5 text-encoders
/// example workflow.</summary>
public sealed class Sd35TripleClipWorkflow : Txt2ImgWorkflow<Txt2ImgParams>
{
    public override string Name => "sd35-large-tri";

    protected override ComfyWorkflowGraph Build(Txt2ImgParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        g[Nodes.Model] = new CheckpointLoaderSimple { CkptName = req.RequiredCheckpoint() };
        Output<Slot.Model> model0 = CheckpointLoaderSimple.ModelOut(Nodes.Model);
        Output<Slot.Vae> vae0 = CheckpointLoaderSimple.VaeOut(Nodes.Model);
        g[Nodes.Clip] = new TripleCLIPLoader
        {
            ClipName1 = req.TextEncoder(0),
            ClipName2 = req.TextEncoder(1),
            ClipName3 = req.TextEncoder(2),
        };
        Output<Slot.Clip> clipSrc = TripleCLIPLoader.ClipOut(Nodes.Clip);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clipSrc };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clipSrc };
        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptySD3LatentImage) { Width = w, Height = h, BatchSize = 1 };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = "forgemcp" };
        return g;
    }
}

/// <summary>Chroma's flow-shift knob (ModelSamplingAuraFlow).</summary>
public sealed record ChromaParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)] public required double Shift { get; init; }
}

/// <summary>Chroma1-HD: an 8.9B FLUX.1-schnell-derived DiT prompted with T5-XXL only (no CLIP-L). A single
/// CLIPLoader(type "chroma"), a T5TokenizerOptions(min_padding 0) pass that Chroma needs, ModelSamplingAuraFlow
/// flow-shift 1.0, then a plain KSampler at real CFG with a genuine negative prompt. Wired from the official
/// Chroma1-HD T2I workflow.</summary>
public sealed class ChromaWorkflow : Txt2ImgWorkflow<ChromaParams>
{
    public override string Name => "chroma";

    /// <summary>Chroma's only extra node id (reuses the inherited txt2img <c>Nodes.*</c>).</summary>
    private const string T5Options = "22";

    protected override ComfyWorkflowGraph Build(ChromaParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        (Output<Slot.Model> model0, Output<Slot.Vae> vae0) = HighVram.LoadDiffusion(g, p, req);
        string clipName = req.TextEncoder(0);
        g[Nodes.Clip] = ComfyGraph.IsGguf(clipName)
            ? new CLIPLoaderGGUF { ClipName = clipName, Type = "chroma" }
            : new CLIPLoader { ClipName = clipName, Type = "chroma", Device = "default" };
        // Chroma needs T5 min-padding disabled (the official graph inserts T5TokenizerOptions before the encodes).
        g[T5Options] = new T5TokenizerOptions { Clip = new Output<Slot.Clip>(Nodes.Clip, 0), MinPadding = 0, MinLength = 0 };
        Output<Slot.Clip> clipSrc = T5TokenizerOptions.Out(T5Options);
        g[Nodes.ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = p.Shift };
        Output<Slot.Model> modelSrc = ModelSamplingAuraFlow.Out(Nodes.ModelSampling);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clipSrc };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clipSrc };
        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptySD3LatentImage) { Width = w, Height = h, BatchSize = 1 };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = modelSrc,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = "forgemcp" };
        return g;
    }
}
