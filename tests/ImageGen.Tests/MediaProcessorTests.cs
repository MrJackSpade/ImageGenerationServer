using ImageGen.Application.Media;
using ImageGen.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageGen.Tests;

/// <summary>
/// <see cref="MediaProcessor.Identify"/> must derive the content-type from the bytes themselves (#97): an upload's
/// declared content-type / filename is an untrusted claim, so the stored/served MIME comes from the decoded header.
/// </summary>
public sealed class MediaProcessorTests
{
    private static readonly MediaProcessor Media = new(new MediaOptions());

    private static byte[] Encode(SixLabors.ImageSharp.Formats.IImageEncoder encoder)
    {
        using Image<Rgba32> image = new(4, 4);
        using MemoryStream ms = new();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("jpeg")]
    public void Identify_derives_mime_from_the_bytes(string format)
    {
        (byte[] bytes, string expectedMime) = format switch
        {
            "png" => (Encode(new PngEncoder()), "image/png"),
            "webp" => (Encode(new WebpEncoder()), "image/webp"),
            "jpeg" => (Encode(new JpegEncoder()), "image/jpeg"),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        ImageDimensions dims = Media.Identify(bytes);

        Assert.Equal(expectedMime, dims.MimeType);
        Assert.Equal(4, dims.Width);
        Assert.Equal(4, dims.Height);
    }

    [Fact]
    public void Identify_reports_webp_even_when_a_client_would_call_it_png()
    {
        // The upload path used to fabricate image/png for a content-type-less part; the bytes are authoritative.
        byte[] webp = Encode(new WebpEncoder());

        Assert.Equal("image/webp", Media.Identify(webp).MimeType);
    }
}
