namespace ImageGen.Comfy;

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
/// per-layer conditioning rebalance (<see cref="Krea2Workflow.PostEncodePositive"/>) so the baked "uncensor" applies
/// to the shared positive conditioning exactly as it does for the plain krea2 / krea2-turbo configs.
/// </summary>
public sealed class Krea2RefineWorkflow : Krea2Workflow
{
    public override string Name => "krea2-refine";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = new Krea2Workflow().Schema.Concat(new ParamSpec[]
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

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));

        Dictionary<string, object> wf = new Dictionary<string, object>();

        // Base diffusion model (req.Checkpoint) + Turbo refiner (req.MotionModel slot). Shared Qwen3-VL encoder + VAE.
        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf[RefinerModel] = ComfyGraph.DiffusionLoader(req.RequiredMotionModel());
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "krea2", device = "default" });
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });

        object baseModel = ComfyGraph.ApplyLora(wf, ComfyGraph.Ref(Nodes.Model, 0), p);   // optional LoRA on the base model only
        object turboModel = ComfyGraph.Ref(RefinerModel, 0);
        object clipSrc = ComfyGraph.Ref(Nodes.Clip, 0);
        object vaeSrc = ComfyGraph.Ref(Nodes.Vae, 0);

        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clipSrc });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clipSrc });
        object posSrc = PostEncodePositive(wf, ComfyGraph.Ref(Nodes.Positive, 0), p);   // Krea 2 per-layer rebalance (node "13")
        object negSrc = ComfyGraph.Ref(Nodes.Negative, 0);

        wf[Nodes.Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptyLatentImage, new { width = w, height = h, batch_size = 1 });

        // Stage 1 — base: full denoise at real CFG for structure + prompt adherence.
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = baseModel,
            positive = posSrc,
            negative = negSrc,
            latent_image = ComfyGraph.Ref(Nodes.Latent, 0),
        });

        // Stage 2 — Turbo polish: partial-denoise pass over the base latent (no VAE round-trip). polish_denoise sets how
        // hard Turbo reworks it; Turbo is distilled (cfg 1, so the negative is inert here — passed for graph symmetry).
        wf[RefinerSampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.RefinerSteps),
            cfg = p.DblReq(WorkflowParamKeys.RefinerCfg),
            sampler_name = ComfyGraph.MapSampler(p.Has(WorkflowParamKeys.RefinerSampler) ? p.StrReq(WorkflowParamKeys.RefinerSampler) : p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.Has(WorkflowParamKeys.RefinerScheduler) ? p.StrReq(WorkflowParamKeys.RefinerScheduler) : p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = p.DblReq(WorkflowParamKeys.PolishDenoise),
            model = turboModel,
            positive = posSrc,
            negative = negSrc,
            latent_image = ComfyGraph.Ref(Nodes.Sampler, 0),
        });

        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(RefinerSampler, 0), vae = vaeSrc });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
