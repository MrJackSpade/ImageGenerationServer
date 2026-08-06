namespace ImageGen.Comfy;

/// <summary>
/// Mage-Flow text-to-image (Microsoft's 4B native-resolution MMDiT: Mage-VAE + Qwen3-VL encoder, rectified flow).
/// Its generation graph does NOT fit the shared <see cref="Txt2ImgWorkflowBase"/> topology: Mage encodes the prompt
/// with the SAME unified node as editing — <c>TextEncodeMageFlowEdit</c> — which, given no reference images, emits
/// the (positive, negative) conditioning AND a correctly-shaped zero latent in one shot (there is no separate
/// CLIPTextEncode / Empty-latent node). So this overrides <see cref="Build"/> with the official t2i template's graph.
/// The flow shift (6.0) is baked into the model at load, so no ModelSamplingAuraFlow node is emitted.
/// Exact lift of the Comfy-Org <c>image_mage_flow_t2i_int8</c> template's sampling path.
/// </summary>
public abstract class MageFlowGenBase : Txt2ImgWorkflow<Txt2ImgParams>
{
    protected override ComfyWorkflowGraph Build(Txt2ImgParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        ComfyWorkflowGraph g = new();

        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // UNETLoader (.safetensors int8_convrot / bf16)
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Mage, Device = ComfyWidgets.Device.Default };
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };

        // Mage's unified conditioning node in its text-only mode: no image inputs -> pure t2i, and it also produces
        // the zero latent (batch×128×h/16×w/16) so the sampler's shape always matches the model. vae is unused here
        // (only edits encode reference latents), so it is left unconnected.
        g[MageFlowGenBaseNodes.Encode] = new TextEncodeMageFlowGen
        {
            Clip = CLIPLoader.ClipOut(Nodes.Clip),
            Prompt = inputs.Positive,
            NegativePrompt = inputs.Negative ?? "",
            Width = w,
            Height = h,
            BatchSize = 1,
        };

        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = UNETLoader.ModelOut(Nodes.Model),
            Positive = TextEncodeMageFlowGen.PositiveOut(MageFlowGenBaseNodes.Encode),
            Negative = TextEncodeMageFlowGen.NegativeOut(MageFlowGenBaseNodes.Encode),
            LatentImage = TextEncodeMageFlowGen.LatentOut(MageFlowGenBaseNodes.Encode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = VAELoader.VaeOut(Nodes.Vae) };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
