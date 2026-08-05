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

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var (w, h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        var wf = new Dictionary<string, object>();

        // Conditional (req.Checkpoint) + unconditional (req.MotionModel slot) diffusion models.
        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf[UncondModel] = ComfyGraph.DiffusionLoader(req.RequiredMotionModel());
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "ideogram4", device = "default" });
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });

        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = ComfyGraph.Ref(Nodes.Clip, 0) });
        wf[NegativeZeroOut] = ComfyGraph.Node(ComfyNodeTypes.ConditioningZeroOut, new { conditioning = ComfyGraph.Ref(Nodes.Positive, 0) });

        // Asymmetric CFG: CFGOverride raises guidance on the conditional model over the last (1 - start_percent) of the
        // schedule; DualModelGuider then fuses the (override) conditional and the unconditional model at the base cfg.
        wf[CfgOverride] = ComfyGraph.Node(ComfyNodeTypes.CFGOverride, new { model = ComfyGraph.Ref(Nodes.Model, 0), cfg = p.DblReq(WorkflowParamKeys.CfgOverride), start_percent = 0.7, end_percent = 1.0 });
        wf[Guider] = ComfyGraph.Node(ComfyNodeTypes.DualModelGuider, new { model = ComfyGraph.Ref(CfgOverride, 0), positive = ComfyGraph.Ref(Nodes.Positive, 0), model_negative = ComfyGraph.Ref(UncondModel, 0), negative = ComfyGraph.Ref(NegativeZeroOut, 0), cfg = p.DblReq(WorkflowParamKeys.Cfg) });

        wf[Nodes.Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptyFlux2LatentImage, new { width = w, height = h, batch_size = 1 });
        wf[Sigmas] = ComfyGraph.Node(ComfyNodeTypes.Ideogram4Scheduler, new { steps = p.IntReq(WorkflowParamKeys.Steps), width = w, height = h, mu = p.DblReq(WorkflowParamKeys.Mu), std = p.DblReq(WorkflowParamKeys.Std) });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = ComfyGraph.Seed(p) });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Noise, 0), guider = ComfyGraph.Ref(Guider, 0), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas = ComfyGraph.Ref(Sigmas, 0), latent_image = ComfyGraph.Ref(Nodes.Latent, 0) });

        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = ComfyGraph.Ref(Nodes.Vae, 0) });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
