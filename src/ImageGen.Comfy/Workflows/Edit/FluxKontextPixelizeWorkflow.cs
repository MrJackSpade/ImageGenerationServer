using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on FLUX.1-Kontext. Mirrors the Kontext edit graph (CLIP encode → ReferenceLatent on the
/// source's encoded latent → FluxGuidance), but patches the model with the per-step
/// <c>PixelManifoldProjection</c> so every denoise step clamps the x0 estimate onto a fixed grid+palette,
/// and renders the authoritative output with a final <c>PixelQuantize</c>. Virtual resolution sets the
/// sprite's pixel count independent of Kontext's render bucket.
/// </summary>
public sealed class FluxKontextPixelizeWorkflow : EditWorkflow<FluxKontextPixelizeParams>
{
    public override string Name => "pixelize-kontext";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KontextLike();

    /// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes; FlattenOnWhite owns 11-14
    /// internally).</summary>
    private const string Positive = "60";
    private const string Scale = "62";
    private const string Encode = "63";
    private const string RefLatent = "64";
    private const string Guidance = "65";
    private const string NegativeZero = "66";
    private const string Projection = "35";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Quantize = "36";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(FluxKontextPixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // 4/5/6 + LoadImage 10
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (11-14)

        string instruction = string.IsNullOrWhiteSpace(p.StylePrompt) ? inputs.Positive : p.StylePrompt;
        int gw = p.GridW;
        int gh = p.GridH;
        string palette = p.Palette;
        int vres = p.VirtualResolution;

        g[Positive] = new CLIPTextEncode { Text = instruction, Clip = clip0 };
        (int w, int h)? snap = PixelSnap.Target(req.Resolution, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);   // override the Kontext bucket with the clean k×VRES size when on
        g[Scale] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : new FluxKontextImageScale { Image = src };
        g[Encode] = new VAEEncode { Pixels = FluxKontextImageScale.Out(Scale), Vae = vae0 };
        g[RefLatent] = new ReferenceLatent { Conditioning = CLIPTextEncode.Out(Positive), Latent = VAEEncode.Out(Encode) };
        g[Guidance] = new FluxGuidance { Conditioning = ReferenceLatent.Out(RefLatent), Guidance = p.Guidance };
        g[NegativeZero] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Positive) };

        g[Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);
        g[Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = PixelSnap.Denoise(p.Reference, 0),   // reference% -> denoise; 0 (default) == 1.0 == regenerate from the source ref
            Model = PixelManifoldProjection.Out(Projection),
            Positive = FluxGuidance.Out(Guidance),
            Negative = ConditioningZeroOut.Out(NegativeZero),
            LatentImage = VAEEncode.Out(Encode),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Quantize] = PixelizeSchema.FinalQuantize(VAEDecode.Out(Decode), gw, gh, palette, vres, p.FinalMethod);
        g[Save] = new SaveImage { Images = PixelQuantize.Out(Quantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Flux.1-Kontext pixelizer parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings + distilled <c>guidance</c>, the
/// grid/palette/virtual-resolution + the projection ramp. <c>weight_dtype</c>/<c>clip_type</c>/<c>style_prompt</c> are
/// nullable strings; <c>reference</c>/<c>width</c>/<c>height</c> are defaulted ints (reference read via the denoise
/// map, not <c>required</c>), <c>snap_resolution</c> a defaulted bool; <c>seed</c> is the app's single-sourced seed.</summary>
public sealed record FluxKontextPixelizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]            public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]       public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]          public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]     public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]         public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]          public required double Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]           public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]         public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)]       public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Reference)]
    [Range(0, 100)]                                         public int? Reference { get; init; }
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
