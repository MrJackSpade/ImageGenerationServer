using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>SeedVR2 DiT loader (with BlockSwap placement).</summary>
public sealed record SeedVR2LoadDiTModel : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SeedVR2LoadDiTModel;
    [JsonPropertyName("model")]              public required string Model { get; init; }
    [JsonPropertyName("device")]             public required string Device { get; init; }
    [JsonPropertyName("blocks_to_swap")]     public required int BlocksToSwap { get; init; }
    [JsonPropertyName("swap_io_components")] public required bool SwapIoComponents { get; init; }
    [JsonPropertyName("offload_device")]     public required string OffloadDevice { get; init; }
    [JsonPropertyName("cache_model")]        public required bool CacheModel { get; init; }
    [JsonPropertyName("attention_mode")]     public required string AttentionMode { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
