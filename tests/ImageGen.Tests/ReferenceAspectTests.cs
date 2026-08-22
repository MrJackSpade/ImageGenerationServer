using ImageGen.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageGen.Tests;

/// <summary>The shared preprocessing behind the reference-workflow Shape picker: fixed shapes crop around the centre
/// at an exact ratio; Reference bypasses this method and therefore preserves the upload byte-for-byte.</summary>
public sealed class ReferenceAspectTests
{
    private static readonly MediaProcessor Media = new(new MediaOptions());

    private static byte[] Stripes(int width, int height)
    {
        using Image<Rgba32> image = new(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = x < width / 3
                        ? new Rgba32(255, 0, 0, 255)
                        : x >= width * 2 / 3 ? new Rgba32(0, 0, 255, 255) : new Rgba32(0, 128, 0, 255);
                }
            }
        });
        using MemoryStream bytes = new();
        image.Save(bytes, new PngEncoder());
        return bytes.ToArray();
    }

    [Fact]
    public void Landscape_is_an_exact_centered_sixteen_by_nine_crop()
    {
        byte[] output = Media.CropToAspect(Stripes(200, 100), 16, 9);

        using Image<Rgba32> image = Image.Load<Rgba32>(output);
        Assert.Equal(176, image.Width);
        Assert.Equal(99, image.Height);
        Assert.Equal(16 * image.Height, 9 * image.Width);
        Assert.Equal(new Rgba32(0, 128, 0, 255), image[image.Width / 2, image.Height / 2]);
    }

    [Fact]
    public void Square_crops_the_long_axis_without_stretching()
    {
        byte[] output = Media.CropToAspect(Stripes(200, 100), 1, 1);

        using Image<Rgba32> image = Image.Load<Rgba32>(output);
        Assert.Equal(100, image.Width);
        Assert.Equal(100, image.Height);
        Assert.Equal(new Rgba32(255, 0, 0, 255), image[0, 50]);
        Assert.Equal(new Rgba32(0, 128, 0, 255), image[50, 50]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), image[99, 50]);
    }

    [Fact]
    public void An_already_matching_reference_is_not_reencoded()
    {
        byte[] source = Stripes(160, 90);

        byte[] output = Media.CropToAspect(source, 16, 9);

        Assert.Same(source, output);
    }
}
