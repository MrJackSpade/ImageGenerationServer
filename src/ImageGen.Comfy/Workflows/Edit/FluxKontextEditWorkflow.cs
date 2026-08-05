using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>Flux.1 Kontext image edit. Single-image native; multi-image uses the verified ImageStitch method
/// (stitch source+refs into one image, encode as the single reference latent; output stays source-sized).</summary>
public sealed class FluxKontextEditWorkflow : EditWorkflow<FluxKontextParams>
{
    public override string Name => "flux1-kontext";

    /// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes). Two FluxKontextImageScale and two
    /// VAEEncode are disambiguated by input: the source vs the stitched source+refs.</summary>
    private const string Positive = "13";
    private const string SourceScale = "11";
    private const string SourceEncode = "12";
    private const string StitchScale = "18";
    private const string StitchEncode = "19";
    private const string RefLatent = "15";
    private const string Guidance = "14";
    private const string NegativeZero = "16";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(FluxKontextParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;

        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[SourceScale] = new FluxKontextImageScale { Image = LoadImage.ImageOut(Nodes.Source) };
        g[SourceEncode] = new VAEEncode { Pixels = FluxKontextImageScale.Out(SourceScale), Vae = vae0 };
        // No reference_max declared → this editor takes no refs (capacity 0). Supplying references anyway is REFUSED,
        // not silently ignored.
        int rm = p.ReferenceMax ?? 0;
        if (refNames.Count > rm)
            throw new RenderValidationException($"This configuration accepts at most {rm} reference image(s); got {refNames.Count}.");
        int fn = refNames.Count;
        Output<Slot.Latent> refLatent;
        if (fn > 0)
        {
            Output<Slot.Image> stitched = LoadImage.ImageOut(Nodes.Source);
            for (int i = 0; i < fn; i++)
            {
                string load = $"{40 + i}", stitch = $"{50 + i}";
                g[load] = new LoadImage { Image = refNames[i] };
                g[stitch] = new ImageStitch { Image1 = stitched, Image2 = LoadImage.ImageOut(load), Direction = ComfyWidgets.Stitch.Right, MatchImageSize = true, SpacingWidth = 0, SpacingColor = ComfyWidgets.Spacing.White };
                stitched = ImageStitch.Out(stitch);
            }
            g[StitchScale] = new FluxKontextImageScale { Image = stitched };
            g[StitchEncode] = new VAEEncode { Pixels = FluxKontextImageScale.Out(StitchScale), Vae = vae0 };
            refLatent = VAEEncode.Out(StitchEncode);
        }
        else refLatent = VAEEncode.Out(SourceEncode);
        g[RefLatent] = new ReferenceLatent { Conditioning = CLIPTextEncode.Out(Positive), Latent = refLatent };
        g[Guidance] = new FluxGuidance { Conditioning = ReferenceLatent.Out(RefLatent), Guidance = p.Guidance };
        g[NegativeZero] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Positive) };
        g[Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = FluxGuidance.Out(Guidance),
            Negative = ConditioningZeroOut.Out(NegativeZero),
            LatentImage = VAEEncode.Out(SourceEncode),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Flux.1 Kontext parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the sampler settings, the distilled <c>guidance</c>, and the optional
/// <c>reference_max</c> cap (nullable: absent → this editor takes no reference images). The <c>*Req</c> reads are
/// <c>required</c>; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record FluxKontextParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]     public required double Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]
    [AllowNullable("null = the config declares no reference-image cap; distinct from a real 0 cap")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
