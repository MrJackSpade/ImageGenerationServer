using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>Flux.2 Klein custom-sampler edit pipeline. Multi-image uses the ComfyUI reference_latent method (chain
/// one ReferenceLatent per image, source first). Two models run this (4B and 9B) → two workflow classes over this
/// base.</summary>
public abstract class Flux2KleinEditBase : EditWorkflow<Flux2KleinEditParams>
{
    /// <summary>This base's own node ids (the model/clip/vae/source head is the inherited <c>Nodes</c>).</summary>
    private const string Positive = "13";
    private const string ScaledSource = "11";
    private const string Encode = "12";
    private const string SourceSize = "17";
    private const string Guidance = "14";
    private const string RefLatent = "15";
    private const string Guider = "22";
    private const string EmptyLatent = "28";
    private const string Scheduler = "29";
    private const string Noise = "20";
    private const string SamplerSelect = "21";
    private const string Sampler = "23";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(Flux2KleinEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;

        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 64 };
        g[Encode] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(ScaledSource), Vae = vae0 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Positive), Guidance = p.Guidance };
        g[RefLatent] = new ReferenceLatent { Conditioning = FluxGuidance.Out(Guidance), Latent = VAEEncode.Out(Encode) };
        Output<Slot.Conditioning> cond = ReferenceLatent.Out(RefLatent);
        // The model's reference capacity. Supplying MORE references than it accepts is REFUSED, not silently
        // truncated to the first rm — dropping the caller's extra references without a word is the anti-pattern.
        int rm = p.ReferenceMax ?? 0;
        if (refNames.Count > rm)
            throw new RenderValidationException($"This configuration accepts at most {rm} reference image(s); got {refNames.Count}.");
        int fn = refNames.Count;
        for (int i = 0; i < fn; i++)
        {
            string load = $"{40 + i}", scale = $"{50 + i}", enc = $"{60 + i}", rl = $"{70 + i}";
            g[load] = new LoadImage { Image = refNames[i] };
            g[scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(load), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 64 };
            g[enc] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(scale), Vae = vae0 };
            g[rl] = new ReferenceLatent { Conditioning = cond, Latent = VAEEncode.Out(enc) };
            cond = ReferenceLatent.Out(rl);
        }
        g[Guider] = new BasicGuider { Model = model0, Conditioning = cond };
        g[EmptyLatent] = new EmptyFlux2LatentImage { Width = GetImageSize.WidthOut(SourceSize), Height = GetImageSize.HeightOut(SourceSize), BatchSize = 1 };
        g[Scheduler] = new Flux2Scheduler { Steps = p.Steps, Width = GetImageSize.WidthOut(SourceSize), Height = GetImageSize.HeightOut(SourceSize) };
        g[Noise] = new RandomNoise { NoiseSeed = seed };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Noise), Guider = BasicGuider.Out(Guider), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = Flux2Scheduler.Out(Scheduler), LatentImage = EmptyFlux2LatentImage.Out(EmptyLatent) };
        g[Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Flux.2 Klein edit parameters, shared by the 4B and 9B subclasses — the shared loader head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the custom-sampler settings, the
/// distilled <c>guidance</c>, and the optional <c>reference_max</c> cap (Has-guarded nullable int: absent → this editor
/// takes no reference images). The <c>*Req</c> reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are
/// nullable strings; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record Flux2KleinEditParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]     public required double Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
