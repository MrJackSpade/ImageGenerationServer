using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface IHistoryRepository
{
    /// <summary>A page of the user's history, newest first, matching <paramref name="query"/>.</summary>
    Task<PagedResult<HistoryEntry>> GetPageAsync(HistoryQuery query, CancellationToken ct);

    Task<HistoryEntry?> GetByGatewayImageIdAsync(long userId, string gatewayImageId, CancellationToken ct);

    /// <summary>
    /// The gateway image id of the user's most recent generation for each given artist token (newest first),
    /// for the artist display-image fallback. Only artists with at least one generation are returned.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetLatestImageIdsForArtistsAsync(
        long userId, IReadOnlyCollection<string> artistNames, CancellationToken ct);

    /// <summary>
    /// Every workflow configuration the user has generated with, most-used first — the options of the history page's
    /// workflow filter. Empty when they have no history.
    /// </summary>
    Task<IReadOnlyList<HistoryWorkflowUse>> GetUsedWorkflowsAsync(long userId, CancellationToken ct);

    /// <summary>
    /// The gateway image ids of the newer and older neighbours of an entry in the user's history
    /// (newest-first order), for detail-view prev/next. Either may be null at the ends.
    /// </summary>
    Task<HistoryNeighbors> GetNeighborsAsync(long userId, string gatewayImageId, CancellationToken ct);

    /// <summary>Insert one entry (with its marks). Returns false if (UserId, GatewayImageId) already exists.</summary>
    Task<bool> AddAsync(HistoryEntry entry, CancellationToken ct);

    // Deliberately no Delete here. Removing the history row is only one part of deleting an image, and doing just
    // that is what stranded the bytes, bookmark, artist display, frames, and job slot behind it. Deleting an image
    // goes through IImageDeletionRepository, which erases every table that names the id in one transaction.
}
