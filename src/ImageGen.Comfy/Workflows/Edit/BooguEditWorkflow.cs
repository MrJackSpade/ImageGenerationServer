namespace ImageGen.Comfy;

/// <summary>
/// Boogu-Image-0.1-Edit (instruction image edit / TI2I). Built on ComfyUI's native <c>TextEncodeBooguEdit</c> node,
/// which does, in one node, the reference plumbing the Qwen/Flux editors wire up by hand: it caps the reference to
/// the VLM's 384px vision input, VAE-encodes a ~1K (16px-aligned) reference latent, and emits BOTH conditionings —
/// the instruction's vision tokens on the positive only, the reference latent on positive AND negative (so it cancels
/// under CFG and preserves identity while CFG amplifies the instruction). We just hand it the source image and sample
/// from a matching-size empty latent.
///
/// ComfyUI's Boogu model class bakes the flow shift (3.16) in at load, so there is no ModelSampling node here. The
/// model card targets ~1K reference resolution, so the source is scaled to 1MP on a 16px grid (FLUX VAE /8 x patch 2)
/// and that same scaled image both feeds the encode node and seeds the sampling latent (denoise 1.0, shape only). The
/// 0.1 Edit release supports a single reference image, so only the source is used (no extra reference slots).
/// </summary>
public sealed class BooguEditWorkflow : EditWorkflowBase
{
    public override string Name => "boogu-edit";

    /// <summary>Boogu runs real CFG with an (optionally empty) negative; expose it like the inpaint editor does.</summary>
    public override IReadOnlyList<ParamSpec> Schema => BooguSchema;
    private static readonly IReadOnlyList<ParamSpec> BooguSchema = SharedSchema
        .Concat(new ParamSpec[]
        {
            new() { Key = "negative",   Type = ParamType.String },
            new() { Key = "megapixels", Type = ParamType.Double, Min = 0.5, Max = 4.0, Label = "Edit resolution (MP)" },
        })
        .ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // 4=unet 5=clip(boogu) 6=vae 10=LoadImage(source)

        // Lift of the official Comfy-Org image_boogu_image_0_1_edit template. Resize the source to ~1 MP (lanczos) —
        // 1 MP is what the template uses; rendering bigger than the model's ~1 MP reference just soft-upscales. The
        // "megapixels" param stays for tuning but defaults to 1.0.
        double mp = p.DblReq("megapixels");
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = mp, resolution_steps = 16 });

        // Apply the flow-matching shift EXPLICITLY (the template does this even though Boogu's model class also carries
        // 3.16) — sampling quality depends on it being on the model the scheduler/sampler see.
        wf["33"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = model0, shift = 3.16 });
        object modelS = ComfyGraph.Ref("33", 0);

        // Boogu edit conditioning: instruction (+ vision tokens) on positive, empty/explicit negative => DROP. The node
        // VAE-encodes the reference itself and returns positive[0] / negative[1] with the reference latent on both. The
        // reference is the node's "images" Autogrow (COMFY_AUTOGROW_V3) input, keyed by its finalized dotted path
        // "images.image_1" (id "." template-name), which the v3 executor rebuilds into images={"image_1": <IMAGE>}. A
        // bare "image_1" is rejected; the dotted key needs a Dictionary (a C# anonymous type can't express the dot).
        var neg = inputs.Negative ?? p.Str("negative") ?? "";
        wf["13"] = ComfyGraph.Node("TextEncodeBooguEdit", new Dictionary<string, object>
        {
            ["clip"] = clip0,
            ["prompt"] = inputs.Positive,
            ["negative_prompt"] = neg,
            ["vae"] = vae0,
            ["images.image_1"] = ComfyGraph.Ref("11", 0),
        });

        // Output latent: an EMPTY latent sized to the resized source (template uses GetImageSize -> EmptyLatentImage),
        // NOT a VAEEncode of the source. Sample with SamplerCustom + KSamplerSelect(dpmpp_2m) + BasicScheduler sigmas —
        // a plain euler KSampler (what this used to do) is what produced the soft/blurry edits.
        wf["17"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["50"] = ComfyGraph.Node("EmptyLatentImage", new { width = ComfyGraph.Ref("17", 0), height = ComfyGraph.Ref("17", 1), batch_size = 1 });
        wf["16"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        wf["26"] = ComfyGraph.Node("BasicScheduler", new { model = modelS, scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")), steps = p.IntReq("steps"), denoise = 1.0 });

        wf["3"] = ComfyGraph.Node("SamplerCustom", new
        {
            model = modelS,
            add_noise = true,
            noise_seed = ComfyGraph.Seed(p),
            cfg = p.DblReq("cfg"),
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("13", 1),
            sampler = ComfyGraph.Ref("16", 0),
            sigmas = ComfyGraph.Ref("26", 0),
            latent_image = ComfyGraph.Ref("50", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
