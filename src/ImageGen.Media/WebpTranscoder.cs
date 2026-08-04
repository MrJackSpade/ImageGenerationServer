using Loxifi.FFmpeg.Transcoding;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageGen.Media;

/// <summary>
/// Animated-webp → mp4 transcoding. Video workflows save their clip as an animated webp (the only multi-frame thing
/// stored); browsers render an animated webp only in an &lt;img&gt;, which can't loop cleanly or be driven. So clips
/// are played as a looping &lt;video&gt; whose source is an h264 mp4 produced here on demand.
///
/// <para>ffmpeg's native webp decoder reads only the FIRST frame of an animated webp, so ImageSharp (3.1.x) decodes
/// every frame and they are pushed into the encoder one at a time.</para>
///
/// <para>This used to launch <c>ffmpeg.exe</c> and pipe raw RGBA through its stdin. ffmpeg now runs in-process via
/// Loxifi.FFmpeg, which removed an undeclared prerequisite, a download step in both install scripts, a path to
/// resolve, and a failure mode that said "Could not start ffmpeg at 'ffmpeg'" without ever saying that ffmpeg was
/// the thing missing.</para>
/// </summary>
internal static class WebpTranscoder
{
    /// <summary>True if the bytes are a RIFF/WEBP carrying animation frames (an ANMF chunk). Cheap header scan.</summary>
    public static bool IsAnimatedWebp(ReadOnlySpan<byte> b)
    {
        if (b.Length < 16) return false;
        if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return false;
        if (b[8] != 'W' || b[9] != 'E' || b[10] != 'B' || b[11] != 'P') return false;
        var i = 12;
        while (i + 8 <= b.Length)
        {
            if (b[i] == 'A' && b[i + 1] == 'N' && b[i + 2] == 'M' && b[i + 3] == 'F') return true;
            uint size = (uint)(b[i + 4] | (b[i + 5] << 8) | (b[i + 6] << 16) | (b[i + 7] << 24));
            long next = (long)i + 8 + size + (size & 1);   // chunks are padded to an even size
            if (next <= i) break;
            i = (int)next;
        }
        return false;
    }

    /// <summary>The first animation frame's duration, ms, from the webp's first ANMF chunk. Throws when the chunk is
    /// absent or carries a non-positive duration — inventing a frame rate would silently re-time the clip, the very
    /// failure the no-upper-clamp note in <see cref="WebpToMp4"/> exists to avoid.</summary>
    private static int ReadFrameDelayMs(ReadOnlySpan<byte> b)
    {
        var i = 12;
        while (i + 8 <= b.Length)
        {
            uint size = (uint)(b[i + 4] | (b[i + 5] << 8) | (b[i + 6] << 16) | (b[i + 7] << 24));
            var payload = i + 8;
            if (b[i] == 'A' && b[i + 1] == 'N' && b[i + 2] == 'M' && b[i + 3] == 'F' && payload + 15 <= b.Length)
            {
                int dur = b[payload + 12] | (b[payload + 13] << 8) | (b[payload + 14] << 16);
                if (dur <= 0)
                    throw new InvalidOperationException(
                        "Animated webp's first ANMF frame carries a non-positive duration; its playback rate cannot be determined.");
                return dur;
            }
            long next = (long)i + 8 + size + (size & 1);
            if (next <= i) break;
            i = (int)next;
        }
        throw new InvalidOperationException(
            "Animated webp has no ANMF chunk; its frame duration cannot be read.");
    }

    /// <summary>
    /// Transcode an animated webp to a looping-ready h264 mp4. <paramref name="maxEdge"/> optionally downscales the
    /// longest side; null keeps full resolution. Throws if the encode fails.
    /// </summary>
    public static byte[] WebpToMp4(byte[] webp, MediaOptions options, int? maxEdge, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(webp);
        // Only ever reached after IsAnimatedWebp, so a webp that is not a genuine multi-frame animation here means that
        // gate was bypassed or the source is malformed. There is no still-image-to-mp4 conversion to fall back to — a
        // single frame has no timing to preserve — so this is a broken state to surface, not a shape to handle.
        if (image.Frames.Count < 2)
            throw new InvalidOperationException(
                $"WebpToMp4 received a webp with {image.Frames.Count} frame(s); only an animated (multi-frame) webp converts to an mp4.");
        if (maxEdge is int edge && Math.Max(image.Width, image.Height) > edge)
            image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(edge, edge) }));

        int w = image.Width, h = image.Height;
        // The source's own frame rate, floored only to keep a nonsensical delay from producing a zero/negative rate.
        // There is deliberately no upper clamp: a 60fps ceiling silently re-timed any faster clip, so the mp4 played
        // back SLOWER than the webp it was made from with nothing recording that the output no longer matched the
        // input. h264 handles rates above 60 fine, and the frame delay is read from the file rather than assumed.
        double fps = Math.Max(1000.0 / ReadFrameDelayMs(webp), 1.0);

        var output = new MemoryStream();
        using (var encoder = new VideoFrameEncoder(output, new FrameEncodeOptions
        {
            Width = w,
            Height = h,
            FrameRate = fps,
            VideoCodec = options.VideoCodec,
            Quality = options.Quality,
            Preset = options.Preset,
            BitRate = options.BitRate,
            // Fragmented, so the moov atom goes in up front instead of being written by seeking back at the end.
            // The result is served straight out of memory and has to be playable exactly as produced.
            Fragmented = true,
        }))
        {
            var frameBytes = new byte[w * h * 4];
            for (var f = 0; f < image.Frames.Count; f++)
            {
                ct.ThrowIfCancellationRequested();
                using var frame = image.Frames.CloneFrame(f);
                frame.CopyPixelDataTo(frameBytes);
                encoder.WriteFrame(frameBytes);
            }
            encoder.Complete();
        }

        return output.ToArray();
    }
}
