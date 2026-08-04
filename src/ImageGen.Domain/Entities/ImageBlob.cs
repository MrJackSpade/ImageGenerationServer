//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>What a stored image is, for housekeeping/diagnostics.</summary>
public enum ImageBlobKind
{
    /// <summary>An image this app rendered. The only kind still written.</summary>
    Generated = 0,

    /// <summary>Historical. Uploads are render inputs held in memory (see <c>IUploadStore</c>) and are no longer
    /// persisted; the value remains only because old rows carried it.</summary>
    Upload = 1,
}

/// <summary>
/// A binary image persisted in the database — the durable home for generated images. Replaces the
/// old scheme where an image id was a ComfyUI view-ref served by proxy, which collided when ComfyUI's per-prefix
/// filename counter reset (the app and the MCP share one ComfyUI) and vanished when its output dir rotated.
/// <see cref="ImageId"/> is a freshly minted, globally-unique opaque key (a GUID), and the bytes are
/// authoritative; HistoryEntry/ImageBookmark/ArtistDisplay reference images by this id.
/// </summary>
public sealed class ImageBlob
{
    public required string ImageId { get; init; }
    public required byte[] Bytes { get; init; }
    public required string ContentType { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public required int ByteSize { get; init; }
    public ImageBlobKind Kind { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>For a pixel-quantize generation, the derived palette as a JSON array of <c>#RRGGBB</c> strings; null
    /// otherwise. Persisted so the sprite pipeline snaps to the true colours instead of re-deriving from the webp.</summary>
    public string? PaletteJson { get; init; }
}
