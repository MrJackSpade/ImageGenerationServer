using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenXDoG;

/// <summary>XDoG (extended difference-of-Gaussians) outline extractor (ComfyUI-PixelHarness) — pulls existing outlines
/// out as dark-lines-on-white.</summary>
public sealed record XDoGLines : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.XDoGLines;
    [JsonPropertyName("image")]   public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("sigma")]   public required double Sigma { get; init; }
    [JsonPropertyName("k")]       public required double K { get; init; }
    [JsonPropertyName("tau")]     public required double Tau { get; init; }
    [JsonPropertyName("epsilon")] public required double Epsilon { get; init; }
    [JsonPropertyName("phi")]     public required double Phi { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
