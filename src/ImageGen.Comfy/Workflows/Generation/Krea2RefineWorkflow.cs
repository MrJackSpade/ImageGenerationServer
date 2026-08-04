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
        new() { Key = "polish_denoise", Type = ParamType.Double, Min = 0.1, Max = 0.9, Step = 0.01,
                Label = "Polish strength",
                Help = "How hard Turbo reworks the base render in the second pass. ~0.25–0.40 polishes texture and "
                     + "aesthetic while keeping the base composition; higher redraws more of the image (and can drift "
                     + "from the prompt). 0 would skip the polish entirely." },
        new() { Key = "refiner_steps", Type = ParamType.Int, Min = 1, Max = 30,
                Label = "Polish steps",
                Help = "Turbo steps in the polish pass. 8 is the distilled sweet spot; the effective count is scaled by "
                     + "Polish strength (steps × denoise)." },
        new() { Key = "refiner_cfg", Type = ParamType.Double, Min = 1.0, Max = 4.0 },
        new() { Key = "refiner_sampler",   Type = ParamType.String },
        new() { Key = "refiner_scheduler", Type = ParamType.String },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var (w, h) = p.DimsReq("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect));

        var wf = new Dictionary<string, object>();

        // Base diffusion model (req.Checkpoint) + Turbo refiner (req.MotionModel slot). Shared Qwen3-VL encoder + VAE.
        wf["4"]  = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf["40"] = ComfyGraph.DiffusionLoader(req.RequiredMotionModel());
        wf["20"] = ComfyGraph.Node("CLIPLoader", new { clip_name = req.TextEncoder(0), type = "krea2", device = "default" });
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.RequiredVae() });

        object baseModel = ComfyGraph.ApplyLora(wf, ComfyGraph.Ref("4", 0), p);   // optional LoRA on the base model only
        object turboModel = ComfyGraph.Ref("40", 0);
        object clipSrc = ComfyGraph.Ref("20", 0);
        object vaeSrc = ComfyGraph.Ref("21", 0);

        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clipSrc });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clipSrc });
        object posSrc = PostEncodePositive(wf, ComfyGraph.Ref("6", 0), p);   // Krea 2 per-layer rebalance (node "13")
        object negSrc = ComfyGraph.Ref("7", 0);

        wf["5"] = ComfyGraph.Node("EmptyLatentImage", new { width = w, height = h, batch_size = 1 });

        // Stage 1 — base: full denoise at real CFG for structure + prompt adherence.
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = baseModel,
            positive = posSrc,
            negative = negSrc,
            latent_image = ComfyGraph.Ref("5", 0),
        });

        // Stage 2 — Turbo polish: partial-denoise pass over the base latent (no VAE round-trip). polish_denoise sets how
        // hard Turbo reworks it; Turbo is distilled (cfg 1, so the negative is inert here — passed for graph symmetry).
        wf["30"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("refiner_steps"),
            cfg = p.DblReq("refiner_cfg"),
            sampler_name = ComfyGraph.MapSampler(p.Has("refiner_sampler") ? p.StrReq("refiner_sampler") : p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Has("refiner_scheduler") ? p.StrReq("refiner_scheduler") : p.StrReq("scheduler")),
            denoise = p.DblReq("polish_denoise"),
            model = turboModel,
            positive = posSrc,
            negative = negSrc,
            latent_image = ComfyGraph.Ref("3", 0),
        });

        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("30", 0), vae = vaeSrc });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
