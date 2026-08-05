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
public abstract class MageFlowGenBase : Txt2ImgWorkflowBase
{
    /// <summary>Mage's unified text-encode + zero-latent node, emitting the (positive, negative, latent) triple.</summary>
    private const string Encode = "5";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var (w, h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        var wf = new Dictionary<string, object>();

        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());   // UNETLoader (.safetensors int8_convrot / bf16)
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "mage", device = "default" });
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });

        // Mage's unified conditioning node in its text-only mode: no image inputs -> pure t2i, and it also produces
        // the zero latent (batch×128×h/16×w/16) so the sampler's shape always matches the model. vae is unused here
        // (only edits encode reference latents), so it is left unconnected.
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.TextEncodeMageFlowEdit, new
        {
            clip = ComfyGraph.Ref(Nodes.Clip, 0),
            prompt = inputs.Positive,
            negative_prompt = inputs.Negative ?? "",
            width = w,
            height = h,
            batch_size = 1,
        });

        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = ComfyGraph.Ref(Nodes.Model, 0),
            positive = ComfyGraph.Ref(Encode, 0),
            negative = ComfyGraph.Ref(Encode, 1),
            latent_image = ComfyGraph.Ref(Encode, 2),
        });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = ComfyGraph.Ref(Nodes.Vae, 0) });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp" });
        return wf;
    }
}

/// <summary>Mage-Flow (RL-aligned) text-to-image — full CFG (cfg 5, negatives supported), ~20 steps.</summary>
public sealed class MageFlowWorkflow : MageFlowGenBase { public override string Name => "mage-flow"; }

/// <summary>Mage-Flow-Turbo text-to-image — 4-step distilled, cfg 1 (no negative).</summary>
public sealed class MageFlowTurboWorkflow : MageFlowGenBase { public override string Name => "mage-flow-turbo"; }
