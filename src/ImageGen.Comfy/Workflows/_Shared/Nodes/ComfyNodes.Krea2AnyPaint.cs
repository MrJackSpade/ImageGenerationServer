using ImageGen.Domain.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// Krea 2 AnyPaint's three custom nodes (ComfyUI-Krea2-AnyPaint pack, MIT). Together they drive the yijunwang2
/// AnyPaint LoRA for arbitrary-mask inpaint + outpaint on Krea 2 Turbo: <see cref="Krea2AnyPaintPrepare"/> builds the
/// padded known canvas and the generate/keep masks, <see cref="Krea2AnyPaintEncode"/> attaches the reference latents
/// and a token-aligned noise mask, and <see cref="Krea2AnyPaintModelPatch"/> registers the reference over the target
/// grid and caches its isolated K/V. One typed record per class type; inputs are in the node's own
/// <c>INPUT_TYPES</c> order.
/// </summary>
///
/// <remarks>Preservation is per-step (the sampler pins known tokens each denoise step), so there is deliberately NO
/// paste-back composite on this path — unlike the FLUX Fill / ControlNet inpaint workflows.</remarks>
public sealed record Krea2AnyPaintPrepare : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Krea2AnyPaintPrepare;
    [JsonPropertyName("source")] public required Output<Slot.Image> Source { get; init; }
    [JsonPropertyName("left")] public required int Left { get; init; }
    [JsonPropertyName("top")] public required int Top { get; init; }
    [JsonPropertyName("right")] public required int Right { get; init; }
    [JsonPropertyName("bottom")] public required int Bottom { get; init; }
    [JsonPropertyName("reference_max_edge")] public required int ReferenceMaxEdge { get; init; }
    [JsonPropertyName("boundary_redraw_px")] public required int BoundaryRedrawPx { get; init; }

    /// <summary>Optional white-on-black interior region to regenerate. Omitted (null) on a pure outpaint, where only
    /// the padding is generated; the node then treats the interior as all-preserved. Skipped from the wire when null
    /// so the graph carries no <c>generated_mask</c> key rather than an explicit null (the node's own default path).</summary>
    [JsonPropertyName("generated_mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [AllowNullable("null = no interior mask (a pure outpaint); the input is then omitted from the wire so the node uses its all-preserved default. No Output value can express \"absent link\".")]
    public Output<Slot.Mask>? GeneratedMask { get; init; }

    public static Output<Slot.Image> SemanticReferenceOut(string id) => new(id, 0);
    public static Output<Slot.Image> KnownImageOut(string id) => new(id, 1);
    public static Output<Slot.Mask> GeneratedMaskOut(string id) => new(id, 2);
    public static Output<Slot.Mask> KeepMaskOut(string id) => new(id, 3);
    public static Output<Slot.Int> CanvasWidthOut(string id) => new(id, 4);
    public static Output<Slot.Int> CanvasHeightOut(string id) => new(id, 5);
}

/// <summary>Encodes the prompt + 384px semantic reference (Qwen3-VL) and returns the positive conditioning (out 0,
/// with the reference latents appended) and a standard Comfy latent carrying the known image plus a token-aligned
/// <c>noise_mask</c> (out 1). The sampler applies Krea 2's timestep-matched known-region replacement from that mask.</summary>
public sealed record Krea2AnyPaintEncode : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Krea2AnyPaintEncode;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("semantic_reference")] public required Output<Slot.Image> SemanticReference { get; init; }
    [JsonPropertyName("known_image")] public required Output<Slot.Image> KnownImage { get; init; }
    [JsonPropertyName("keep_mask")] public required Output<Slot.Mask> KeepMask { get; init; }
    [JsonPropertyName("vlm_reference")] public required bool VlmReference { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}

/// <summary>Registers the semantic reference over the complete target grid and precomputes its isolated reference K/V
/// cache (once per sampling run when <c>kv_cache</c> is on). Placed after the LoRA loader and before the sampler; its
/// single output (slot 0) is the patched MODEL.</summary>
public sealed record Krea2AnyPaintModelPatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Krea2AnyPaintModelPatch;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("kv_cache")] public required bool KvCache { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
