//TODO: CHECK FOR FALLBACKS
using ImageGen.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageGen.Tests;

/// <summary>
/// The animated-webp → mp4 path, which had no coverage at all while it was a subprocess: the only way to find out
/// whether it worked was to open a video clip in the browser.
///
/// <para>ffmpeg now runs in-process, so it can be tested like anything else. These assert on the produced bytes
/// rather than on "it didn't throw" — a muxer that writes a truncated file throws nothing.</para>
/// </summary>
public sealed class WebpToMp4Tests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>An animated webp of <paramref name="frames"/> frames, each a different colour.</summary>
    private static byte[] AnimatedWebp(int width, int height, int frames, int frameDelayMs)
    {
        using var image = new Image<Rgba32>(width, height);
        image.Frames.RootFrame.Metadata.GetWebpMetadata().FrameDelay = (uint)frameDelayMs;
        image.Mutate(c => c.BackgroundColor(Color.Red));

        for (var i = 1; i < frames; i++)
        {
            using var next = new Image<Rgba32>(width, height);
            next.Mutate(c => c.BackgroundColor(i % 2 == 0 ? Color.Blue : Color.Green));
            var added = image.Frames.AddFrame(next.Frames.RootFrame);
            added.Metadata.GetWebpMetadata().FrameDelay = (uint)frameDelayMs;
        }

        using var ms = new MemoryStream();
        image.Save(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        return ms.ToArray();
    }

    /// <summary>MP4 files carry an <c>ftyp</c> box as the first box; its size prefix sits in the first four bytes.</summary>
    private static bool LooksLikeMp4(byte[] bytes) =>
        bytes.Length > 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p';

    [Fact]
    public void An_animated_webp_is_recognised_and_a_still_one_is_not()
    {
        var processor = new MediaProcessor(new MediaOptions());
        Assert.True(processor.IsAnimatedWebp(AnimatedWebp(32, 32, frames: 4, frameDelayMs: 100)));
        Assert.False(processor.IsAnimatedWebp(AnimatedWebp(32, 32, frames: 1, frameDelayMs: 100)));
    }

    [Fact]
    public async Task An_animated_webp_transcodes_to_a_real_mp4()
    {
        var processor = new MediaProcessor(new MediaOptions());
        var webp = AnimatedWebp(64, 48, frames: 12, frameDelayMs: 100);

        var mp4 = await processor.WebpToMp4Async(webp, maxEdge: null, Ct);

        Assert.NotEmpty(mp4);
        Assert.True(LooksLikeMp4(mp4), "output does not begin with an ftyp box");
        // A container header alone is about 200 bytes; twelve encoded frames must be substantially more than that.
        Assert.True(mp4.Length > 1000, $"output is implausibly small for 12 frames ({mp4.Length} bytes)");
    }

    /// <summary>
    /// The fragmented-MP4 setting exists so the result is playable exactly as produced. A non-fragmented muxer
    /// writes the moov atom by seeking back at the end, which cannot happen when the output is handed straight to
    /// the caller as a byte array — so moov has to be near the front, not the back.
    /// </summary>
    [Fact]
    public async Task The_output_is_fragmented_so_it_plays_without_seeking()
    {
        var processor = new MediaProcessor(new MediaOptions());
        var mp4 = await processor.WebpToMp4Async(AnimatedWebp(32, 32, 8, 100), maxEdge: null, Ct);

        var text = System.Text.Encoding.ASCII.GetString(mp4);
        int moov = text.IndexOf("moov", StringComparison.Ordinal);
        Assert.True(moov >= 0, "no moov atom — the file is not a complete mp4");
        Assert.True(moov < 2048, $"moov is {moov} bytes in; a fragmented mp4 puts it at the front");
        Assert.Contains("moof", text);   // the fragment boxes themselves
    }

    [Fact]
    public async Task MaxEdge_downscales_the_longest_side()
    {
        var processor = new MediaProcessor(new MediaOptions());
        var big = AnimatedWebp(200, 100, frames: 4, frameDelayMs: 100);

        var full = await processor.WebpToMp4Async(big, maxEdge: null, Ct);
        var small = await processor.WebpToMp4Async(big, maxEdge: 64, Ct);

        Assert.True(LooksLikeMp4(full));
        Assert.True(LooksLikeMp4(small));
        // Same clip, fewer pixels: the constrained encode must be the smaller file.
        Assert.True(small.Length < full.Length, $"maxEdge did not reduce the output ({small.Length} vs {full.Length})");
    }

    /// <summary>
    /// Odd dimensions used to be a real failure: H.264 with YUV420P chroma subsampling cannot represent an odd
    /// width or height, and the encoder refuses to open with a message that never mentions the size.
    /// </summary>
    [Fact]
    public async Task An_odd_sized_clip_still_encodes()
    {
        var processor = new MediaProcessor(new MediaOptions());
        var mp4 = await processor.WebpToMp4Async(AnimatedWebp(101, 77, frames: 4, frameDelayMs: 100), null, Ct);
        Assert.True(LooksLikeMp4(mp4));
    }

    [Fact]
    public async Task A_still_webp_has_a_frame_and_encodes_rather_than_throwing()
    {
        // Not an error case: IsAnimatedWebp gates the call, but a single-frame source must still produce a
        // one-frame mp4 rather than dividing by a frame count of zero.
        var processor = new MediaProcessor(new MediaOptions());
        var mp4 = await processor.WebpToMp4Async(AnimatedWebp(32, 32, frames: 1, frameDelayMs: 100), null, Ct);
        Assert.True(LooksLikeMp4(mp4));
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        var processor = new MediaProcessor(new MediaOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.WebpToMp4Async(AnimatedWebp(64, 64, frames: 30, frameDelayMs: 40), null, cts.Token));
    }
}
