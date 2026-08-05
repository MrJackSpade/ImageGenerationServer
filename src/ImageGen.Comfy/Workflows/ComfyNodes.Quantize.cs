using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>Reproduces the required-param throw-on-absent for a quantizer knob that is read only on ONE branch
/// (an fp-only knob, the median-only palette, the key-only matte threshold). Those params are conditionally required —
/// making them <c>required</c> on the record would demand them even for the branch that never reads them, breaking a
/// config that legitimately omits them — so they are nullable and this guard fails fast, with the exact message
/// <c>StrReq</c>/<c>DblReq</c>/<c>IntReq</c> gave, when the branch that needs one finds it absent.</summary>
internal static class QuantizeGuards
{
    internal static double Req(double? v, string key) => v ?? throw Missing(key);
    internal static int Req(int? v, string key) => v ?? throw Missing(key);
    internal static string Req(string? v, string key) => v is { Length: > 0 } s ? s : throw Missing(key);

    private static RenderValidationException Missing(string key) =>
        new($"This configuration needs a value for '{key}' and none is set. It must supply one — there is no default.");
}

/// <summary>Feature-preserving pixel-art quantizer (ComfyUI-PixelHarness) — L0 flatten + XDoG line-thicken + de-AA
/// edge-collapse, then ONE global DIN99d palette over the whole image/frame/batch (temporally consistent without a
/// named palette). This is the DERIVATION form used by the video and batch quantizers: it derives its palette from the
/// input, so it carries no replay <c>palette</c>/<c>frequencies</c> inputs. One typed record per ComfyUI class type;
/// inputs are declared in the exact order the old anonymous-object inputs were written, so the emitted graph is
/// byte-identical.</summary>
public sealed record PixelQuantizeFP : ComfyNode
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
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

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

/// <summary>Stacks two image tensors into one batch along the frame axis (ComfyUI core) — the batch quantizer chains
/// these to fold the source + every reference frame into one <c>(N,H,W,3)</c> tensor. Output 0 is the batched IMAGE.</summary>
public sealed record ImageBatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageBatch;
    [JsonPropertyName("image1")] public required Output<Slot.Image> Image1 { get; init; }
    [JsonPropertyName("image2")] public required Output<Slot.Image> Image2 { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Line-art control-image extractor (comfyui_controlnet_aux) — white-on-black line art at the detector
/// resolution; <c>coarse=enable</c> yields bolder lines. Distinct from <see cref="AnimeLineArtPreprocessor"/> (which
/// takes no <c>coarse</c>).</summary>
public sealed record LineArtPreprocessor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LineArtPreprocessor;
    [JsonPropertyName("image")]      public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("coarse")]     public required string Coarse { get; init; }
    [JsonPropertyName("resolution")] public required int Resolution { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Saves frames as an animated WEBP with a LITERAL frame rate (the video quantizer's explicit-fps branch).
/// The same class type as <see cref="SaveAnimatedWEBP"/>, but its <c>fps</c> is a constant double rather than a wired
/// <see cref="Slot.Float"/> edge, so it is a distinct record; inputs are declared in the exact order the old anonymous
/// object wrote them, so the emitted graph is byte-identical.</summary>
public sealed record SaveAnimatedWEBPFixedFps : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SaveAnimatedWEBP;
    [JsonPropertyName("images")]          public required Output<Slot.Image> Images { get; init; }
    [JsonPropertyName("filename_prefix")] public required string FilenamePrefix { get; init; }
    [JsonPropertyName("fps")]             public required double Fps { get; init; }
    [JsonPropertyName("lossless")]        public required bool Lossless { get; init; }
    [JsonPropertyName("quality")]         public required int Quality { get; init; }
    [JsonPropertyName("method")]          public required string Method { get; init; }
}
