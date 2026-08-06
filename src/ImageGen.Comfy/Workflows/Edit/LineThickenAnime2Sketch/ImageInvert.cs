using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenAnime2Sketch;

/// <summary>Inverts an image (ComfyUI core) — here white-on-black line art to dark-lines-on-white.</summary>
public sealed record ImageInvert : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageInvert;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
