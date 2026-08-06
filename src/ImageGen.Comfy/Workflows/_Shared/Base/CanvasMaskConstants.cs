namespace ImageGen.Comfy;

/// <summary>
/// Bounds and node-graph literals for the <b>canvas mask</b> — the four <c>mask_*_pct</c> edit params that fence an
/// edit model into a sub-rectangle of the canvas (see <see cref="QwenEditBase"/>). Named here rather than inlined at
/// the use sites so the schema's advertised range and the geometry's validation can never drift apart.
/// </summary>
public static class CanvasMaskConstants
{
    /// <summary>Smallest accepted blocked-margin percentage (0 = that side is not blocked at all).</summary>
    public const int MinSidePct = 0;

    /// <summary>
    /// Largest accepted blocked-margin percentage for one side. Capped below 100 because a single side blocking the
    /// whole canvas would leave the model no pixels to draw into.
    /// </summary>
    public const int MaxSidePct = 99;

    /// <summary>
    /// The two margins on one axis must leave at least this percentage of that axis open. Guards the degenerate
    /// <c>top=60, bottom=60</c> case, which would otherwise ask for a negative-height drawing rectangle.
    /// </summary>
    public const int MinOpenPctPerAxis = 1;

    /// <summary>Smallest drawing rectangle, in source pixels, on either axis — below this there is nothing to sample.</summary>
    public const int MinRectPx = 8;

    /// <summary>The blocked margin is filled with this RGB — the plain white background every sprite stage assumes.</summary>
    public const int BlockedFillRgb = 0xFFFFFF;

    /// <summary>
    /// The sampled rectangle's width/height are rounded DOWN to a multiple of this before being encoded, then scaled
    /// back to the exact rectangle on the way out. Pinned to 16: the VAE downsamples 8×, and Qwen's transformer packs
    /// 2×2 latent patches on top of that, so a dimension off this stride is silently cropped by the model.
    /// </summary>
    public const int LatentAlignPx = 16;
}
