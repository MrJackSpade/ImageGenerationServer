namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>
/// Ideogram 4 text-to-image. fp8-only (no bf16 is published; the nvfp4 build needs Blackwell). Its distinctive trait
/// is a dual-model classifier-free guidance: a CONDITIONAL UNet and a separate UNCONDITIONAL UNet are fused by
/// <c>DualModelGuider</c> at the base CFG, while <c>CFGOverride</c> raises guidance to a second value over the last
/// 30% of the schedule. Sampling uses the model's own <c>Ideogram4Scheduler</c> (logit-normal sigmas) driven through
/// <c>SamplerCustomAdvanced</c>. Flux2 latent + flux2-vae, Qwen3-VL 8B encoder (CLIPLoader type "ideogram4"); the flow
/// shift (1.0) is baked in at load. VERY VRAM-heavy: BOTH ~9.3 GB UNets are resident during sampling. The
/// unconditional companion model is carried in the configuration's "motion_model" requirement slot.
/// The sampler path is an exact lift of the official Comfy-Org image_ideogram4_t2i template. Before that path, the
/// conditional model alone receives the frozen first-step residual correction. The separately loaded unconditional
/// model is deliberately untouched. The correction is installed as a reversible first-party ComfyUI node pack; no
/// checkpoint file is modified.
/// </summary>
public sealed class Ideogram4Workflow : Txt2ImgWorkflow<Ideogram4Params>
{
    public override string Name => "ideogram4";

    public override IReadOnlyList<ParamSpec> Schema =>
    [
        .. Txt2ImgWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.DebannerStrength, Type = ParamType.Double, Min = 0, Max = 2, Step = 0.01, Label = "Debanner Strength" },
        new() { Key = WorkflowParamKeys.CfgOverride, Type = ParamType.Double, Min = 1,   Max = 30, Label = "Late-step CFG" },
        new() { Key = WorkflowParamKeys.Mu,          Type = ParamType.Double, Min = -10, Max = 10, Label = "Schedule shift (mu)" },
        new() { Key = WorkflowParamKeys.Std,         Type = ParamType.Double, Min = 0.1, Max = 5,  Label = "Schedule spread (std)" },
    ];

    protected override ComfyWorkflowGraph Build(Ideogram4Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = RenderSize(p, req, inputs);
        ComfyWorkflowGraph g = new();

        // Conditional (req.Checkpoint) + unconditional (req.MotionModel slot) diffusion models.
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[Ideogram4WorkflowNodes.UncondModel] = ComfyGraph.DiffusionLoaderNode(req.RequiredMotionModel());
        g[Ideogram4WorkflowNodes.Debanner] = new Ideogram4CorrectionPatch
        {
            Model = UNETLoader.ModelOut(Nodes.Model),
            Enabled = p.DebannerStrength != 0,
            Strength = p.DebannerStrength,
        };
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Ideogram4, Device = ComfyWidgets.Device.Default };
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = CLIPLoader.ClipOut(Nodes.Clip) };
        g[Ideogram4WorkflowNodes.NegativeZeroOut] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Nodes.Positive) };

        // Asymmetric CFG: CFGOverride raises guidance on the conditional model over the last (1 - start_percent) of the
        // schedule; DualModelGuider then fuses the (override) conditional and the unconditional model at the base cfg.
        g[Ideogram4WorkflowNodes.CfgOverride] = new CFGOverride { Model = Ideogram4CorrectionPatch.Out(Ideogram4WorkflowNodes.Debanner), Cfg = p.CfgOverride, StartPercent = 0.7, EndPercent = 1.0 };
        g[Ideogram4WorkflowNodes.Guider] = new DualModelGuider
        {
            Model = CFGOverride.Out(Ideogram4WorkflowNodes.CfgOverride),
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            ModelNegative = UNETLoader.ModelOut(Ideogram4WorkflowNodes.UncondModel),
            Negative = ConditioningZeroOut.Out(Ideogram4WorkflowNodes.NegativeZeroOut),
            Cfg = p.RequiredCfg(),
        };

        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptyFlux2LatentImage) { Width = w, Height = h, BatchSize = 1 };
        g[Ideogram4WorkflowNodes.Sigmas] = new Ideogram4Scheduler { Steps = p.Steps, Width = w, Height = h, Mu = p.Mu, Std = p.Std };
        g[Ideogram4WorkflowNodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Ideogram4WorkflowNodes.Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[Ideogram4WorkflowNodes.Sampler] = new SamplerCustomAdvanced
        {
            Noise = RandomNoise.Out(Ideogram4WorkflowNodes.Noise),
            Guider = DualModelGuider.Out(Ideogram4WorkflowNodes.Guider),
            Sampler = KSamplerSelect.Out(Ideogram4WorkflowNodes.SamplerSelect),
            Sigmas = Ideogram4Scheduler.Out(Ideogram4WorkflowNodes.Sigmas),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };

        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Ideogram4WorkflowNodes.Sampler), Vae = VAELoader.VaeOut(Nodes.Vae) };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
