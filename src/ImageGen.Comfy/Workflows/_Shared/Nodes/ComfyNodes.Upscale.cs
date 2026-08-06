using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Resample an image by a scalar factor.</summary>
public sealed record ImageScaleBy : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScaleBy;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("scale_by")] public required double ScaleBy { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
