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

    /// <summary>The HunyuanVideo15SuperResolution node's input-field names (the srInputs-dict keys). Values are the
    /// ComfyUI input names, preserved exactly.</summary>
    private static class Inputs
    {
        public const string Positive = "positive";
        public const string Negative = "negative";
        public const string Latent = "latent";
        public const string NoiseAugmentation = "noise_augmentation";
        public const string Vae = "vae";
        public const string StartImage = "start_image";
        public const string ClipVisionOutput = "clip_vision_output";
    }

    /// <summary>SR knobs, appended to the HunyuanVideo 1.5 i2v/t2v schemas. <c>sr</c> is the on/off toggle; the rest
    /// carry the SR file names (literal, like the MoE <c>unet_low</c>) and the refine settings.</summary>
    public static readonly ParamSpec[] Schema =
    {
        new() { Key = WorkflowParamKeys.Sr,           Type = ParamType.Bool,   Label = "Super-resolution (1080p)" },
        new() { Key = WorkflowParamKeys.SrModel,     Type = ParamType.String, IsModelRef = true },   // SR distilled UNet filename
        new() { Key = WorkflowParamKeys.SrUpsampler, Type = ParamType.String, IsModelRef = true },   // latent upsampler filename
        new() { Key = WorkflowParamKeys.SrWidth,     Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.SrHeight,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.SrSteps,     Type = ParamType.Int,    Min = 1, Max = 50 },
        new() { Key = WorkflowParamKeys.SrDenoise,   Type = ParamType.Double, Min = 0.1, Max = 1.0 },
        new() { Key = WorkflowParamKeys.SrNoiseAug, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.SrCfg,       Type = ParamType.Double, Min = 1.0, Max = 12.0 },
        new() { Key = WorkflowParamKeys.SrShift,     Type = ParamType.Double, Min = 1.0, Max = 12.0 },
    };

    /// <summary>True when the config asked for SR and supplied an SR model file.</summary>
    public static bool Enabled(ParamValues p) => p.Bool(WorkflowParamKeys.Sr) && !string.IsNullOrWhiteSpace(p.Str(WorkflowParamKeys.SrModel));

    /// <summary>Append the SR pass and return its refined latent; returns <paramref name="baseLatent"/> unchanged
    /// when SR is off. <paramref name="positive"/>/<paramref name="negative"/> are the raw text-encode conditioning;
    /// <paramref name="startImage"/>/<paramref name="clipVisionOutput"/> are optional (null for t2v).</summary>
    public static object Refine(Dictionary<string, object> wf, ParamValues p, object baseLatent,
        object positive, object negative, object vae, object? startImage, object? clipVisionOutput, long seed)
    {
        if (!Enabled(p)) return baseLatent;

        wf[Nodes.UpsamplerLoader] = ComfyGraph.Node(ComfyNodeTypes.LatentUpscaleModelLoader, new { model_name = p.Model(WorkflowParamKeys.SrUpsampler) });
        wf[Nodes.LatentUpscale] = ComfyGraph.Node(ComfyNodeTypes.HunyuanVideo15LatentUpscaleWithModel, new
        {
            model = ComfyGraph.Ref(Nodes.UpsamplerLoader, 0), samples = baseLatent,
            upscale_method = "bilinear", width = p.IntReq(WorkflowParamKeys.SrWidth), height = p.IntReq(WorkflowParamKeys.SrHeight), crop = "disabled",
        });

        // The SR node re-emits a (positive, negative, latent) triple for the SR model (mirrors HunyuanVideo15ImageToVideo).
        // Required: positive/negative/latent/noise_augmentation; optional: vae/start_image/clip_vision_output.
        var srInputs = new Dictionary<string, object>
        {
            [Inputs.Positive] = positive,
            [Inputs.Negative] = negative,
            [Inputs.Latent] = ComfyGraph.Ref(Nodes.LatentUpscale, 0),
            [Inputs.NoiseAugmentation] = p.DblReq(WorkflowParamKeys.SrNoiseAug),
            [Inputs.Vae] = vae,
        };
        if (startImage is not null) srInputs[Inputs.StartImage] = startImage;
        if (clipVisionOutput is not null) srInputs[Inputs.ClipVisionOutput] = clipVisionOutput;
        wf[Nodes.SuperResolution] = ComfyGraph.Node(ComfyNodeTypes.HunyuanVideo15SuperResolution, srInputs);

        wf[Nodes.SrModel] = ComfyGraph.DiffusionLoader(p.Model(WorkflowParamKeys.SrModel));
        wf[Nodes.ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = ComfyGraph.Ref(Nodes.SrModel, 0), shift = p.DblReq(WorkflowParamKeys.SrShift) });
        object srModel = ComfyGraph.Ref(Nodes.ModelSampling, 0);
        wf[Nodes.Scheduler] = ComfyGraph.Node(ComfyNodeTypes.BasicScheduler, new { model = srModel, scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), steps = p.IntReq(WorkflowParamKeys.SrSteps), denoise = p.DblReq(WorkflowParamKeys.SrDenoise) });
        wf[Nodes.SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Nodes.Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = seed });
        wf[Nodes.Guider] = ComfyGraph.Node(ComfyNodeTypes.CFGGuider, new { model = srModel, positive = ComfyGraph.Ref(Nodes.SuperResolution, 0), negative = ComfyGraph.Ref(Nodes.SuperResolution, 1), cfg = p.DblReq(WorkflowParamKeys.SrCfg) });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Nodes.Noise, 0), guider = ComfyGraph.Ref(Nodes.Guider, 0), sampler = ComfyGraph.Ref(Nodes.SamplerSelect, 0), sigmas = ComfyGraph.Ref(Nodes.Scheduler, 0), latent_image = ComfyGraph.Ref(Nodes.SuperResolution, 2) });
        return ComfyGraph.Ref(Nodes.Sampler, 0);
    }
}
