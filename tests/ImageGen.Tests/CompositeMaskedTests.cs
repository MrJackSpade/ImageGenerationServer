using ImageGen.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageGen.Tests;

/// <summary>
/// <see cref="MediaProcessor.CompositeMasked"/> (Part B): the server-side masked-edit paste-back. The result is scaled
/// to the original's dimensions and alpha-blended over it through the feathered mask — inside the painted region the
/// result shows, outside it the original is untouched — and a mask that doesn't match the original is refused.
/// </summary>
public sealed class CompositeMaskedTests
{
    private static readonly MediaProcessor Media = new(new MediaOptions());

    private static byte[] Solid(int w, int h, Rgba32 color)
    {
        using Image<Rgba32> img = new(w, h, color);
        using MemoryStream ms = new();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    /// <summary>A white square (the painted region) on a black field, at <paramref name="w"/>×<paramref name="h"/>.</summary>
    private static byte[] MaskWithSquare(int w, int h, int x0, int y0, int x1, int y1)
    {
        using Image<Rgba32> img = new(w, h, new Rgba32(0, 0, 0, 255));
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                img[x, y] = new Rgba32(255, 255, 255, 255);
            }
        }

        using MemoryStream ms = new();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static Rgba32 PixelAt(byte[] png, int x, int y)
    {
        using Image<Rgba32> img = Image.Load<Rgba32>(png);
        return img[x, y];
    }

    [Fact]
    public void Inside_the_square_is_the_result_outside_is_the_original_and_dims_match()
    {
        byte[] original = Solid(64, 64, new Rgba32(0, 0, 255, 255));   // blue
        byte[] result = Solid(32, 32, new Rgba32(255, 0, 0, 255));     // red, half-size (exercises the scale-up)
        byte[] mask = MaskWithSquare(64, 64, 20, 20, 44, 44);          // 24px centred painted square

        byte[] outPng = Media.CompositeMasked(original, result, mask, growPx: 8, blurRadius: 6);

        using Image<Rgba32> composed = Image.Load<Rgba32>(outPng);
        Assert.Equal(64, composed.Width);
        Assert.Equal(64, composed.Height);

        // Centre of the square (past the feather band) is fully the result — red.
        Rgba32 centre = composed[32, 32];
        Assert.True(centre.R > 200 && centre.B < 60, $"centre should be red, was {centre}");

        // A corner, well outside the grown/feathered region, is the untouched original — blue.
        Rgba32 corner = composed[2, 2];
        Assert.True(corner.B > 200 && corner.R < 60, $"corner should be blue, was {corner}");
    }

    [Fact]
    public void A_mask_that_does_not_match_the_original_dimensions_throws()
    {
        byte[] original = Solid(64, 64, new Rgba32(0, 0, 255, 255));
        byte[] result = Solid(64, 64, new Rgba32(255, 0, 0, 255));
        byte[] mask = MaskWithSquare(48, 48, 8, 8, 24, 24);   // wrong size

        _ = Assert.Throws<ArgumentException>(() => Media.CompositeMasked(original, result, mask, growPx: 8, blurRadius: 6));
    }
}
