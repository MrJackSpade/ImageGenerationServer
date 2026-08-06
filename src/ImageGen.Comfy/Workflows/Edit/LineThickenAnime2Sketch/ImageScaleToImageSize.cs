using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenAnime2Sketch;

/// <summary>Scale an image to width/height WIRED from another node's outputs (e.g. <see cref="GetImageSize"/>). Same
/// ComfyUI class type as <see cref="ImageScale"/>, but its width/height are edges rather than literals, so it is a
/// distinct record — the dimensions serialize as <c>["nodeId", idx]</c> to keep the emitted graph byte-identical.</summary>
public sealed record ImageScaleToImageSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScale;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("crop")] public required string Crop { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
