namespace ImageGen.Application.Media;

/// <summary>The pixel dimensions of an image.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct ImageDimensions(int Width, int Height);

/// <summary>Encoded media bytes together with the MIME type to serve them as.</summary>
/// <param name="Bytes">The encoded image or video bytes.</param>
/// <param name="ContentType">The MIME type of <see cref="Bytes"/> (e.g. image/jpeg, image/webp, video/mp4).</param>
public sealed record MediaPayload(byte[] Bytes, string ContentType);
