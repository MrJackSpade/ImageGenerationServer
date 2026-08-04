//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// Erases an image and everything that refers to it, in one transaction.
/// <para>
/// This exists as its own port because deleting an image is inherently cross-table: the history row is only the
/// user-visible half. Before this, <c>DELETE FROM dbo.HistoryEntry</c> was the whole of it, which left the bytes in
/// <c>dbo.ImageBlob</c> forever plus a bookmark, an artist display, lossless frames, and the producing job slot all
/// pointing at an id the app no longer admits exists.
/// </para>
/// </summary>
public interface IImageDeletionRepository
{
    /// <summary>Delete the image owned by <paramref name="userId"/> and every row that references it. Returns false
    /// when that user has no history entry for the id (nothing was deleted).</summary>
    Task<bool> DeleteEverywhereAsync(long userId, string gatewayImageId, CancellationToken ct);
}
