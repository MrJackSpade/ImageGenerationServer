using ImageGen.Application.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ImageGen.Media;

/// <summary>
/// <see cref="IMediaProcessor"/> over ImageSharp (identity, pHash comparison, thumbnails) and ffmpeg (animated-webp
/// → mp4). This is the sole home of the imaging stack; the core and API depend only on the port.
/// </summary>
public sealed class MediaProcessor(MediaOptions options) : IMediaProcessor
{
    /// <summary>MIME types written on the media payloads this processor produces.</summary>
    private static class MimeTypes
    {
        /// <summary>MIME type of the animated-webp thumbnail.</summary>
        public const string WebpMimeType = "image/webp";

        /// <summary>MIME type of the still JPEG thumbnail.</summary>
        public const string JpegMimeType = "image/jpeg";

        /// <summary>MIME type of the only stored clip ImageSharp can't identify (the MiniMax-H3 mp4).</summary>
        public const string Mp4MimeType = "video/mp4";
    }

    /// <inheritdoc/>
    /// <remarks>Measured: silent declines &lt;= 0.039, smallest real edit (glasses) 0.047 — 0.043 splits them.</remarks>
    public double NoChangeThreshold => 0.043;

    /// <inheritdoc/>
    public ImageDimensions Identify(byte[] bytes)
    {
        ImageInfo info = Image.Identify(bytes);
        // DecodedImageFormat is set by the decoder that just read the header. If it is somehow absent the format is
        // genuinely undeterminable — refuse rather than fabricate a MIME the caller would serve as truth.
        string mime = info.Metadata.DecodedImageFormat?.DefaultMimeType
            ?? throw new InvalidOperationException("The image format could not be determined from its bytes.");
        return new ImageDimensions(info.Width, info.Height, mime);
    }

    /// <inheritdoc/>
    public ImageDimensions IdentifyVideo(byte[] bytes)
    {
        (int w, int h) = Mp4Probe.GetDimensions(bytes);
        return new ImageDimensions(w, h, MimeTypes.Mp4MimeType);
    }

    /// <inheritdoc/>
    public double Difference(byte[] a, byte[] b) => PerceptualHash.Difference(a, b);

    /// <inheritdoc/>
    public bool IsAnimatedWebp(ReadOnlySpan<byte> bytes) => WebpTranscoder.IsAnimatedWebp(bytes);

    /// <inheritdoc/>
    /// <remarks>
    /// Encoding is CPU-bound and runs in-process, so it goes to the thread pool instead of pretending to be async.
    /// Awaiting a synchronous encode on the request thread would block it for the duration of the clip.
    /// </remarks>
    public Task<byte[]> WebpToMp4Async(byte[] webp, int? maxEdge, CancellationToken ct) =>
        Task.Run(() => WebpTranscoder.WebpToMp4(webp, options, maxEdge, ct), ct);

    /// <inheritdoc/>
    public MediaPayload Thumbnail(byte[] source, int maxEdge)
    {
        using Image image = Image.Load(source);
        if (Math.Max(image.Width, image.Height) > maxEdge)
        {
            image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxEdge, maxEdge) }));
        }

        using MemoryStream ms = new();
        if (image.Frames.Count > 1)
        {
            image.Save(ms, new WebpEncoder { Quality = 80 });
            return new MediaPayload(ms.ToArray(), MimeTypes.WebpMimeType);
        }

        image.Save(ms, new JpegEncoder { Quality = 80 });
        return new MediaPayload(ms.ToArray(), MimeTypes.JpegMimeType);
    }

    /// <inheritdoc/>
    public MediaPayload StillThumbnail(byte[] source, int maxEdge)
    {
        using Image image = Image.Load(source);
        while (image.Frames.Count > 1)
        {
            image.Frames.RemoveFrame(image.Frames.Count - 1);
        }

        if (Math.Max(image.Width, image.Height) > maxEdge)
        {
            image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxEdge, maxEdge) }));
        }

        using MemoryStream ms = new();
        image.Save(ms, new JpegEncoder { Quality = 80 });
        return new MediaPayload(ms.ToArray(), MimeTypes.JpegMimeType);
    }
}
