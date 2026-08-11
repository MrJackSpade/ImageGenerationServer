namespace ImageGen.Domain.Repositories;

/// <summary>
/// Whether an image id is one a given user is allowed to read. There is no owner column on the bytes themselves
/// (<c>dbo.ImageBlob</c> is keyed by id alone), so ownership is read from the two side tables that record it:
/// <c>dbo.HistoryEntry</c> (UserId + GatewayImageId) and <c>dbo.JobSlot.ImageId</c> joined to <c>dbo.Job.UserId</c>.
///
/// <para>Both are consulted because neither is total: an image whose history write failed still has its job slot, and
/// a history row can name an id whose job rows were pruned. The question asked is deliberately "does THIS caller have
/// a claim on this id", not "who owns it" — one blob id may legitimately be named by more than one user's history.</para>
///
/// <para>An id neither table knows is NOT readable. Answering "no record, so allow it" would reinstate exactly the
/// cross-user read this exists to close.</para>
/// </summary>
public interface IImageVisibilityRepository
{
    /// <summary>True when <paramref name="userId"/> has a history row or a job slot for <paramref name="imageId"/>.</summary>
    Task<bool> IsReadableAsync(long userId, string imageId, CancellationToken ct);

    /// <summary>The subset of <paramref name="imageIds"/> that <paramref name="userId"/> may read, answered in one
    /// round of chunked queries rather than an id at a time — the gallery asks about every card on the page at once.</summary>
    Task<IReadOnlySet<string>> ReadableAsync(long userId, IReadOnlyCollection<string> imageIds, CancellationToken ct);
}
