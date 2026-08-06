using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenSketchKeras;

/// <summary>sketchKeras line extractor (ComfyUI-PixelHarness) — dark-on-white line art at the input size.</summary>
public sealed record SketchKerasLines : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SketchKerasLines;
    [JsonPropertyName("image")]     public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("threshold")] public required double Threshold { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
