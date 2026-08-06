namespace ImageGen.Comfy.Generation.Sd35TripleClip;

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
        ComfyWorkflowGraph g = new();
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
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
