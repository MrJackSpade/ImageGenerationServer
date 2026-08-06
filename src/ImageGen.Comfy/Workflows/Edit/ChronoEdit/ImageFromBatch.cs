using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.ChronoEdit;

/// <summary>Picks a single frame out of an image batch (ComfyUI core) — ChronoEdit keeps the LAST trajectory frame.</summary>
public sealed record ImageFromBatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageFromBatch;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("batch_index")] public required int BatchIndex { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
