namespace ImageGen.Application.Media;

/// <summary>
/// Image/video processing the application needs but does not want to depend on an imaging library for: reading
/// dimensions, the perceptual-hash no-change comparison that gates silent-decline edits, animated-webp detection and
/// transcoding to mp4, and thumbnail generation. Implemented by an adapter (ImageGen.Media over ImageSharp + ffmpeg)
/// so neither the orchestrator nor the API references the imaging stack directly.
/// </summary>
public interface IMediaProcessor
{
    /// <summary>Below this perceptual-hash distance an edit is treated as a silent no-op (the model declined) and no
    /// new image is stored. See <see cref="Difference"/>.</summary>
    double NoChangeThreshold { get; }

    /// <summary>Read an image's pixel dimensions from its bytes without fully decoding the pixels.</summary>
    ImageDimensions Identify(byte[] bytes);

    /// <summary>Normalized perceptual-hash (pHash) distance between two images (0 = identical structure, 1 = fully
    /// different). Used to detect a silent "no-op" edit: a re-rendered-but-unchanged scene reads near 0, a real edit
    /// crosses <see cref="NoChangeThreshold"/>.</summary>
    double Difference(byte[] a, byte[] b);

    /// <summary>True if the bytes are an animated WEBP (carries frames); a still webp or non-webp returns false.
    /// Cheap header scan, no decode.</summary>
    bool IsAnimatedWebp(ReadOnlySpan<byte> bytes);

    /// <summary>Transcode an animated WEBP clip to a looping-ready h264 MP4 (browsers can't play animated webp in a
    /// &lt;video&gt;). <paramref name="maxEdge"/> optionally downscales the longest side; null keeps full resolution.</summary>
    Task<byte[]> WebpToMp4Async(byte[] webp, int? maxEdge, CancellationToken ct);

    /// <summary>A downscaled preview fit within <paramref name="maxEdge"/> px on the longest side. A still image becomes
    /// a small JPEG; a multi-frame source stays an animated webp so the card plays the clip.</summary>
    MediaPayload Thumbnail(byte[] source, int maxEdge);

    /// <summary>A still (first-frame) downscaled JPEG poster of a possibly-animated source, used for video cards that
    /// only play on hover.</summary>
    MediaPayload StillThumbnail(byte[] source, int maxEdge);
}
