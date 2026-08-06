using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.QwenPixelize;

/// <summary>An empty SD3-family latent whose width/height are WIRED from another node's int outputs (e.g.
/// <see cref="GetImageSize"/>). Same ComfyUI class type as the literal-dimension <see cref="EmptyLatent"/> built with
/// <see cref="ComfyNodeTypes.EmptySD3LatentImage"/>, but its dimensions are edges rather than constants, so it is a
/// distinct record — the Qwen pixelizer sizes its generate-fresh latent to the scaled source read via GetImageSize.</summary>
public sealed record EmptySD3LatentFromSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptySD3LatentImage;
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}
