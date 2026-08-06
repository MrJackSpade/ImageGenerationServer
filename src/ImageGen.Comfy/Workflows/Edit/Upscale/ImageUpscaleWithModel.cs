using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Upscale;

/// <summary>Feed-forward SR pass through a loaded upscale network.</summary>
public sealed record ImageUpscaleWithModel : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageUpscaleWithModel;
    [JsonPropertyName("upscale_model")] public required Output<Slot.UpscaleModel> UpscaleModel { get; init; }
    [JsonPropertyName("image")]         public required Output<Slot.Image> Image { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
