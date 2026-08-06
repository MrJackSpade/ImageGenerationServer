using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Application.Media;

/// <summary>The pixel dimensions of an image and the MIME type detected from its bytes.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="MimeType">The MIME type detected from the file header (e.g. image/png, image/webp, video/mp4) — the
/// authoritative content-type, not a client-declared claim.</param>
public readonly record struct ImageDimensions(int Width, int Height, string MimeType);

/// <summary>What an uploaded file is: its authoritative MIME type (sniffed from the bytes, never the client's claim)
/// and pixel dimensions when it has them (null for audio, and for video containers whose size isn't read).</summary>
/// <param name="MimeType">The detected MIME type — an <c>image/*</c>, <c>audio/*</c>, or <c>video/*</c> family.</param>
/// <param name="Width">Pixel width, or null when the media has no readable pixel size (audio, some containers).</param>
/// <param name="Height">Pixel height, or null (see <paramref name="Width"/>).</param>
public readonly record struct MediaIdentity(
    string MimeType,
    [property: AllowNullable("null = the media has no readable pixel size (audio, or a container whose size isn't parsed); distinct from a 0px default")] int? Width,
    [property: AllowNullable("null = the media has no readable pixel size (audio, or a container whose size isn't parsed); distinct from a 0px default")] int? Height);

/// <summary>Encoded media bytes together with the MIME type to serve them as.</summary>
/// <param name="Bytes">The encoded image or video bytes.</param>
/// <param name="ContentType">The MIME type of <see cref="Bytes"/> (e.g. image/jpeg, image/webp, video/mp4).</param>
public sealed record MediaPayload(byte[] Bytes, string ContentType);
