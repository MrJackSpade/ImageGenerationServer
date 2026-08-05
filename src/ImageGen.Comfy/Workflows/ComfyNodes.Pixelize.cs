using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>The per-step pixel-manifold projection model patch (spliced before the sampler). Input order matches the
/// old anonymous object exactly.</summary>
public sealed record PixelManifoldProjection : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.PixelManifoldProjection;
    [JsonPropertyName("model")]              public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("vae")]                public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("grid_w")]             public required int GridW { get; init; }
    [JsonPropertyName("grid_h")]             public required int GridH { get; init; }
    [JsonPropertyName("palette")]            public required string Palette { get; init; }
    [JsonPropertyName("method")]             public required string Method { get; init; }
    [JsonPropertyName("w_start")]            public required double WStart { get; init; }
    [JsonPropertyName("w_end")]              public required double WEnd { get; init; }
    [JsonPropertyName("start_percent")]      public required double StartPercent { get; init; }
    [JsonPropertyName("end_percent")]        public required double EndPercent { get; init; }
    [JsonPropertyName("project_every")]      public required int ProjectEvery { get; init; }
    [JsonPropertyName("virtual_resolution")] public required int VirtualResolution { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>The authoritative final PixelQuantize render.</summary>
public sealed record PixelQuantize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.PixelQuantize;
    [JsonPropertyName("image")]              public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("grid_w")]             public required int GridW { get; init; }
    [JsonPropertyName("grid_h")]             public required int GridH { get; init; }
    [JsonPropertyName("palette")]            public required string Palette { get; init; }
    [JsonPropertyName("method")]             public required string Method { get; init; }
    [JsonPropertyName("virtual_resolution")] public required int VirtualResolution { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
