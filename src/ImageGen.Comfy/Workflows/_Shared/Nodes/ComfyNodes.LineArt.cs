using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Grayscale morphological line thickener (ComfyUI-PixelHarness) — grows dark pixels by <c>thickness</c>
/// iterations of a 3×3 minimum filter. One typed record per ComfyUI class type; inputs are declared in the exact order
/// the old anonymous-object inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record LineThicken : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LineThicken;
    [JsonPropertyName("image")]     public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("thickness")] public required int Thickness { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Blend two images by a factor and mode (ComfyUI core) — here a multiply composite of a bolded line layer
/// over the source.</summary>
public sealed record ImageBlend : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageBlend;
    [JsonPropertyName("image1")]       public required Output<Slot.Image> Image1 { get; init; }
    [JsonPropertyName("image2")]       public required Output<Slot.Image> Image2 { get; init; }
    [JsonPropertyName("blend_factor")] public required double BlendFactor { get; init; }
    [JsonPropertyName("blend_mode")]   public required string BlendMode { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
