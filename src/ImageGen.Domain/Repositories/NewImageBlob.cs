//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>
/// The bytes and metadata for a new image to be stored by <see cref="IImageBlobRepository"/>. The repository mints
/// the globally-unique id; this carries only what the caller supplies at store time.
/// </summary>
/// <param name="Bytes">The raw image (or video) bytes to persist.</param>
/// <param name="ContentType">The MIME type of <paramref name="Bytes"/> (e.g. image/png, image/webp).</param>
/// <param name="Width">Pixel width, when known.</param>
/// <param name="Height">Pixel height, when known.</param>
/// <param name="Kind">Whether the blob is a generated image, an upload, or another kind.</param>
public sealed record NewImageBlob(
    byte[] Bytes,
    string ContentType,
    int? Width,
    int? Height,
    ImageBlobKind Kind);
