using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// Qwen-Image 2.1's unified text/reference encoder. It emits positive and negative conditioning plus an empty
/// 64-channel latent matching the first reference image. Reference inputs are a ComfyUI autogrow group and therefore
/// must keep the <c>images.</c> prefix in API-format graphs.
/// </summary>
public sealed record TextEncodeQwenImage21 : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.TextEncodeQwenImage21;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("negative_prompt")] public required string NegativePrompt { get; init; }
    [JsonPropertyName("resolution")] public required int Resolution { get; init; }

    /// <summary>Optional edit-only inputs: VAE followed by <c>images.image_1..N</c>.</summary>
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; init; }

    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

/// <summary>Controls Qwen-Image 2.1's prefix KV cache. Auto/default is the official memory-aware setting.</summary>
public sealed record QwenImage21Cache : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.QwenImage21Cache;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("device")] public required string Device { get; init; }
    [JsonPropertyName("dtype")] public required string Dtype { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
