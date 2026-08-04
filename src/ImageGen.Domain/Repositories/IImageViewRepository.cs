//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// Which images a user has opened. The grids outline an image until it has been looked at, so this is what the
/// outline means — per user, per image, durable, and therefore the same on every device they use.
/// <para>Absence is the unviewed state: the table holds only what HAS been seen, so a newly generated image is
/// unviewed without anything having to write a row for it.</para>
/// </summary>
public interface IImageViewRepository
{
    /// <summary>Record that this user has opened this image. Idempotent — opening it again is not a new fact, and the
    /// first view's timestamp is the one kept.</summary>
    Task MarkViewedAsync(long userId, string gatewayImageId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Which of these image ids this user has already opened. Ids they haven't are simply absent from the
    /// result, so a caller renders the outline for everything the set doesn't contain.</summary>
    Task<IReadOnlySet<string>> ViewedAsync(long userId, IReadOnlyCollection<string> gatewayImageIds, CancellationToken ct);

    /// <summary>Mark every image in this user's history viewed, and answer how many that newly covered. What clears a
    /// backlog: without it an outline that means "unread" can only ever be cleared one image at a time.</summary>
    Task<int> MarkAllViewedAsync(long userId, DateTime nowUtc, CancellationToken ct);
}
