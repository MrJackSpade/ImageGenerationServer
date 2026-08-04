//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface IPendingJobRepository
{
    /// <summary>Register a pending job. No-op if (UserId, JobId) already exists (idempotent re-registration).</summary>
    Task AddAsync(PendingJob job, CancellationToken ct);

    /// <summary>All outstanding pending jobs across all users, oldest first — the reconciler's work list.</summary>
    Task<IReadOnlyList<PendingJob>> ListAllAsync(CancellationToken ct);

    /// <summary>One user's outstanding pending jobs, oldest first — for cross-device in-progress display.</summary>
    Task<IReadOnlyList<PendingJob>> ListForUserAsync(long userId, CancellationToken ct);

    /// <summary>Remove a pending job once it has been recorded, has failed, or has aged out.</summary>
    Task RemoveAsync(long id, CancellationToken ct);
}
