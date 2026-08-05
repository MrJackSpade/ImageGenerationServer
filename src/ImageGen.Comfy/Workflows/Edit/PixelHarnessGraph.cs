namespace ImageGen.Comfy;

internal static class PixelHarnessGraph
{
    /// <summary>Node ids named by role. <see cref="Source"/> is the caller's <c>LoadImage</c> (emitted upstream, e.g.
    /// <see cref="EditWorkflowBase.Nodes.Source"/>); the rest are the flatten-on-white nodes this helper emits. Values
    /// preserved exactly.</summary>
    private static class Nodes
    {
        public const string Source = "10";
        public const string SourceSize = "11";
        public const string WhiteBackground = "12";
        public const string InvertedAlpha = "13";
        public const string Composite = "14";
    }

    /// <summary>Override a graph's source-derived render size with a fixed snapped size: a plain lanczos
    /// <c>ImageScale</c> to exactly (w,h). Used when <see cref="PixelSnap"/> is active so the sampler runs at a
    /// clean k×VRES multiple instead of the megapixels/source-bucket size.</summary>
    public static object FixedScale(object image, int w, int h) =>
        ComfyGraph.Node(ComfyNodeTypes.ImageScale, new { image, upscale_method = "lanczos", width = w, height = h, crop = "disabled" });

    /// <summary>Flatten the source <c>LoadImage</c> (node "10") onto a WHITE background using its alpha — mirroring
    /// the harness's RGBA→RGB-on-white — so a transparent sky/background lands on white instead of black (which
    /// otherwise haloes the soft glow around lit elements). Emits nodes 11–14 and returns the flattened image ref.
    /// A no-op for sources without alpha (the placeholder mask interpolates to fully-opaque). White = 0xFFFFFF.</summary>
    public static object FlattenOnWhite(Dictionary<string, object> wf)
    {
        wf[Nodes.SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(Nodes.Source, 0) });
        wf[Nodes.WhiteBackground] = ComfyGraph.Node(ComfyNodeTypes.EmptyImage, new { width = ComfyGraph.Ref(Nodes.SourceSize, 0), height = ComfyGraph.Ref(Nodes.SourceSize, 1), batch_size = 1, color = 0xFFFFFF });
        wf[Nodes.InvertedAlpha] = ComfyGraph.Node(ComfyNodeTypes.InvertMask, new { mask = ComfyGraph.Ref(Nodes.Source, 1) });
        wf[Nodes.Composite] = ComfyGraph.Node(ComfyNodeTypes.ImageCompositeMasked, new { destination = ComfyGraph.Ref(Nodes.WhiteBackground, 0), source = ComfyGraph.Ref(Nodes.Source, 0), x = 0, y = 0, resize_source = false, mask = ComfyGraph.Ref(Nodes.InvertedAlpha, 0) });
        return ComfyGraph.Ref(Nodes.Composite, 0);
    }
}
