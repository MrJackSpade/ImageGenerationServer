using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on FLUX.2-Klein 4B. Mirrors the Klein custom-sampler edit graph (ReferenceLatent on the
/// source, BasicGuider + SamplerCustomAdvanced over a fresh Flux.2 latent), with the model patched by the
/// per-step <c>PixelManifoldProjection</c> before the guider and a final <c>PixelQuantize</c> render.
/// </summary>
public sealed class Flux2Klein4bPixelizeWorkflow : EditWorkflow<Flux2Klein4bPixelizeParams>
{
    public override string Name => "pixelize-klein4b";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KleinLike(PixelizeSchema.DefaultPixelPrompt);

    /// <summary>This subclass's own node ids.</summary>
    private const string Positive = "60";
    private const string ScaledImage = "62";
    private const string Encode = "63";
    private const string ImageSize = "64";
    private const string Guidance = "65";
    private const string RefLatent = "66";
    private const string Projection = "35";
    private const string Guider = "22";
    private const string EmptyLatentNode = "28";
    private const string Scheduler = "29";
    private const string Noise = "20";
    private const string SamplerSelect = "21";
    private const string SplitSigmas = "27";
    private const string Sampler = "23";
    private const string Decode = "8";
    private const string FinalQuantize = "36";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(Flux2Klein4bPixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);   // 4/5/6 + LoadImage 10
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (11-14)

        string instruction = string.IsNullOrWhiteSpace(p.StylePrompt) ? inputs.Positive : p.StylePrompt;
        int gw = p.GridW;
        int gh = p.GridH;
        string palette = p.Palette;
        int vres = p.VirtualResolution;

        g[Positive] = new CLIPTextEncode { Text = instruction, Clip = clip0 };
        (int w, int h)? snap = PixelSnap.Target(req.Resolution, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);   // override the megapixels bucket with the clean k×VRES size when on
        g[ScaledImage] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : new ImageScaleToTotalPixels { Image = src, UpscaleMethod = "lanczos", Megapixels = p.Megapixels, ResolutionSteps = 64 };
        g[Encode] = new VAEEncode { Pixels = ImageScale.Out(ScaledImage), Vae = vae0 };
        g[ImageSize] = new GetImageSize { Image = ImageScale.Out(ScaledImage) };
        g[Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Positive), Guidance = p.Guidance };
        g[RefLatent] = new ReferenceLatent { Conditioning = FluxGuidance.Out(Guidance), Latent = VAEEncode.Out(Encode) };

        g[Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);
        g[Guider] = new BasicGuider { Model = PixelManifoldProjection.Out(Projection), Conditioning = ReferenceLatent.Out(RefLatent) };
        g[EmptyLatentNode] = new EmptyFlux2LatentImage { Width = GetImageSize.WidthOut(ImageSize), Height = GetImageSize.HeightOut(ImageSize), BatchSize = 1 };
        g[Scheduler] = new Flux2Scheduler { Steps = p.Steps, Width = GetImageSize.WidthOut(ImageSize), Height = GetImageSize.HeightOut(ImageSize) };
        g[Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        // reference% -> img2img: 0 generates from the empty latent over the full schedule; >0 inits from the source
        // latent and runs only the denoise tail (SplitSigmasDenoise low_sigmas = denoise fraction of the steps).
        Output<Slot.Sigmas> sigmas;
        Output<Slot.Latent> initLatent;
        if (p.Reference > 0)
        {
            g[SplitSigmas] = new SplitSigmasDenoise { Sigmas = Flux2Scheduler.Out(Scheduler), Denoise = PixelSnap.Denoise(p.Reference, 0) };
            sigmas = SplitSigmasDenoise.LowOut(SplitSigmas);        // low_sigmas — the img2img tail
            initLatent = VAEEncode.Out(Encode);    // source latent
        }
        else { sigmas = Flux2Scheduler.Out(Scheduler); initLatent = EmptyFlux2LatentImage.Out(EmptyLatentNode); }
        g[Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Noise), Guider = BasicGuider.Out(Guider), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = sigmas, LatentImage = initLatent };
        g[Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Sampler), Vae = vae0 };
        g[FinalQuantize] = PixelizeSchema.FinalQuantize(VAEDecode.Out(Decode), gw, gh, palette, vres, p.FinalMethod);
        g[Save] = new SaveImage { Images = PixelQuantize.Out(FinalQuantize), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>Flux.2-Klein 4B pixelizer parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the custom-sampler knobs (<c>steps</c>/<c>sampler</c> +
/// distilled <c>guidance</c>, the megapixel working area), the grid/palette/virtual-resolution + the projection ramp,
/// and the <c>reference</c> %% (read as a <c>required</c> int: both the img2img toggle and the denoise-tail split).
/// <c>weight_dtype</c>/<c>clip_type</c>/<c>style_prompt</c> are nullable strings; <c>width</c>/<c>height</c> are
/// defaulted ints, <c>snap_resolution</c> a defaulted bool; <c>seed</c> is the app's single-sourced seed.</summary>
public sealed record Flux2Klein4bPixelizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]            public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]       public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]          public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]     public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]          public required double Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]           public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    [Range(0.1, 4.0)]                                       public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)]       public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Reference)]
    [Range(0, 100)]                                         public required int Reference { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)]                                        public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)]                                        public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)]                                        public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)]
    [Range(0, 4096)]                                        public int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)]
    [Range(0, 4096)]                                        public int Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SnapResolution)]    public bool SnapResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)]           public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjMethod)]        public required string ProjMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)]       public required string FinalMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WStart)]
    [Range(0.0, 1.0)]                                       public required double WStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WEnd)]
    [Range(0.0, 1.0)]                                       public required double WEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StartPercent)]
    [Range(0.0, 1.0)]                                       public required double StartPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPercent)]
    [Range(0.0, 1.0)]                                       public required double EndPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjectEvery)]
    [Range(1, 8)]                                           public required int ProjectEvery { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]              public long Seed { get; init; }
}
