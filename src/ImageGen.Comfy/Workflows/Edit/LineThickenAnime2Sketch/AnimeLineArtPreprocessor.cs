using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenAnime2Sketch;

/// <summary>Anime line-art network (comfyui_controlnet_aux) — white-on-black line art at the detector resolution.</summary>
public sealed record AnimeLineArtPreprocessor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.AnimeLineArtPreprocessor;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("resolution")] public required int Resolution { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
