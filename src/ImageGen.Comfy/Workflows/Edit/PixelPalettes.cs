namespace ImageGen.Comfy;

/// <summary>The named palettes the PixelQuantize / PixelManifoldProjection nodes (ComfyUI-PixelHarness) accept, in
/// dropdown order. <c>adaptive</c> derives a ≤256-colour palette from the image itself (median cut); the rest are
/// bundled <c>palettes/*.hex</c> files. An inline hex list ("aabbcc, 112233, …") is still accepted via an API
/// override for a per-character LOCKED palette — it just isn't one of the fixed dropdown choices.</summary>
internal static class PixelPalettes
{
    public static readonly string[] Choices =
        { "adaptive", "chroma-256", "vibrant-256", "xterm-256", "town-adaptive-256", "aap-splendor128" };
}
