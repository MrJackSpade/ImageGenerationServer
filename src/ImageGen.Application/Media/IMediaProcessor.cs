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

    /// <summary>Read an image's pixel dimensions and its true MIME type from its bytes without fully decoding the
    /// pixels. The MIME comes from the decoded file header, not a client-declared claim, so it is authoritative;
    /// throws if the format cannot be determined.</summary>
    ImageDimensions Identify(byte[] bytes);

    /// <summary>Classify an uploaded file by its bytes: the authoritative MIME (an image/audio/video family, sniffed
    /// from the header — never the client's claim) plus pixel dimensions when it has them (null for audio). Throws when
    /// the bytes are not a recognised image, audio, or video file — the upload endpoint takes exactly those.</summary>
    MediaIdentity IdentifyUpload(byte[] bytes);

    /// <summary>Read an MP4 clip's coded pixel dimensions from its container boxes (ImageSharp cannot read an mp4 — the
    /// only stored clip that isn't an animated webp is the MiniMax-H3 mp4). Throws on an unreadable file, exactly like
    /// <see cref="Identify"/>: a video whose header won't parse is a failed render, not a fabricated 0×0.</summary>
    ImageDimensions IdentifyVideo(byte[] bytes);

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

    /// <summary>A still (first decoded frame) downscaled JPEG poster of a video container (mp4/webm/avi — formats
    /// ImageSharp cannot read, so the frame comes from an ffmpeg decode). Throws on bytes that don't demux, carry no
    /// video stream, or yield no frame.</summary>
    MediaPayload VideoThumbnail(byte[] source, int maxEdge);

    /// <summary>
    /// Composite <paramref name="result"/> back over <paramref name="original"/> in the painted region only, for the
    /// server-side masked-edit path (a plain Edit workflow ran the whole canvas; only the masked area is kept). The
    /// result is scaled to the original's dimensions, then the binary white-on-black <paramref name="maskPng"/> is
    /// feathered — grown by <paramref name="growPx"/> then blurred by <paramref name="blurRadius"/> — and used as the
    /// per-pixel alpha: <c>out = original*(1-a) + result*a</c>. Mirrors the in-graph recipe
    /// (<c>GrowMask + ImageBlur</c>) so the composite route and the sibling-inpaint route paste back the same way.
    /// Throws when the mask's dimensions do not match the original's, or when the result's aspect ratio is materially
    /// incompatible with the original (same-aspect bucket-to-source scaling remains supported). Returns PNG bytes.
    /// </summary>
    byte[] CompositeMasked(byte[] original, byte[] result, byte[] maskPng, int growPx, int blurRadius);
}
