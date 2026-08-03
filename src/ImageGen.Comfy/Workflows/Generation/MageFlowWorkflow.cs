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
    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        int sw = p.Int("width", 1024), sh = p.Int("height", 1024);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        var enc = req.TextEncoders;
        var wf = new Dictionary<string, object>();

        wf["4"]  = ComfyGraph.DiffusionLoader(req.Checkpoint);   // UNETLoader (.safetensors int8_convrot / bf16)
        wf["20"] = ComfyGraph.Node("CLIPLoader", new { clip_name = enc.ElementAtOrDefault(0) ?? "", type = p.Str("clip_type") ?? "mage", device = "default" });
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });

        // Mage's unified conditioning node in its text-only mode: no image inputs -> pure t2i, and it also produces
        // the zero latent (batch×128×h/16×w/16) so the sampler's shape always matches the model. vae is unused here
        // (only edits encode reference latents), so it is left unconnected.
        wf["5"] = ComfyGraph.Node("TextEncodeMageFlowEdit", new
        {
            clip = ComfyGraph.Ref("20", 0),
            prompt = inputs.Positive,
            negative_prompt = inputs.Negative ?? "",
            width = w,
            height = h,
            batch_size = 1,
        });

        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 20),
            cfg = p.Dbl("cfg", 5),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model = ComfyGraph.Ref("4", 0),
            positive = ComfyGraph.Ref("5", 0),
            negative = ComfyGraph.Ref("5", 1),
            latent_image = ComfyGraph.Ref("5", 2),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = ComfyGraph.Ref("21", 0) });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}

/// <summary>Mage-Flow (RL-aligned) text-to-image — full CFG (cfg 5, negatives supported), ~20 steps.</summary>
public sealed class MageFlowWorkflow : MageFlowGenBase { public override string Name => "mage-flow"; }

/// <summary>Mage-Flow-Turbo text-to-image — 4-step distilled, cfg 1 (no negative).</summary>
public sealed class MageFlowTurboWorkflow : MageFlowGenBase { public override string Name => "mage-flow-turbo"; }
