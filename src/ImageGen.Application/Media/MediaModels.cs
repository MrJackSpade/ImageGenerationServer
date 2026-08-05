namespace ImageGen.Application.Media;

/// <summary>The pixel dimensions of an image and the MIME type detected from its bytes.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="MimeType">The MIME type detected from the file header (e.g. image/png, image/webp, video/mp4) — the
/// authoritative content-type, not a client-declared claim.</param>
public readonly record struct ImageDimensions(int Width, int Height, string MimeType);

/// <summary>Encoded media bytes together with the MIME type to serve them as.</summary>
/// <param name="Bytes">The encoded image or video bytes.</param>
/// <param name="ContentType">The MIME type of <see cref="Bytes"/> (e.g. image/jpeg, image/webp, video/mp4).</param>
public sealed record MediaPayload(byte[] Bytes, string ContentType);
