namespace ImageGen.Comfy;

/// <summary>
/// HunyuanImage 2.1 text→image (2K native). A diffusion-transformer image model with dual text encoders
/// (Qwen2.5-VL + ByT5-glyph, the SAME two files HunyuanVideo 1.5 already ships, reused by requirement id) and a
/// 32× VAE. Its own graph because it diverges from the shared txt2img base on two nodes: <c>ModelSamplingSD3</c>
/// (flow-shift 5, not AuraFlow) and <c>EmptyHunyuanImageLatent</c> (not the std/SD3/Flux2 latents the base offers).
/// The distilled build runs ~8 steps at low CFG (meanflow distillation); the standard build ~50 steps at CFG 3.5.
/// The refiner stage isn't implemented natively in ComfyUI yet, so it's intentionally omitted. ~17 GB fp8 unet +
/// the Qwen encoder → 24 GB tier (gated by the config's min_vram_mb).
/// </summary>
public sealed class HunyuanImage21Workflow : Txt2ImgWorkflowBase
{
    public override string Name => "hunyuanimage21";
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Shift, Type = ParamType.Double, Min = 1.0, Max = 12.0, Label = "Flow shift" },
    }).ToArray();

    /// <summary>This workflow's flow-shift node id (reuses the inherited txt2img <c>Nodes.*</c>).</summary>
    private const string ModelSampling = "30";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));

        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = ComfyGraph.Ref(Nodes.Model, 0), shift = p.DblReq(WorkflowParamKeys.Shift) });
        object model = ComfyGraph.ApplyLora(wf, ComfyGraph.Ref(ModelSampling, 0), p);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.DualCLIPLoader, new { clip_name1 = req.TextEncoder(0), clip_name2 = req.TextEncoder(1), type = "hunyuan_image", device = "default" });
        object clip = ComfyGraph.Ref(Nodes.Clip, 0);
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        object vae = ComfyGraph.Ref(Nodes.Vae, 0);

        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip });
        wf[Nodes.Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptyHunyuanImageLatent, new { width = w, height = h, batch_size = 1 });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model,
            positive = ComfyGraph.Ref(Nodes.Positive, 0),
            negative = ComfyGraph.Ref(Nodes.Negative, 0),
            latent_image = ComfyGraph.Ref(Nodes.Latent, 0),
        });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
        return wf;
    }
}
