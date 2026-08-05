using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>The typed view of the SR knobs a HunyuanVideo 1.5 i2v/t2v params record exposes for the super-resolution
/// second pass. The values are nullable because SR is optional — a config that leaves <c>sr</c> off supplies none of
/// them; the typed <see cref="HunyuanSr.Refine"/> reads them (with a fail-fast refusal on an absent value) only once
/// <see cref="HunyuanSr.Enabled(IHunyuanSrParams)"/> is true.</summary>
public interface IHunyuanSrParams
{
    /// <summary>The SR on/off toggle.</summary>
    bool Sr { get; }
    /// <summary>The SR distilled UNet filename (resolved model ref); null/blank means SR is not actually wired.</summary>
    string? SrModel { get; }
    /// <summary>The latent upsampler filename (resolved model ref).</summary>
    string? SrUpsampler { get; }
    /// <summary>The SR latent-upscale target width.</summary>
    int? SrWidth { get; }
    /// <summary>The SR latent-upscale target height.</summary>
    int? SrHeight { get; }
    /// <summary>The SR refine step count.</summary>
    int? SrSteps { get; }
    /// <summary>The SR refine denoise fraction.</summary>
    double? SrDenoise { get; }
    /// <summary>The SR noise-augmentation amount fed to the SR conditioning node.</summary>
    double? SrNoiseAug { get; }
    /// <summary>The SR real-CFG scale.</summary>
    double? SrCfg { get; }
    /// <summary>The SR model's flow shift.</summary>
    double? SrShift { get; }
}

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
    {
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
    };

    /// <summary>True when the config asked for SR and supplied an SR model file.</summary>
    public static bool Enabled(IHunyuanSrParams p) => p.Sr && !string.IsNullOrWhiteSpace(p.SrModel);

    /// <summary>Append the SR pass over a typed <see cref="ComfyWorkflowGraph"/> and return its refined latent; returns
    /// <paramref name="baseLatent"/> unchanged when SR is off.
    /// <paramref name="positive"/>/<paramref name="negative"/> are the raw text-encode conditioning;
    /// <paramref name="startImage"/>/<paramref name="clipVisionOutput"/> are optional (null for t2v — omitted from the SR
    /// node). <paramref name="sampler"/>/<paramref name="scheduler"/> are the ALREADY-MAPPED ComfyUI names.</summary>
    public static Output<Slot.Latent> Refine(ComfyWorkflowGraph g, IHunyuanSrParams p, Output<Slot.Latent> baseLatent,
        Output<Slot.Conditioning> positive, Output<Slot.Conditioning> negative, Output<Slot.Vae> vae,
        Output<Slot.Image>? startImage, Output<Slot.ClipVision>? clipVisionOutput, string sampler, string scheduler, long seed)
    {
        if (!Enabled(p)) return baseLatent;

        g[Nodes.UpsamplerLoader] = new LatentUpscaleModelLoader { ModelName = Req(p.SrUpsampler, WorkflowParamKeys.SrUpsampler) };
        g[Nodes.LatentUpscale] = new HunyuanVideo15LatentUpscaleWithModel
        {
            Model = LatentUpscaleModelLoader.Out(Nodes.UpsamplerLoader),
            Samples = baseLatent,
            UpscaleMethod = ComfyWidgets.Upscale.Bilinear,
            Width = Req(p.SrWidth, WorkflowParamKeys.SrWidth),
            Height = Req(p.SrHeight, WorkflowParamKeys.SrHeight),
            Crop = ComfyWidgets.Crop.Disabled,
        };
        // The SR node re-emits a (positive, negative, latent) triple for the SR model (mirrors HunyuanVideo15ImageToVideo).
        // start_image/clip_vision_output ride the i2v path only; null here omits them, byte-identical to the old conditional dict.
        g[Nodes.SuperResolution] = new HunyuanVideo15SuperResolution
        {
            Positive = positive,
            Negative = negative,
            Latent = HunyuanVideo15LatentUpscaleWithModel.Out(Nodes.LatentUpscale),
            NoiseAugmentation = Req(p.SrNoiseAug, WorkflowParamKeys.SrNoiseAug),
            Vae = vae,
            StartImage = startImage,
            ClipVisionOutput = clipVisionOutput,
        };

        g[Nodes.SrModel] = ComfyGraph.DiffusionLoaderNode(Req(p.SrModel, WorkflowParamKeys.SrModel));
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.SrModel), Shift = Req(p.SrShift, WorkflowParamKeys.SrShift) };
        Output<Slot.Model> srModel = ModelSamplingSD3.Out(Nodes.ModelSampling);
        g[Nodes.Scheduler] = new BasicScheduler { Model = srModel, Scheduler = scheduler, Steps = Req(p.SrSteps, WorkflowParamKeys.SrSteps), Denoise = Req(p.SrDenoise, WorkflowParamKeys.SrDenoise) };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = sampler };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[Nodes.Guider] = new CFGGuider { Model = srModel, Positive = HunyuanVideo15SuperResolution.PositiveOut(Nodes.SuperResolution), Negative = HunyuanVideo15SuperResolution.NegativeOut(Nodes.SuperResolution), Cfg = Req(p.SrCfg, WorkflowParamKeys.SrCfg) };
        g[Nodes.Sampler] = new SamplerCustomAdvanced
        {
            Noise = RandomNoise.Out(Nodes.Noise),
            Guider = CFGGuider.Out(Nodes.Guider),
            Sampler = KSamplerSelect.Out(Nodes.SamplerSelect),
            Sigmas = BasicScheduler.Out(Nodes.Scheduler),
            LatentImage = HunyuanVideo15SuperResolution.LatentOut(Nodes.SuperResolution),
        };
        return SamplerCustomAdvanced.Out(Nodes.Sampler);
    }

    /// <summary>A required SR scalar: present, or the render is REFUSED (SR configs always supply these; an absent one
    /// is a broken config, never a silent default).</summary>
    private static T Req<T>(T? value, string key) where T : struct =>
        value ?? throw new RenderValidationException($"This configuration needs a value for '{key}' and none is set. It must supply one — there is no default.");

    /// <summary>A required SR filename (resolved model ref): present, or the render is REFUSED.</summary>
    private static string Req(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new RenderValidationException($"This configuration needs a value for '{key}' and none is set. It must supply one — there is no default.");
}
