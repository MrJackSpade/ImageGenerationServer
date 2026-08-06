namespace ImageGen.Domain;

/// <summary>
/// The media kind of a reference input a workflow accepts — an image, an audio clip, or a video. A reference's kind is
/// intrinsic to the uploaded blob (derived from its content type), NEVER a client-declared label: an uploaded <c>.mp3</c>
/// IS audio no matter what the caller says, so the kind is authoritative and cannot be spoofed to smuggle a file into a
/// workflow that doesn't accept it. Workflows DECLARE which kinds they take (see the reference block on a card); the
/// render path routes each reference to the graph input for its kind.
/// </summary>
public enum ReferenceKind
{
    /// <summary>A still image reference.</summary>
    Image,

    /// <summary>An audio clip reference (e.g. a voice/sound to condition on).</summary>
    Audio,

    /// <summary>A video reference (e.g. motion to condition on).</summary>
    Video,
}

/// <summary>The wire/JSON tokens and MIME prefixes for <see cref="ReferenceKind"/> — the single vocabulary the client
/// (<c>accept</c> filters, per-kind routing) and the classifier below share, so a kind is spelled ONE way everywhere.</summary>
public static class ReferenceKindNames
{
    /// <summary>The JSON token for <see cref="ReferenceKind.Image"/>.</summary>
    public const string Image = "image";

    /// <summary>The JSON token for <see cref="ReferenceKind.Audio"/>.</summary>
    public const string Audio = "audio";

    /// <summary>The JSON token for <see cref="ReferenceKind.Video"/>.</summary>
    public const string Video = "video";

    /// <summary>The MIME type prefix that classifies a content type as <see cref="ReferenceKind.Image"/>.</summary>
    public const string ImageMime = Image + "/";

    /// <summary>The MIME type prefix that classifies a content type as <see cref="ReferenceKind.Audio"/>.</summary>
    public const string AudioMime = Audio + "/";

    /// <summary>The MIME type prefix that classifies a content type as <see cref="ReferenceKind.Video"/>.</summary>
    public const string VideoMime = Video + "/";
}

/// <summary>Classifies and names <see cref="ReferenceKind"/>s. The classifier reads a stored blob's content type — the
/// authoritative source of a reference's kind — and is deliberately the ONLY place a MIME string becomes a kind.</summary>
public static class ReferenceKinds
{
    /// <summary>The kind a content type denotes, or null when it is blank or not one of the three media families
    /// (the caller decides whether an unclassifiable reference is an error).</summary>
    public static ReferenceKind? Classify(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? null
        : contentType.StartsWith(ReferenceKindNames.ImageMime, StringComparison.OrdinalIgnoreCase) ? ReferenceKind.Image
        : contentType.StartsWith(ReferenceKindNames.AudioMime, StringComparison.OrdinalIgnoreCase) ? ReferenceKind.Audio
        : contentType.StartsWith(ReferenceKindNames.VideoMime, StringComparison.OrdinalIgnoreCase) ? ReferenceKind.Video
        : null;

    /// <summary>The kind a wire/JSON token names. Throws <see cref="ArgumentException"/> on an unrecognized token — a
    /// reference declaration with a bad kind is a config error, not something to silently drop.</summary>
    public static ReferenceKind Parse(string? token) => token switch
    {
        ReferenceKindNames.Image => ReferenceKind.Image,
        ReferenceKindNames.Audio => ReferenceKind.Audio,
        ReferenceKindNames.Video => ReferenceKind.Video,
        _ => throw new ArgumentException($"Unknown reference kind '{token}'; expected one of {ReferenceKindNames.Image}/{ReferenceKindNames.Audio}/{ReferenceKindNames.Video}.", nameof(token)),
    };

    /// <summary>The wire/JSON token for a kind.</summary>
    public static string Wire(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Image => ReferenceKindNames.Image,
        ReferenceKind.Audio => ReferenceKindNames.Audio,
        ReferenceKind.Video => ReferenceKindNames.Video,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A default upload-filename extension for a kind (ComfyUI keys the decode off the file, so the extension
    /// must match the bytes' family — an audio reference uploaded as <c>.png</c> would fail to decode).</summary>
    public static string Extension(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Image => ".png",
        ReferenceKind.Audio => ".wav",
        ReferenceKind.Video => ".mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
