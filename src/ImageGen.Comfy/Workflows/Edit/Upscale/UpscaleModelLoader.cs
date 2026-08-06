using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Upscale;

/// <summary>Loads an ESRGAN-family super-resolution network. Typed node record (inputs in the old anonymous order).</summary>
public sealed record UpscaleModelLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.UpscaleModelLoader;
    [JsonPropertyName("model_name")] public required string ModelName { get; init; }
    public static Output<Slot.UpscaleModel> Out(string id) => new(id, 0);
}
