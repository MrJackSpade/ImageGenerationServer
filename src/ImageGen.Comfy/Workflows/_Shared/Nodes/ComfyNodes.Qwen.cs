using System.Text.Json.Serialization;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>
/// Qwen-Image-Edit's multi-reference text encoder (<c>TextEncodeQwenImageEditPlus</c>). Its reference image slots are
/// DYNAMIC — how many, and under which input names (<c>image2</c>, <c>image3</c>, …), is decided by the config's
/// <c>reference_inputs</c> — and the <c>vae</c> input appears only when at least one reference is present. So the fixed
/// inputs (<c>clip</c>/<c>image1</c>/<c>prompt</c>) are declared properties and the variable tail rides in an ordered
/// overflow bag: System.Text.Json emits <see cref="JsonExtensionData"/> AFTER the declared members in insertion order,
/// reproducing the exact <c>clip, image1, prompt, image2…, vae</c> order the hand-built dictionary emitted.
/// </summary>
public sealed record TextEncodeQwenImageEditPlus : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.TextEncodeQwenImageEditPlus;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("image1")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [AllowNullable("null deliberately omits optional image1 for reference-only generation")]
    public Output<Slot.Image>? Image1 { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }

    /// <summary>The dynamic tail inputs, in emit order: each configured reference slot (<c>image2</c>/<c>image3</c>/…)
    /// wired to its scaled <see cref="Output{Slot.Image}"/>, then — only when any reference is present — the
    /// <c>vae</c> wired to the VAE. Null/empty when this edit takes no references (bare <c>clip/image1/prompt</c>).</summary>
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; init; }

    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}

/// <summary>Selects the multi-reference latent-injection method on a Qwen edit conditioning (ComfyUI core) — used when
/// two or more reference images are stitched into the encode, in place of the single <see cref="ReferenceLatent"/>.</summary>
public sealed record FluxKontextMultiReferenceLatentMethod : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.FluxKontextMultiReferenceLatentMethod;
    [JsonPropertyName("conditioning")] public required Output<Slot.Conditioning> Conditioning { get; init; }
    [JsonPropertyName("reference_latents_method")] public required string ReferenceLatentsMethod { get; init; }
    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}

/// <summary>CFG normalization on a model (ComfyUI core) — the standard Qwen-Image-Edit 2511 sampling fix paired with
/// <see cref="ModelSamplingAuraFlow"/>.</summary>
public sealed record CFGNorm : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CFGNorm;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("strength")] public required double Strength { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>A solid-colour image with LITERAL width/height (ComfyUI core <c>EmptyImage</c>). Same class type as the
/// wired-dimension <see cref="EmptyImage"/>, but its dimensions are constants rather than edges, so it is a distinct
/// record — the Qwen reframe's rectangle seed and full-size white paste canvas both size themselves in fixed pixels.</summary>
public sealed record EmptyImageLiteral : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyImage;
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("color")] public required int Color { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Scale an image whose target width/height are WIRED from another node's int outputs (ComfyUI core
/// <c>ImageScale</c>). Same class type as the literal-dimension <see cref="ImageScale"/>, but its dimensions are edges —
/// the Qwen reframe matches the composited canvas back to the unmasked path's bucket size read via <see cref="GetImageSize"/>.</summary>
public sealed record ImageScaleFromSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScale;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("crop")] public required string Crop { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Composite a source image onto a destination at an offset with NO mask (ComfyUI core
/// <c>ImageCompositeMasked</c>, whose mask input is optional). Same class type as the masked
/// <see cref="ImageCompositeMasked"/>, but omits the mask — the Qwen reframe pastes the decoded rectangle onto the
/// white canvas by position alone.</summary>
public sealed record ImageCompositePaste : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageCompositeMasked;
    [JsonPropertyName("destination")] public required Output<Slot.Image> Destination { get; init; }
    [JsonPropertyName("source")] public required Output<Slot.Image> Source { get; init; }
    [JsonPropertyName("x")] public required int X { get; init; }
    [JsonPropertyName("y")] public required int Y { get; init; }
    [JsonPropertyName("resize_source")] public required bool ResizeSource { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>T5 tokenizer options (ComfyUI core) — Chroma prompts through T5-XXL with min-padding disabled; sits in
/// front of the encodes so the padded conditioning does not degrade the render.</summary>
public sealed record T5TokenizerOptions : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.T5TokenizerOptions;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("min_padding")] public required int MinPadding { get; init; }
    [JsonPropertyName("min_length")] public required int MinLength { get; init; }
    public static Output<Slot.Clip> Out(string id) => new(id, 0);
}
