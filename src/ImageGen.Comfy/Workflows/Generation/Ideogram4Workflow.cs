using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Ideogram 4's extra knobs: the late-step CFG override, and the mu/std of its own logit-normal schedule.</summary>
public sealed record Ideogram4Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.CfgOverride)]
    [Range(1.0, 30.0)] public required double CfgOverride { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Mu)]
    [Range(-10.0, 10.0)] public required double Mu { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Std)]
    [Range(0.1, 5.0)] public required double Std { get; init; }
}

/// <summary>
/// Ideogram 4 text-to-image. fp8-only (no bf16 is published; the nvfp4 build needs Blackwell). Its distinctive trait
/// is a dual-model classifier-free guidance: a CONDITIONAL UNet and a separate UNCONDITIONAL UNet are fused by
/// <c>DualModelGuider</c> at the base CFG, while <c>CFGOverride</c> raises guidance to a second value over the last
/// 30% of the schedule. Sampling uses the model's own <c>Ideogram4Scheduler</c> (logit-normal sigmas) driven through
/// <c>SamplerCustomAdvanced</c>. Flux2 latent + flux2-vae, Qwen3-VL 8B encoder (CLIPLoader type "ideogram4"); the flow
/// shift (1.0) is baked in at load. VERY VRAM-heavy: BOTH ~9.3 GB UNets are resident during sampling. The
/// unconditional companion model is carried in the configuration's "motion_model" requirement slot.
/// Exact lift of the official Comfy-Org image_ideogram4_t2i template's sampling path.
///
/// NOTE: its configuration is currently <b>hidden</b> (visible:false in workflows.json). Ideogram 4 only renders from
/// a full structured-JSON caption; a raw natural-language prompt trips a safety placeholder baked into the weights
/// ("Image blocked by safety filter"). The official workflow does NOT contain an LLM — it ships a system prompt the
/// user pastes into their own chat model to produce the JSON. Making plain prompts work here needs an NL->JSON
/// rewriter (an LLM running that system prompt) which the app no longer has. Re-enable once that exists.
/// </summary>
public sealed class Ideogram4Workflow : Txt2ImgWorkflow<Ideogram4Params>
{
    public override string Name => "ideogram4";

    public override IReadOnlyList<ParamSpec> Schema => Txt2ImgWorkflowBase.SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.CfgOverride, Type = ParamType.Double, Min = 1,   Max = 30, Label = "Late-step CFG" },
        new() { Key = WorkflowParamKeys.Mu,          Type = ParamType.Double, Min = -10, Max = 10, Label = "Schedule shift (mu)" },
        new() { Key = WorkflowParamKeys.Std,         Type = ParamType.Double, Min = 0.1, Max = 5,  Label = "Schedule spread (std)" },
    }).ToArray();

    /// <summary>Own node ids beyond the inherited txt2img roles.</summary>
    private const string UncondModel = "40";
    private const string NegativeZeroOut = "26";
    private const string CfgOverride = "2";
    private const string Guider = "22";
    private const string Sigmas = "17";
    private const string SamplerSelect = "16";
    private const string Noise = "18";
    private const string Sampler = "23";

    protected override ComfyWorkflowGraph Build(Ideogram4Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();

        // Conditional (req.Checkpoint) + unconditional (req.MotionModel slot) diffusion models.
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[UncondModel] = ComfyGraph.DiffusionLoaderNode(req.RequiredMotionModel());
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Ideogram4, Device = ComfyWidgets.Device.Default };
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = CLIPLoader.ClipOut(Nodes.Clip) };
        g[NegativeZeroOut] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Nodes.Positive) };

        // Asymmetric CFG: CFGOverride raises guidance on the conditional model over the last (1 - start_percent) of the
        // schedule; DualModelGuider then fuses the (override) conditional and the unconditional model at the base cfg.
        g[CfgOverride] = new CFGOverride { Model = UNETLoader.ModelOut(Nodes.Model), Cfg = p.CfgOverride, StartPercent = 0.7, EndPercent = 1.0 };
        g[Guider] = new DualModelGuider
        {
            Model = ImageGen.Comfy.CFGOverride.Out(CfgOverride),
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            ModelNegative = UNETLoader.ModelOut(UncondModel),
            Negative = ConditioningZeroOut.Out(NegativeZeroOut),
            Cfg = p.RequiredCfg(),
        };

        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptyFlux2LatentImage) { Width = w, Height = h, BatchSize = 1 };
        g[Sigmas] = new Ideogram4Scheduler { Steps = p.Steps, Width = w, Height = h, Mu = p.Mu, Std = p.Std };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[Sampler] = new SamplerCustomAdvanced
        {
            Noise = RandomNoise.Out(Noise),
            Guider = DualModelGuider.Out(Guider),
            Sampler = KSamplerSelect.Out(SamplerSelect),
            Sigmas = Ideogram4Scheduler.Out(Sigmas),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };

        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Sampler), Vae = VAELoader.VaeOut(Nodes.Vae) };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
