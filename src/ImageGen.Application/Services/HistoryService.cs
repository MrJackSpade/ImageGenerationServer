using ImageGen.Application.Models;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

public sealed class HistoryService(
    IHistoryRepository history, IImageDeletionRepository deletions, IJobRepository jobs)
{
    /// <summary>Page size used to collect a recents window bigger than one repository page.</summary>
    private const int RecentsPageSize = 200;

    private readonly IHistoryRepository _history = history;
    private readonly IImageDeletionRepository _deletions = deletions;
    private readonly IJobRepository _jobs = jobs;

    public Task<PagedResult<HistoryEntry>> GetPageAsync(HistoryQuery query, CancellationToken ct) =>
        _history.GetPageAsync(query, ct);

    public Task<HistoryEntry?> GetByImageIdAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _history.GetByGatewayImageIdAsync(userId, gatewayImageId, ct);

    /// <summary>
    /// What the compose page's Recent strip shows: the user's newest images, at least <paramref name="minimum"/> of
    /// them, and enough of them to cover their current-or-last batch whenever that batch has produced more than the
    /// minimum. A batch's images are the newest in history, so a window that size shows the batch and only the batch.
    ///
    /// The WINDOW is decided here, not by the browser. It used to be assembled client-side from live job events, which
    /// meant the size existed only in the page that watched the batch happen: reload after it finished and the strip
    /// forgot, falling back to the minimum and cropping the last batch (50 made, 48 shown). The batch is a fact in the
    /// job table — read it from there, every time, and a page that just loaded is as right as one that watched.
    /// </summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetRecentsAsync(long userId, int minimum, CancellationToken ct)
    {
        var produced = await _jobs.CountLatestBatchImagesAsync(userId, ct);
        var window = Math.Max(minimum, produced);

        // Collected a page at a time because the repository caps a page (a batch may be larger than one). Stops early
        // when history runs out — a fresh account has fewer images than the batch it just started.
        var items = new List<HistoryEntry>(window);
        for (var page = 1; items.Count < window; page++)
        {
            var size = Math.Min(RecentsPageSize, window);
            var result = await _history.GetPageAsync(new HistoryQuery(userId, page, size), ct);
            items.AddRange(result.Items);
            if (result.Items.Count < size) break;
        }
        return items.Count > window ? items.GetRange(0, window) : items;
    }

    /// <summary>The workflows the user has generated with, most-used first (the history filter's options).</summary>
    public Task<IReadOnlyList<HistoryWorkflowUse>> GetUsedWorkflowsAsync(long userId, CancellationToken ct) =>
        _history.GetUsedWorkflowsAsync(userId, ct);

    public Task<HistoryNeighbors> GetNeighborsAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _history.GetNeighborsAsync(userId, gatewayImageId, ct);

    /// <summary>Record a generation. Returns false if the image was already in the user's history.</summary>
    public Task<bool> AddAsync(AddHistoryCommand command, CancellationToken ct) =>
        _history.AddAsync(command.ToEntity(), ct);

    /// <summary>Delete an image: the history row AND every other row that names it (bookmark, artist display, lossless
    /// frames, the producing job slot, and the bytes themselves). Returns false if the user had no such image.</summary>
    public Task<bool> DeleteAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _deletions.DeleteEverywhereAsync(userId, gatewayImageId, ct);
}
