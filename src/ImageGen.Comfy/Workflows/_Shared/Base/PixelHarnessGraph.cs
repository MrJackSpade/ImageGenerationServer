namespace ImageGen.Comfy;

internal static class PixelHarnessGraph
{
    /// <summary>Node ids named by role. <see cref="Source"/> is the caller's <c>LoadImage</c> (emitted upstream at the
    /// shared edit head's source-image node); the rest are the flatten-on-white nodes this helper emits. Values
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
    /// <c>ImageScale</c> to exactly (w,h). Used when <c>pixel_snap</c> is active so the sampler runs at a
    /// clean k×VRES multiple instead of the megapixels/source-bucket size.</summary>
    public static ImageScale FixedScale(Output<Slot.Image> image, int w, int h) =>
        new ImageScale { Image = image, UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Width = w, Height = h, Crop = ComfyWidgets.Crop.Disabled };

    /// <summary>Flatten the source <c>LoadImage</c> (node "10") onto a WHITE background using its alpha — mirroring
    /// the harness's RGBA→RGB-on-white — so a transparent sky/background lands on white instead of black (which
    /// otherwise haloes the soft glow around lit elements). Emits nodes 11–14 on the typed graph and returns the
    /// flattened image edge. A no-op for sources without alpha (the placeholder mask interpolates to fully-opaque).</summary>
    public static Output<Slot.Image> FlattenOnWhite(ComfyWorkflowGraph g)
    {
        g[Nodes.SourceSize] = new GetImageSize { Image = LoadImage.ImageOut(Nodes.Source) };
        g[Nodes.WhiteBackground] = new EmptyImage { Width = GetImageSize.WidthOut(Nodes.SourceSize), Height = GetImageSize.HeightOut(Nodes.SourceSize), BatchSize = 1, Color = 0xFFFFFF };
        g[Nodes.InvertedAlpha] = new InvertMask { Mask = LoadImage.MaskOut(Nodes.Source) };
        g[Nodes.Composite] = new ImageCompositeMasked { Destination = EmptyImage.Out(Nodes.WhiteBackground), Source = LoadImage.ImageOut(Nodes.Source), X = 0, Y = 0, ResizeSource = false, Mask = InvertMask.Out(Nodes.InvertedAlpha) };
        return ImageCompositeMasked.Out(Nodes.Composite);
    }
}
