using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>SeedVR2 one-step diffusion upscaler.</summary>
public sealed record SeedVR2VideoUpscaler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SeedVR2VideoUpscaler;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("dit")] public required Output<Slot.Model> Dit { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("seed")] public required long Seed { get; init; }
    [JsonPropertyName("resolution")] public required int Resolution { get; init; }
    [JsonPropertyName("max_resolution")] public required int MaxResolution { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("uniform_batch_size")] public required bool UniformBatchSize { get; init; }
    [JsonPropertyName("color_correction")] public required string ColorCorrection { get; init; }
    [JsonPropertyName("offload_device")] public required string OffloadDevice { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
