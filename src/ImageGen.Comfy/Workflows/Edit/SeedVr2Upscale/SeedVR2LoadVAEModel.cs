using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>SeedVR2 VAE loader (tiled encode/decode).</summary>
public sealed record SeedVR2LoadVAEModel : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SeedVR2LoadVAEModel;
    [JsonPropertyName("model")]               public required string Model { get; init; }
    [JsonPropertyName("device")]              public required string Device { get; init; }
    [JsonPropertyName("encode_tiled")]        public required bool EncodeTiled { get; init; }
    [JsonPropertyName("encode_tile_size")]    public required int EncodeTileSize { get; init; }
    [JsonPropertyName("encode_tile_overlap")] public required int EncodeTileOverlap { get; init; }
    [JsonPropertyName("decode_tiled")]        public required bool DecodeTiled { get; init; }
    [JsonPropertyName("decode_tile_size")]    public required int DecodeTileSize { get; init; }
    [JsonPropertyName("decode_tile_overlap")] public required int DecodeTileOverlap { get; init; }
    [JsonPropertyName("offload_device")]      public required string OffloadDevice { get; init; }
    [JsonPropertyName("cache_model")]         public required bool CacheModel { get; init; }
    public static Output<Slot.Vae> Out(string id) => new(id, 0);
}
