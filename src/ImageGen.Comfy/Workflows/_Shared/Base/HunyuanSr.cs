using ImageGen.Application.Rendering;

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
    /// <summary>The SR pass's node ids, named by role. Values (70–79) are preserved exactly so the emitted graph stays
    /// byte-identical; the names replace the bare literals at the use sites.</summary>
    private static class Nodes
    {
        public const string UpsamplerLoader = "70";
        public const string LatentUpscale = "71";
        public const string SuperResolution = "72";
        public const string SrModel = "73";
        public const string ModelSampling = "74";
        public const string Scheduler = "75";
        public const string SamplerSelect = "76";
        public const string Noise = "77";
        public const string Guider = "78";
        public const string Sampler = "79";
    }

    /// <summary>SR knobs, appended to the HunyuanVideo 1.5 i2v/t2v schemas. <c>sr</c> is the on/off toggle; the rest
    /// carry the SR file names (literal, like the MoE <c>unet_low</c>) and the refine settings.</summary>
    public static readonly ParamSpec[] Schema =
    [
        new() { Key = WorkflowParamKeys.Sr,           Type = ParamType.Bool,   Label = "Super-resolution (1080p)" },
        new() { Key = WorkflowParamKeys.SrModel,     Type = ParamType.String, IsModelRef = true },   // SR distilled UNet filename
        new() { Key = WorkflowParamKeys.SrUpsampler, Type = ParamType.String, IsModelRef = true },   // latent upsampler filename
        new() { Key = WorkflowParamKeys.SrWidth,     Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.SrHeight,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.SrSteps,     Type = ParamType.Int,    Min = 1, Max = 50 },
        new() { Key = WorkflowParamKeys.SrDenoise,   Type = ParamType.Double, Min = ParamBounds.DenoiseMin, Max = ParamBounds.DenoiseMax, Step = 0.01 },
        new() { Key = WorkflowParamKeys.SrNoiseAug, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.SrCfg,       Type = ParamType.Double, Min = 1.0, Max = 12.0 },
        new() { Key = WorkflowParamKeys.SrShift,     Type = ParamType.Double, Min = 1.0, Max = 12.0 },
    ];

    /// <summary>The SR pass for these params, or null when SR is off — the toggle is the params SHAPE (a concrete SR
    /// subtype implements <see cref="IHunyuanSrPass"/>) plus a supplied SR model file. Callers gate <see cref="Refine"/>
    /// and the tiled decode on a non-null result.</summary>
    public static IHunyuanSrPass? PassOf(object? p)
    {
        if (p is not IHunyuanSrPass sr)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sr.SrModel))
        {
            throw new RenderValidationException(
                $"Super-resolution is enabled, but '{WorkflowParamKeys.SrModel}' is blank.");
        }

        if (string.IsNullOrWhiteSpace(sr.SrUpsampler))
        {
            throw new RenderValidationException(
                $"Super-resolution is enabled, but '{WorkflowParamKeys.SrUpsampler}' is blank.");
        }

        return sr;
    }

    /// <summary>Append the SR pass over a typed <see cref="ComfyWorkflowGraph"/> and return its refined latent. Called
    /// only for an SR config (the caller gates on <see cref="PassOf"/>), so every knob on <paramref name="p"/> is present.
    /// <paramref name="positive"/>/<paramref name="negative"/> are the raw text-encode conditioning;
    /// <paramref name="startImage"/>/<paramref name="clipVisionOutput"/> are optional (null for t2v — omitted from the SR
    /// node). <paramref name="sampler"/>/<paramref name="scheduler"/> are the ALREADY-MAPPED ComfyUI names.</summary>
    public static Output<Slot.Latent> Refine(ComfyWorkflowGraph g, IHunyuanSrPass p, Output<Slot.Latent> baseLatent,
        Output<Slot.Conditioning> positive, Output<Slot.Conditioning> negative, Output<Slot.Vae> vae,
        Output<Slot.Image>? startImage, Output<Slot.ClipVision>? clipVisionOutput, string sampler, string scheduler, long seed)
    {
        g[Nodes.UpsamplerLoader] = new LatentUpscaleModelLoader { ModelName = p.SrUpsampler };
        g[Nodes.LatentUpscale] = new HunyuanVideo15LatentUpscaleWithModel
        {
            Model = LatentUpscaleModelLoader.Out(Nodes.UpsamplerLoader),
            Samples = baseLatent,
            UpscaleMethod = ComfyWidgets.Upscale.Bilinear,
            Width = p.SrWidth,
            Height = p.SrHeight,
            Crop = ComfyWidgets.Crop.Disabled,
        };
        // The SR node re-emits a (positive, negative, latent) triple for the SR model (mirrors HunyuanVideo15ImageToVideo).
        // start_image/clip_vision_output ride the i2v path only — they arrive together (i2v) or not at all (t2v), which is a
        // choice of NODE, not a pair of conditional-nullable inputs.
        Output<Slot.Latent> upscaled = HunyuanVideo15LatentUpscaleWithModel.Out(Nodes.LatentUpscale);
        g[Nodes.SuperResolution] = (startImage, clipVisionOutput) switch
        {
            ({ } start, { } clip) => new HunyuanVideo15SuperResolutionI2V
            {
                Positive = positive,
                Negative = negative,
                Latent = upscaled,
                NoiseAugmentation = p.SrNoiseAug,
                Vae = vae,
                StartImage = start,
                ClipVisionOutput = clip,
            },
            (null, null) => new HunyuanVideo15SuperResolutionT2V
            {
                Positive = positive,
                Negative = negative,
                Latent = upscaled,
                NoiseAugmentation = p.SrNoiseAug,
                Vae = vae,
            },
            _ => throw new InvalidOperationException(
                "HunyuanVideo 1.5 SR needs start_image and clip_vision_output supplied together (i2v) or both omitted (t2v)."),
        };

        g[Nodes.SrModel] = ComfyGraph.DiffusionLoaderNode(p.SrModel);
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.SrModel), Shift = p.SrShift };
        Output<Slot.Model> srModel = ModelSamplingSD3.Out(Nodes.ModelSampling);
        g[Nodes.Scheduler] = new BasicScheduler { Model = srModel, Scheduler = scheduler, Steps = p.SrSteps, Denoise = p.SrDenoise };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = sampler };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[Nodes.Guider] = new CFGGuider { Model = srModel, Positive = IHunyuanVideo15SuperResolution.PositiveOut(Nodes.SuperResolution), Negative = IHunyuanVideo15SuperResolution.NegativeOut(Nodes.SuperResolution), Cfg = p.SrCfg };
        g[Nodes.Sampler] = new SamplerCustomAdvanced
        {
            Noise = RandomNoise.Out(Nodes.Noise),
            Guider = CFGGuider.Out(Nodes.Guider),
            Sampler = KSamplerSelect.Out(Nodes.SamplerSelect),
            Sigmas = BasicScheduler.Out(Nodes.Scheduler),
            LatentImage = IHunyuanVideo15SuperResolution.LatentOut(Nodes.SuperResolution),
        };
        return SamplerCustomAdvanced.Out(Nodes.Sampler);
    }
}
