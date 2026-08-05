using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>Flux.2 Klein custom-sampler edit pipeline. Multi-image uses the ComfyUI reference_latent method (chain
/// one ReferenceLatent per image, source first). Two models run this (4B and 9B) → two workflow classes over this
/// base.</summary>
public abstract class Flux2KleinEditBase : EditWorkflow<Flux2KleinEditParams>
{
    protected override ComfyWorkflowGraph Build(Flux2KleinEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 64 };
        g[Nodes.Encode] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Vae = vae0 };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = p.Guidance };
        g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = FluxGuidance.Out(Nodes.Guidance), Latent = VAEEncode.Out(Nodes.Encode) };
        Output<Slot.Conditioning> cond = ReferenceLatent.Out(Nodes.RefLatent);
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
        g[Nodes.Guider] = new BasicGuider { Model = model0, Conditioning = cond };
        g[Nodes.EmptyLatent] = new EmptyFlux2LatentImage { Width = GetImageSize.WidthOut(Nodes.SourceSize), Height = GetImageSize.HeightOut(Nodes.SourceSize), BatchSize = 1 };
        g[Nodes.Scheduler] = new Flux2Scheduler { Steps = p.Steps, Width = GetImageSize.WidthOut(Nodes.SourceSize), Height = GetImageSize.HeightOut(Nodes.SourceSize) };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Nodes.Noise), Guider = BasicGuider.Out(Nodes.Guider), Sampler = KSamplerSelect.Out(Nodes.SamplerSelect), Sigmas = Flux2Scheduler.Out(Nodes.Scheduler), LatentImage = EmptyFlux2LatentImage.Out(Nodes.EmptyLatent) };
        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Flux2KleinEditBase's own node ids (the model/clip/vae/source head is the inherited <c>EditNodes</c>).</summary>
file static class Nodes
{
    public const string Positive = "13";
    public const string ScaledSource = "11";
    public const string Encode = "12";
    public const string SourceSize = "17";
    public const string Guidance = "14";
    public const string RefLatent = "15";
    public const string Guider = "22";
    public const string EmptyLatent = "28";
    public const string Scheduler = "29";
    public const string Noise = "20";
    public const string SamplerSelect = "21";
    public const string Sampler = "23";
    public const string Decode = "8";
    public const string Save = "9";
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
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]
    [AllowNullable("null = the config declares no reference-image cap; distinct from a real 0 cap")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
