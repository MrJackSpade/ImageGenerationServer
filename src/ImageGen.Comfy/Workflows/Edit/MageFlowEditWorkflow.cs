namespace ImageGen.Comfy;

/// <summary>
/// Mage-Flow-Edit instruction-based image editing (Mage-VAE + Native-Resolution DiT + Qwen3-VL). One unified node —
/// <c>TextEncodeMageFlowEdit</c> — takes the CLIP, the instruction (+ optional negative), the VAE and the reference
/// image(s) and emits (positive, negative) conditioning plus a zero latent sized to the output. The primary edited
/// image is <c>image_1</c>; extra references are <c>image_2..N</c>. Width/height are left 0 so the node follows the
/// (pre-scaled) reference size — Mage's RoPE aligns reference and target by position, so all references are resized
/// to the output resolution before encoding. The source is pre-scaled to the ~1MP native range (aligned to /16),
/// mirroring the official <c>image_mage_flow_edit_int8</c> template. Flow shift (6.0) is baked in at load.
/// </summary>
public abstract class MageFlowEditBase : EditWorkflowBase
{
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 512, MinH = 512, MaxW = 2048, MaxH = 2048, Step = 16 };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // UNETLoader / CLIPLoader(type=mage) / VAELoader + LoadImage at "10"

        // Pre-scale the source into Mage's native ~1MP range, aligned to a /16 grid (matches the template's
        // ImageScaleToTotalPixels: lanczos, 1.0 MP, 16-px steps). Keeps a large upload inside the training
        // distribution instead of asking the model to render at, e.g., 3000px.
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new
        {
            image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 16,
        });

        var enc = new Dictionary<string, object>
        {
            ["clip"] = clip0,
            ["prompt"] = inputs.Positive,
            ["negative_prompt"] = inputs.Negative ?? "",
            ["vae"] = vae0,
            ["width"] = 0,      // 0 -> follow the (scaled) reference's own size
            ["height"] = 0,
            ["batch_size"] = 1,
            ["image_1"] = ComfyGraph.Ref("11", 0),
        };

        // Extra reference images -> image_2, image_3, ... (scaled the same way).
        var refNames = inputs.ReferenceImageNames;
        int rn = p.Has("reference_max") ? Math.Min(refNames.Count, p.IntReq("reference_max")) : 0;   // no reference_max declared → no extra refs
        for (int i = 0; i < rn; i++)
        {
            string load = $"{40 + i * 2}", scale = $"{41 + i * 2}";
            wf[load] = ComfyGraph.Node("LoadImage", new { image = refNames[i] });
            wf[scale] = ComfyGraph.Node("ImageScaleToTotalPixels", new
            {
                image = ComfyGraph.Ref(load, 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 16,
            });
            enc[$"image_{i + 2}"] = ComfyGraph.Ref(scale, 0);
        }

        wf["5"] = ComfyGraph.Node("TextEncodeMageFlowEdit", enc);
        wf["6"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref("5", 0),
            negative = ComfyGraph.Ref("5", 1),
            latent_image = ComfyGraph.Ref("5", 2),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("6", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp" });
        return wf;
    }
}

/// <summary>Mage-Flow-Edit (RL-aligned) — full CFG (cfg 5, negatives supported), ~30 steps.</summary>
public sealed class MageFlowEditWorkflow : MageFlowEditBase { public override string Name => "mage-flow-edit"; }

/// <summary>Mage-Flow-Edit-Turbo — 4-step distilled, cfg 1 (no negative).</summary>
public sealed class MageFlowEditTurboWorkflow : MageFlowEditBase { public override string Name => "mage-flow-edit-turbo"; }
