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

        /// <summary>The video MIME family prefix, for deciding whether a sniffed type carries a pixel size to read.</summary>
        public const string VideoFamily = "video/";

        /// <summary>Sniffed upload MIME types (audio/video containers ImageSharp can't identify).</summary>
        public const string Wav = "audio/wav";
        public const string Avi = "video/x-msvideo";
        public const string M4a = "audio/mp4";
        public const string Webm = "video/webm";
        public const string Mp3 = "audio/mpeg";
        public const string Flac = "audio/flac";
        public const string Ogg = "audio/ogg";
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
    public MediaIdentity IdentifyUpload(byte[] bytes)
    {
        // Audio and video are sniffed first from their container magic bytes — cheaply and distinctively — so an image
        // decode is only attempted for what isn't audio/video. That order (rather than "try to decode as an image, and
        // on failure assume it's something else") keeps a genuinely-corrupt image a hard failure instead of a file
        // silently reclassified as media: if it isn't recognised audio/video here, Identify below decides image-or-throw.
        string? av = SniffAudioOrVideo(bytes);
        if (av is not null)
        {
            if (av.StartsWith(MimeTypes.VideoFamily, StringComparison.Ordinal))
            {
                // MP4 carries readable coded dimensions; other containers (webm) don't here — a null size is honest.
                if (string.Equals(av, MimeTypes.Mp4MimeType, StringComparison.Ordinal))
                {
                    (int w, int h) = Mp4Probe.GetDimensions(bytes);
                    return new MediaIdentity(av, w, h);
                }

                return new MediaIdentity(av, null, null);
            }

            return new MediaIdentity(av, null, null);   // audio has no pixel size
        }

        ImageDimensions img = Identify(bytes);   // image, or throws for a file that is none of the three
        return new MediaIdentity(img.MimeType, img.Width, img.Height);
    }

    /// <summary>The <c>audio/*</c> or <c>video/*</c> MIME a file's container magic bytes name, or null when the bytes
    /// aren't a recognised audio/video container (an image, or unknown — the caller then tries an image decode). Header
    /// bytes only, no decode. WebP is deliberately NOT matched here (it is a RIFF like WAV/AVI) so it stays an image.</summary>
    private static string? SniffAudioOrVideo(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12)
        {
            return null;
        }

        // RIFF container: the four bytes at offset 8 name the form — WAVE (audio), AVI (video), WEBP (image → null).
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F')
        {
            if (b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E')
            {
                return MimeTypes.Wav;
            }

            if (b[8] == 'A' && b[9] == 'V' && b[10] == 'I' && b[11] == ' ')
            {
                return MimeTypes.Avi;
            }

            return null;   // WEBP or another RIFF form — let the image path decide
        }

        // ISO-BMFF (MP4/MOV/M4A): an 'ftyp' box at offset 4; the major brand at offset 8 splits audio-only from video.
        if (b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
        {
            return b[8] == 'M' && b[9] == '4' && (b[10] == 'A' || b[10] == 'B') ? MimeTypes.M4a : MimeTypes.Mp4MimeType;
        }

        // Matroska / WebM: the EBML magic. DocType (webm vs matroska, audio vs video) isn't parsed here — video is the
        // overwhelming case and the render path only needs the family.
        if (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
        {
            return MimeTypes.Webm;
        }

        if (b[0] == 'I' && b[1] == 'D' && b[2] == '3')
        {
            return MimeTypes.Mp3;   // ID3v2-tagged MP3
        }

        if (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)
        {
            return MimeTypes.Mp3;   // raw MPEG-audio frame sync
        }

        if (b[0] == 'f' && b[1] == 'L' && b[2] == 'a' && b[3] == 'C')
        {
            return MimeTypes.Flac;
        }

        if (b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S')
        {
            return MimeTypes.Ogg;
        }

        return null;
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
