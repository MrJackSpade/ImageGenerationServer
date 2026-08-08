namespace ImageGen.Comfy.Generation.HiDream;

/// <summary>HiDream-I1 (Full/Dev/Fast): a 17B MoE DiT fed by FOUR text encoders via QuadrupleCLIPLoader
/// (clip_l → clip_g → t5xxl → llama-3.1-8b, in that order), with ModelSamplingSD3 flow-shift then a plain KSampler.
/// One workflow; the Full/Dev/Fast configs differ only by file + shift/steps/cfg/sampler. Wired from the official
/// hidream_i1_*.json templates.</summary>
public sealed class HiDreamWorkflow : Txt2ImgWorkflow<HiDreamParams>
{
    public override string Name => "hidream";

    protected override ComfyWorkflowGraph Build(HiDreamParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
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

        (int w, int h) = RenderSize(p, req, inputs);
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
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
