using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>
/// Durable database storage for image bytes, keyed by a globally-unique opaque id. Replaces serving images by
/// proxying ComfyUI's /view (where the id was a ComfyUI filename that could collide across the app + MCP, or
/// disappear when ComfyUI's output dir rotated). New images are stored here on generation/upload and served
/// DB-first.
/// </summary>
public interface IImageBlobRepository
{
    /// <summary>Store image bytes under a freshly minted, globally-unique id; returns that id.</summary>
    Task<string> AddAsync(NewImageBlob blob, CancellationToken ct);

    /// <summary>Fetch a stored image (bytes + metadata) by id, or null if there's no blob for it.</summary>
    Task<ImageBlob?> GetAsync(string imageId, CancellationToken ct);

    /// <summary>Look up the stored content type for each id (without fetching the bytes), for ids that exist. Used to
    /// tell which library images are video clips (animated webp) so the UI can play them as &lt;video&gt;.</summary>
    Task<IReadOnlyDictionary<string, string>> GetContentTypesAsync(IReadOnlyCollection<string> imageIds, CancellationToken ct);

    /// <summary>Attach the derived pixel-quantize palette (JSON array of #RRGGBB) to an existing image.</summary>
    Task SetPaletteAsync(string imageId, string paletteJson, CancellationToken ct);

    /// <summary>The derived palette JSON for an image, or null if none was captured (non-pixel or pre-feature).</summary>
    Task<string?> GetPaletteAsync(string imageId, CancellationToken ct);

    /// <summary>Attach the fp quantize's pooled label frequencies (JSON float array, indexed by the palette's order)
    /// to an existing image — the second batch-global a single-frame replay needs besides the palette.</summary>
    Task SetFrequenciesAsync(string imageId, string frequenciesJson, CancellationToken ct);

    /// <summary>The fp label-frequencies JSON for an image, or null if none was captured.</summary>
    Task<string?> GetFrequenciesAsync(string imageId, CancellationToken ct);
}
