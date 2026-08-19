using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>The full video form of SeedVR2's upscaler. It shares the upstream class type with the still-image record,
/// but includes the temporal batching, overlap, padding, and conservative noise controls used for a frame batch.</summary>
public sealed record SeedVR2TemporalVideoUpscaler : ComfyNode
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
    [JsonPropertyName("temporal_overlap")] public required int TemporalOverlap { get; init; }
    [JsonPropertyName("prepend_frames")] public required int PrependFrames { get; init; }
    [JsonPropertyName("color_correction")] public required string ColorCorrection { get; init; }
    [JsonPropertyName("input_noise_scale")] public required double InputNoiseScale { get; init; }
    [JsonPropertyName("latent_noise_scale")] public required double LatentNoiseScale { get; init; }
    [JsonPropertyName("offload_device")] public required string OffloadDevice { get; init; }
    [JsonPropertyName("enable_debug")] public required bool EnableDebug { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
