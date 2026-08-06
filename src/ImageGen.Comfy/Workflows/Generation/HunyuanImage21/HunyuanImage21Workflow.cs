namespace ImageGen.Comfy.Generation.HunyuanImage21;

/// <summary>
/// HunyuanImage 2.1 text→image (2K native). A diffusion-transformer image model with dual text encoders
/// (Qwen2.5-VL + ByT5-glyph, the SAME two files HunyuanVideo 1.5 already ships, reused by requirement id) and a
/// 32× VAE. Its own graph because it diverges from the shared txt2img base on two nodes: <c>ModelSamplingSD3</c>
/// (flow-shift 5, not AuraFlow) and <c>EmptyHunyuanImageLatent</c> (not the std/SD3/Flux2 latents the base offers).
/// The distilled build runs ~8 steps at low CFG (meanflow distillation); the standard build ~50 steps at CFG 3.5.
/// The refiner stage isn't implemented natively in ComfyUI yet, so it's intentionally omitted. ~17 GB fp8 unet +
/// the Qwen encoder → 24 GB tier (gated by the config's min_vram_mb).
/// </summary>
public sealed class HunyuanImage21Workflow : Txt2ImgWorkflow<HunyuanImage21Params>
{
    public override string Name => "hunyuanimage21";
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override IReadOnlyList<ParamSpec> Schema => Txt2ImgWorkflowBase.SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Shift, Type = ParamType.Double, Min = 1.0, Max = 12.0, Label = "Flow shift" },
    }).ToArray();

    protected override ComfyWorkflowGraph Build(HunyuanImage21Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));

        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[HunyuanImage21WorkflowNodes.ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.Model), Shift = p.Shift };
        Output<Slot.Model> model = ComfyGraph.ApplyLora(g, ModelSamplingSD3.Out(HunyuanImage21WorkflowNodes.ModelSampling), p.Lora, p.LoraStrength);
        g[Nodes.Clip] = new DualCLIPLoader { ClipName1 = req.TextEncoder(0), ClipName2 = req.TextEncoder(1), Type = ComfyWidgets.ClipType.HunyuanImage, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = DualCLIPLoader.ClipOut(Nodes.Clip);
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(Nodes.Vae);

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip };
        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptyHunyuanImageLatent) { Width = w, Height = h, BatchSize = 1 };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
