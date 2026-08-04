//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// Durable storage for the native-resolution LOSSLESS frames of a pixel-art clip (dbo.ImageFrame), captured before
/// the lossy webp encode and keyed to the produced image id. Lets the sprite pipeline request clean frames instead
/// of decoding the lossy animated webp. Stateless (fresh connection per call), registered as a singleton.
/// </summary>
public interface IImageFrameRepository
{
    /// <summary>Replace the stored frame set for an image with <paramref name="frames"/> (index = list order).</summary>
    Task AddFramesAsync(string imageId, IReadOnlyList<byte[]> frames, CancellationToken ct);

    /// <summary>How many lossless frames are stored for this image (0 if none).</summary>
    Task<int> GetFrameCountAsync(string imageId, CancellationToken ct);

    /// <summary>The stored lossless frame PNGs for an image, ordered by frame index (empty if none).</summary>
    Task<IReadOnlyList<byte[]>> GetFramesAsync(string imageId, CancellationToken ct);
}
