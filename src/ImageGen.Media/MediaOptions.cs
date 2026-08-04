using Loxifi.FFmpeg.Transcoding.Codecs;

namespace ImageGen.Media;

/// <summary>
/// Settings for the media adapter — how animated-webp clips are encoded to mp4 for in-browser
/// &lt;video&gt; playback.
///
/// <para>There is no ffmpeg path here any more. ffmpeg runs in-process, so there is no executable to find,
/// nothing for an operator to install, and no setting that can point at the wrong build.</para>
/// </summary>
public sealed record MediaOptions
{
    /// <summary>
    /// The H.264 encoder. Defaults to Cisco's OpenH264, which is what the LGPL ffmpeg runtime carries.
    ///
    /// <para>x264 is the better encoder and is deliberately not the default: it is GPL, and linking it into a
    /// proprietary application that is then shipped as an archive is the case the GPL exists to prevent.
    /// Switching means swapping the two runtime package references in <c>ImageGen.Media.csproj</c> for their
    /// <c>.GPL</c> counterparts and setting this to <c>GPL.Video.X264</c> — a decision about what may be
    /// redistributed, not a tuning knob.</para>
    /// </summary>
    public VideoCodec VideoCodec { get; init; } = LGPL.Video.OpenH264;

    /// <summary>
    /// Constant rate factor, for encoders that have one. x264 and x265 do; OpenH264 does not and ignores it,
    /// which is why <see cref="BitRate"/> is what carries quality for the default encoder.
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>Encoder speed preset (x264/x265 only). Null leaves the encoder's own default.</summary>
    public string? Preset { get; init; }

    /// <summary>
    /// Target bitrate in bits per second. This is a preview of an already-lossy webp rather than a master, and
    /// 4 Mbps is generous at the sizes video workflows produce.
    /// </summary>
    public long BitRate { get; init; } = 4_000_000;
}
