using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

/// <summary>
/// Which images a user has opened — what the grids' outline means. An image is outlined until it has been looked at,
/// and because that has to survive a reload and follow the user between devices it is durable per-user state, not
/// something a page accumulates while it happens to be open.
/// <para>Unviewed is the ABSENCE of a record, so a newly generated image is unviewed by definition and no special
/// case is needed to light up a fresh batch — the useful part of the old behaviour falls out of the correct rule.</para>
/// </summary>
public sealed class ImageViewService(IImageViewRepository views, TimeProvider clock)
{
    private readonly IImageViewRepository _views = views;
    private readonly TimeProvider _clock = clock;

    /// <summary>Record that the user opened this image. Opening it again changes nothing.</summary>
    public Task MarkViewedAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _views.MarkViewedAsync(userId, gatewayImageId, _clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>Which of these images the user has opened. Everything absent from the result is unviewed.</summary>
    public Task<IReadOnlySet<string>> ViewedAsync(
        long userId, IReadOnlyCollection<string> gatewayImageIds, CancellationToken ct) =>
        _views.ViewedAsync(userId, gatewayImageIds, ct);

    /// <summary>The viewed set for a page of history, keyed the way the callers hold it.</summary>
    public Task<IReadOnlySet<string>> ViewedAsync(
        long userId, IEnumerable<HistoryEntry> entries, CancellationToken ct) =>
        _views.ViewedAsync(userId, entries.Select(e => e.GatewayImageId).ToList(), ct);

    /// <summary>Clear the whole backlog: mark every image in the user's history viewed. Answers how many that
    /// newly covered, so the caller can report what it did rather than claiming a number it guessed.</summary>
    public Task<int> MarkAllViewedAsync(long userId, CancellationToken ct) =>
        _views.MarkAllViewedAsync(userId, _clock.GetUtcNow().UtcDateTime, ct);
}
