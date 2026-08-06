namespace ImageGen.Comfy.Generation.Chroma;

/// <summary>Chroma1-HD: an 8.9B FLUX.1-schnell-derived DiT prompted with T5-XXL only (no CLIP-L). A single
/// CLIPLoader(type "chroma"), a T5TokenizerOptions(min_padding 0) pass that Chroma needs, ModelSamplingAuraFlow
/// flow-shift 1.0, then a plain KSampler at real CFG with a genuine negative prompt. Wired from the official
/// Chroma1-HD T2I workflow.</summary>
public sealed class ChromaWorkflow : Txt2ImgWorkflow<ChromaParams>
{
    public override string Name => "chroma";

    protected override ComfyWorkflowGraph Build(ChromaParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        (Output<Slot.Model> model0, Output<Slot.Vae> vae0) = HighVram.LoadDiffusion(g, p, req);
        string clipName = req.TextEncoder(0);
        g[Nodes.Clip] = ComfyGraph.IsGguf(clipName)
            ? new CLIPLoaderGGUF { ClipName = clipName, Type = ComfyWidgets.ClipType.Chroma }
            : new CLIPLoader { ClipName = clipName, Type = ComfyWidgets.ClipType.Chroma, Device = ComfyWidgets.Device.Default };
        // Chroma needs T5 min-padding disabled (the official graph inserts T5TokenizerOptions before the encodes).
        g[ChromaWorkflowNodes.T5Options] = new T5TokenizerOptions { Clip = new Output<Slot.Clip>(Nodes.Clip, 0), MinPadding = 0, MinLength = 0 };
        Output<Slot.Clip> clipSrc = T5TokenizerOptions.Out(ChromaWorkflowNodes.T5Options);
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
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
