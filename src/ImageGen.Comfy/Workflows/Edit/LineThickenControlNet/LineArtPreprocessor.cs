using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenControlNet;

/// <summary>Line-art control-image extractor (comfyui_controlnet_aux) — white-on-black line art at the detector
/// resolution; <c>coarse=enable</c> yields bolder lines. Distinct from <see cref="AnimeLineArtPreprocessor"/> (which
/// takes no <c>coarse</c>).</summary>
public sealed record LineArtPreprocessor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LineArtPreprocessor;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("coarse")] public required string Coarse { get; init; }
    [JsonPropertyName("resolution")] public required int Resolution { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
