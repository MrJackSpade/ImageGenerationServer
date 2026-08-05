using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Krea 2 two-stage refine parameters: the shared Krea 2 knobs (<see cref="Krea2Params"/>) plus the Turbo
/// polish pass — how hard Turbo reworks the base render (<c>polish_denoise</c>) and its own step count / CFG, with an
/// optional sampler/scheduler override (null → reuse the base pass's).</summary>
public sealed record Krea2RefineParams : Krea2Params
{
    [JsonPropertyName(WorkflowParamKeys.PolishDenoise)]    public required double PolishDenoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSteps)]     public required int RefinerSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerCfg)]       public required double RefinerCfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSampler)]   public string? RefinerSampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerScheduler)] public string? RefinerScheduler { get; init; }
}

/// <summary>
/// Krea 2 two-stage "base → Turbo polish" text-to-image. Stage 1 renders the whole image on the Krea 2 RAW base at
/// real CFG (structure + prompt adherence); stage 2 hands that latent to the distilled Krea 2 Turbo for a partial-
/// denoise polish pass (no VAE round-trip — both share the Qwen-Image/Wan2.1 VAE and the Qwen3-VL encoder, so the
/// latent flows straight through). Turbo runs its native few-step schedule at cfg 1, reworking texture and applying
/// Krea's aesthetic without redrawing the base composition. The single meaningful knob is <c>polish_denoise</c>: how
/// hard Turbo reworks the base render.
///
/// Model wiring mirrors <see cref="Ideogram4Workflow"/>'s dual-model pattern — the second diffusion model rides in
/// the configuration's <c>motion_model</c> requirement slot (resolved to <see cref="ResolvedRequirements.MotionModel"/>),
/// which also presence-gates the config on BOTH the RAW base and the Turbo weight being on disk. Inherits Krea 2's
/// per-layer conditioning rebalance (<see cref="Krea2Base{TParams}.PostEncodePositive"/>) so the baked "uncensor"
/// applies to the shared positive conditioning exactly as it does for the plain krea2 / krea2-turbo configs.
/// </summary>
public sealed class Krea2RefineWorkflow : Krea2Base<Krea2RefineParams>
{
    public override string Name => "krea2-refine";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = Txt2ImgWorkflowBase.SharedSchema.Concat(Krea2Rebalance.Schema).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.PolishDenoise, Type = ParamType.Double, Min = 0.1, Max = 0.9, Step = 0.01,
                Label = "Polish strength",
                Help = "How hard Turbo reworks the base render in the second pass. ~0.25–0.40 polishes texture and "
                     + "aesthetic while keeping the base composition; higher redraws more of the image (and can drift "
                     + "from the prompt). 0 would skip the polish entirely." },
        new() { Key = WorkflowParamKeys.RefinerSteps, Type = ParamType.Int, Min = 1, Max = 30,
                Label = "Polish steps",
                Help = "Turbo steps in the polish pass. 8 is the distilled sweet spot; the effective count is scaled by "
                     + "Polish strength (steps × denoise)." },
        new() { Key = WorkflowParamKeys.RefinerCfg, Type = ParamType.Double, Min = 1.0, Max = 4.0 },
        new() { Key = WorkflowParamKeys.RefinerSampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.RefinerScheduler, Type = ParamType.String },
    }).ToArray();

    /// <summary>Own nodes beyond the inherited txt2img roles
    /// (Nodes.Model/Clip/Vae/Positive/Negative/Latent/Sampler/Decode/Save reused below).</summary>
    private const string RefinerModel = "40";
    private const string RefinerSampler = "30";

    protected override ComfyWorkflowGraph Build(Krea2RefineParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));

        ComfyWorkflowGraph g = new ComfyWorkflowGraph();

        // Base diffusion model (req.Checkpoint) + Turbo refiner (req.MotionModel slot). Shared Qwen3-VL encoder + VAE.
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[RefinerModel] = ComfyGraph.DiffusionLoaderNode(req.RequiredMotionModel());
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = "krea2", Device = "default" };
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };

        Output<Slot.Model> baseModel = ComfyGraph.ApplyLora(g, UNETLoader.ModelOut(Nodes.Model), p.Lora, p.LoraStrength);   // optional LoRA on the base model only
        Output<Slot.Model> turboModel = UNETLoader.ModelOut(RefinerModel);
        Output<Slot.Clip> clipSrc = CLIPLoader.ClipOut(Nodes.Clip);
        Output<Slot.Vae> vaeSrc = VAELoader.VaeOut(Nodes.Vae);

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clipSrc };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clipSrc };
        Output<Slot.Conditioning> posSrc = PostEncodePositive(g, CLIPTextEncode.Out(Nodes.Positive), p);   // Krea 2 per-layer rebalance (node "13")
        Output<Slot.Conditioning> negSrc = CLIPTextEncode.Out(Nodes.Negative);

        g[Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptyLatentImage) { Width = w, Height = h, BatchSize = 1 };

        // Stage 1 — base: full denoise at real CFG for structure + prompt adherence.
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = baseModel,
            Positive = posSrc,
            Negative = negSrc,
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };

        // Stage 2 — Turbo polish: partial-denoise pass over the base latent (no VAE round-trip). polish_denoise sets how
        // hard Turbo reworks it; Turbo is distilled (cfg 1, so the negative is inert here — passed for graph symmetry).
        g[RefinerSampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.RefinerSteps,
            Cfg = p.RefinerCfg,
            SamplerName = ComfyGraph.MapSampler(p.RefinerSampler ?? p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.RefinerScheduler ?? p.Scheduler),
            Denoise = p.PolishDenoise,
            Model = turboModel,
            Positive = posSrc,
            Negative = negSrc,
            LatentImage = KSampler.Out(Nodes.Sampler),
        };

        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(RefinerSampler), Vae = vaeSrc };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = "forgemcp" };
        return g;
    }
}
