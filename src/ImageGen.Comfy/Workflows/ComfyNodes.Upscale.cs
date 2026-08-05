using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Loads an ESRGAN-family super-resolution network. Typed node record (inputs in the old anonymous order).</summary>
public sealed record UpscaleModelLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.UpscaleModelLoader;
    [JsonPropertyName("model_name")] public required string ModelName { get; init; }
    public static Output<Slot.UpscaleModel> Out(string id) => new(id, 0);
}

/// <summary>Feed-forward SR pass through a loaded upscale network.</summary>
public sealed record ImageUpscaleWithModel : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageUpscaleWithModel;
    [JsonPropertyName("upscale_model")] public required Output<Slot.UpscaleModel> UpscaleModel { get; init; }
    [JsonPropertyName("image")]         public required Output<Slot.Image> Image { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Resample an image by a scalar factor.</summary>
public sealed record ImageScaleBy : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScaleBy;
    [JsonPropertyName("image")]          public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("scale_by")]       public required double ScaleBy { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

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

/// <summary>SeedVR2 one-step diffusion upscaler.</summary>
public sealed record SeedVR2VideoUpscaler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SeedVR2VideoUpscaler;
    [JsonPropertyName("image")]              public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("dit")]                public required Output<Slot.Model> Dit { get; init; }
    [JsonPropertyName("vae")]                public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("seed")]               public required long Seed { get; init; }
    [JsonPropertyName("resolution")]         public required int Resolution { get; init; }
    [JsonPropertyName("max_resolution")]     public required int MaxResolution { get; init; }
    [JsonPropertyName("batch_size")]         public required int BatchSize { get; init; }
    [JsonPropertyName("uniform_batch_size")] public required bool UniformBatchSize { get; init; }
    [JsonPropertyName("color_correction")]   public required string ColorCorrection { get; init; }
    [JsonPropertyName("offload_device")]     public required string OffloadDevice { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
