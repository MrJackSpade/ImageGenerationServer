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

    [Fact]
    public void IdentifyUpload_classifies_a_real_image_by_family()
    {
        MediaIdentity id = Media.IdentifyUpload(Encode(new PngEncoder()));
        Assert.StartsWith("image/", id.MimeType);
        Assert.Equal(4, id.Width);
    }

    /// <summary>Minimal container magic bytes — enough for the header sniff, which is all IdentifyUpload reads for the
    /// audio/video families.</summary>
    [Theory]
    [InlineData("wav", "audio/")]
    [InlineData("mp3-id3", "audio/")]
    [InlineData("flac", "audio/")]
    [InlineData("ogg", "audio/")]
    [InlineData("webm", "video/")]
    [InlineData("m4a", "audio/")]
    public void IdentifyUpload_classifies_audio_and_video_by_magic_bytes(string kind, string family)
    {
        byte[] bytes = kind switch
        {
            "wav" => Riff("WAVE"),
            "mp3-id3" => Pad([(byte)'I', (byte)'D', (byte)'3']),
            "flac" => Pad([(byte)'f', (byte)'L', (byte)'a', (byte)'C']),
            "ogg" => Pad([(byte)'O', (byte)'g', (byte)'g', (byte)'S']),
            "webm" => Pad([0x1A, 0x45, 0xDF, 0xA3]),
            "m4a" => Ftyp("M4A "),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.StartsWith(family, Media.IdentifyUpload(bytes).MimeType);
    }

    [Fact]
    public void IdentifyUpload_rejects_bytes_that_are_none_of_the_three() =>
        Assert.ThrowsAny<Exception>(() => Media.IdentifyUpload(Pad([(byte)'n', (byte)'o', (byte)'p', (byte)'e'])));

    private static byte[] Pad(byte[] head)
    {
        byte[] b = new byte[16];
        Array.Copy(head, b, head.Length);
        return b;
    }

    private static byte[] Riff(string form)
    {
        byte[] b = Pad([(byte)'R', (byte)'I', (byte)'F', (byte)'F']);
        byte[] f = System.Text.Encoding.ASCII.GetBytes(form);
        Array.Copy(f, 0, b, 8, f.Length);
        return b;
    }

    private static byte[] Ftyp(string brand)
    {
        byte[] b = new byte[16];
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("ftyp"), 0, b, 4, 4);
        byte[] br = System.Text.Encoding.ASCII.GetBytes(brand);
        Array.Copy(br, 0, b, 8, br.Length);
        return b;
    }
}
