using ImageGen.Comfy;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.PixelQuantize;

/// <summary>The feature-preserving quantizer in REPLAY form (the still pixelizer): the same <see cref="PixelQuantizeFP"/>
/// class type, but with the two extra replay-global inputs (<c>palette</c> inline hex list + <c>frequencies</c> float
/// list from a previous fp run) so a single frame can reproduce its whole-batch result exactly. A distinct record
/// because the input shape differs — the extra inputs are declared last, in the exact order the old anonymous object
/// wrote them, so the emitted graph is byte-identical.</summary>
public sealed record PixelQuantizeFPReplay : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.PixelQuantizeFP;
    [JsonPropertyName("image")]              public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("grid_w")]             public required int GridW { get; init; }
    [JsonPropertyName("grid_h")]             public required int GridH { get; init; }
    [JsonPropertyName("virtual_resolution")] public required int VirtualResolution { get; init; }
    [JsonPropertyName("thicken")]            public required double Thicken { get; init; }
    [JsonPropertyName("tau")]                public required double Tau { get; init; }
    [JsonPropertyName("lam")]                public required double Lam { get; init; }
    [JsonPropertyName("k")]                  public required int K { get; init; }
    [JsonPropertyName("beta")]               public required double Beta { get; init; }
    [JsonPropertyName("step")]               public required double Step { get; init; }
    [JsonPropertyName("palette")]            public required string Palette { get; init; }
    [JsonPropertyName("frequencies")]        public required string Frequencies { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
