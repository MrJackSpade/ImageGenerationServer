using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

internal static class PixelHarnessGraph
{
    /// <summary>Override a graph's source-derived render size with a fixed snapped size: a plain lanczos
    /// <c>ImageScale</c> to exactly (w,h). Used when <see cref="PixelSnap"/> is active so the sampler runs at a
    /// clean k×VRES multiple instead of the megapixels/source-bucket size.</summary>
    public static object FixedScale(object image, int w, int h) =>
        ComfyGraph.Node("ImageScale", new { image, upscale_method = "lanczos", width = w, height = h, crop = "disabled" });

    /// <summary>Flatten the source <c>LoadImage</c> (node "10") onto a WHITE background using its alpha — mirroring
    /// the harness's RGBA→RGB-on-white — so a transparent sky/background lands on white instead of black (which
    /// otherwise haloes the soft glow around lit elements). Emits nodes 11–14 and returns the flattened image ref.
    /// A no-op for sources without alpha (the placeholder mask interpolates to fully-opaque). White = 0xFFFFFF.</summary>
    public static object FlattenOnWhite(Dictionary<string, object> wf)
    {
        wf["11"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("10", 0) });
        wf["12"] = ComfyGraph.Node("EmptyImage", new { width = ComfyGraph.Ref("11", 0), height = ComfyGraph.Ref("11", 1), batch_size = 1, color = 0xFFFFFF });
        wf["13"] = ComfyGraph.Node("InvertMask", new { mask = ComfyGraph.Ref("10", 1) });
        wf["14"] = ComfyGraph.Node("ImageCompositeMasked", new { destination = ComfyGraph.Ref("12", 0), source = ComfyGraph.Ref("10", 0), x = 0, y = 0, resize_source = false, mask = ComfyGraph.Ref("13", 0) });
        return ComfyGraph.Ref("14", 0);
    }
}
