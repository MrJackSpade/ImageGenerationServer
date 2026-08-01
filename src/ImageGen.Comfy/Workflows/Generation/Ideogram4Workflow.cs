namespace ImageGen.Comfy;

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
public sealed class Ideogram4Workflow : Txt2ImgWorkflowBase
{
    public override string Name => "ideogram4";

    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(new ParamSpec[]
    {
        new() { Key = "cfg_override", Type = ParamType.Double, Default = 3.0,  Min = 1,   Max = 30, Label = "Late-step CFG" },
        new() { Key = "mu",          Type = ParamType.Double, Default = 0.5,  Min = -10, Max = 10, Label = "Schedule shift (mu)" },
        new() { Key = "std",         Type = ParamType.Double, Default = 1.75, Min = 0.1, Max = 5,  Label = "Schedule spread (std)" },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        int sw = p.Int("width", 1024), sh = p.Int("height", 1024);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        var enc = req.TextEncoders;
        var wf = new Dictionary<string, object>();

        // Conditional (req.Checkpoint) + unconditional (req.MotionModel slot) diffusion models.
        wf["4"]  = ComfyGraph.DiffusionLoader(req.Checkpoint);
        wf["40"] = ComfyGraph.DiffusionLoader(req.MotionModel ?? "");
        wf["20"] = ComfyGraph.Node("CLIPLoader", new { clip_name = enc.ElementAtOrDefault(0) ?? "", type = p.Str("clip_type") ?? "ideogram4", device = "default" });
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });

        wf["6"]  = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = ComfyGraph.Ref("20", 0) });
        wf["26"] = ComfyGraph.Node("ConditioningZeroOut", new { conditioning = ComfyGraph.Ref("6", 0) });

        // Asymmetric CFG: CFGOverride raises guidance on the conditional model over the last (1 - start_percent) of the
        // schedule; DualModelGuider then fuses the (override) conditional and the unconditional model at the base cfg.
        wf["2"]  = ComfyGraph.Node("CFGOverride", new { model = ComfyGraph.Ref("4", 0), cfg = p.Dbl("cfg_override", 3.0), start_percent = 0.7, end_percent = 1.0 });
        wf["22"] = ComfyGraph.Node("DualModelGuider", new { model = ComfyGraph.Ref("2", 0), positive = ComfyGraph.Ref("6", 0), model_negative = ComfyGraph.Ref("40", 0), negative = ComfyGraph.Ref("26", 0), cfg = p.Dbl("cfg", 7.0) });

        wf["5"]  = ComfyGraph.Node("EmptyFlux2LatentImage", new { width = w, height = h, batch_size = 1 });
        wf["17"] = ComfyGraph.Node("Ideogram4Scheduler", new { steps = p.Int("steps", 20), width = w, height = h, mu = p.Dbl("mu", 0.5), std = p.Dbl("std", 1.75) });
        wf["16"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.Str("sampler")) });
        wf["18"] = ComfyGraph.Node("RandomNoise", new { noise_seed = ComfyGraph.Seed(p) });
        wf["23"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("18", 0), guider = ComfyGraph.Ref("22", 0), sampler = ComfyGraph.Ref("16", 0), sigmas = ComfyGraph.Ref("17", 0), latent_image = ComfyGraph.Ref("5", 0) });

        wf["8"]  = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("23", 0), vae = ComfyGraph.Ref("21", 0) });
        wf["9"]  = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
