using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>An empty HunyuanVideo 1.5 video latent (ComfyUI core) — seeds the 1.5 text-to-video clip. Its
/// width/height/length are literal render dimensions. Output 0 = latent.</summary>
public sealed record EmptyHunyuanVideo15Latent : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyHunyuanVideo15Latent;
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}
