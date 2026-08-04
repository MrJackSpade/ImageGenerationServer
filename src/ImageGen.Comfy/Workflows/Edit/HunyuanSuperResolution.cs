namespace ImageGen.Comfy;

/// <summary>
/// HunyuanVideo 1.5 super-resolution second pass — the optional latent-space upscale-and-refine stage from the
/// official ComfyUI i2v/t2v SR template. When a configuration sets <c>sr=true</c> (and supplies the SR distilled
/// UNet + latent upsampler filenames via <c>sr_model</c>/<c>sr_upsampler</c>, with those requirement ids linked in
/// the config's <c>extra</c> so the row is presence-gated), <see cref="Refine"/> appends:
/// <list type="number">
///   <item><c>LatentUpscaleModelLoader</c> + <c>HunyuanVideo15LatentUpscaleWithModel</c> — rescale the generated
///   latent sequence to the SR target (1920×1080 by default) in latent space.</item>
///   <item><c>HunyuanVideo15SuperResolution</c> — re-emit the (positive, negative, latent) triple conditioned for
///   the SR model (optionally with the source image + CLIP-vision cues, which i2v supplies and t2v omits).</item>
///   <item>A dedicated SR-model sampling chain (its own <c>UNETLoader</c> + <c>ModelSamplingSD3</c> shift, a
///   <c>BasicScheduler</c> at the SR denoise, and <c>SamplerCustomAdvanced</c>) that refines fine detail.</item>
/// </list>
/// Returns the refined latent, or the input latent unchanged when SR is off. Two UNets are resident during SR, so
/// SR configs are gated to the 24 GB tier. Node ids 70–79 to avoid colliding with the i2v/t2v base graphs.
/// NOTE: faithful to the template but not yet smoke-tested live — validate on the 24 GB box after deploy.
/// </summary>
internal static class HunyuanSr
{
    /// <summary>SR knobs, appended to the HunyuanVideo 1.5 i2v/t2v schemas. <c>sr</c> is the on/off toggle; the rest
    /// carry the SR file names (literal, like the MoE <c>unet_low</c>) and the refine settings.</summary>
    public static readonly ParamSpec[] Schema =
    {
        new() { Key = "sr",           Type = ParamType.Bool,   Label = "Super-resolution (1080p)" },
        new() { Key = "sr_model",     Type = ParamType.String, IsModelRef = true },   // SR distilled UNet filename
        new() { Key = "sr_upsampler", Type = ParamType.String, IsModelRef = true },   // latent upsampler filename
        new() { Key = "sr_width",     Type = ParamType.Int },
        new() { Key = "sr_height",    Type = ParamType.Int },
        new() { Key = "sr_steps",     Type = ParamType.Int,    Min = 1, Max = 50 },
        new() { Key = "sr_denoise",   Type = ParamType.Double, Min = 0.1, Max = 1.0 },
        new() { Key = "sr_noise_aug", Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "sr_cfg",       Type = ParamType.Double, Min = 1.0, Max = 12.0 },
        new() { Key = "sr_shift",     Type = ParamType.Double, Min = 1.0, Max = 12.0 },
    };

    /// <summary>True when the config asked for SR and supplied an SR model file.</summary>
    public static bool Enabled(ParamValues p) => p.Bool("sr") && !string.IsNullOrWhiteSpace(p.Str("sr_model"));

    /// <summary>Append the SR pass and return its refined latent; returns <paramref name="baseLatent"/> unchanged
    /// when SR is off. <paramref name="positive"/>/<paramref name="negative"/> are the raw text-encode conditioning;
    /// <paramref name="startImage"/>/<paramref name="clipVisionOutput"/> are optional (null for t2v).</summary>
    public static object Refine(Dictionary<string, object> wf, ParamValues p, object baseLatent,
        object positive, object negative, object vae, object? startImage, object? clipVisionOutput, long seed)
    {
        if (!Enabled(p)) return baseLatent;

        wf["70"] = ComfyGraph.Node("LatentUpscaleModelLoader", new { model_name = p.Model("sr_upsampler") });
        wf["71"] = ComfyGraph.Node("HunyuanVideo15LatentUpscaleWithModel", new
        {
            model = ComfyGraph.Ref("70", 0), samples = baseLatent,
            upscale_method = "bilinear", width = p.IntReq("sr_width"), height = p.IntReq("sr_height"), crop = "disabled",
        });

        // The SR node re-emits a (positive, negative, latent) triple for the SR model (mirrors HunyuanVideo15ImageToVideo).
        // Required: positive/negative/latent/noise_augmentation; optional: vae/start_image/clip_vision_output.
        var srInputs = new Dictionary<string, object>
        {
            ["positive"] = positive,
            ["negative"] = negative,
            ["latent"] = ComfyGraph.Ref("71", 0),
            ["noise_augmentation"] = p.DblReq("sr_noise_aug"),
            ["vae"] = vae,
        };
        if (startImage is not null) srInputs["start_image"] = startImage;
        if (clipVisionOutput is not null) srInputs["clip_vision_output"] = clipVisionOutput;
        wf["72"] = ComfyGraph.Node("HunyuanVideo15SuperResolution", srInputs);

        wf["73"] = ComfyGraph.DiffusionLoader(p.Model("sr_model"));
        wf["74"] = ComfyGraph.Node("ModelSamplingSD3", new { model = ComfyGraph.Ref("73", 0), shift = p.DblReq("sr_shift") });
        object srModel = ComfyGraph.Ref("74", 0);
        wf["75"] = ComfyGraph.Node("BasicScheduler", new { model = srModel, scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")), steps = p.IntReq("sr_steps"), denoise = p.DblReq("sr_denoise") });
        wf["76"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        wf["77"] = ComfyGraph.Node("RandomNoise", new { noise_seed = seed });
        wf["78"] = ComfyGraph.Node("CFGGuider", new { model = srModel, positive = ComfyGraph.Ref("72", 0), negative = ComfyGraph.Ref("72", 1), cfg = p.DblReq("sr_cfg") });
        wf["79"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("77", 0), guider = ComfyGraph.Ref("78", 0), sampler = ComfyGraph.Ref("76", 0), sigmas = ComfyGraph.Ref("75", 0), latent_image = ComfyGraph.Ref("72", 2) });
        return ComfyGraph.Ref("79", 0);
    }
}
