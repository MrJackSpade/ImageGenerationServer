using ImageGen.Application.Models;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

public sealed class PendingJobService(IPendingJobRepository pending)
{
    private readonly IPendingJobRepository _pending = pending;

    /// <summary>Record that a gateway job is in flight for a user (idempotent).</summary>
    public Task RegisterAsync(RegisterPendingJobCommand command, CancellationToken ct) =>
        _pending.AddAsync(command.ToEntity(), ct);

    /// <summary>The reconciler's work list: every outstanding pending job, oldest first.</summary>
    public Task<IReadOnlyList<PendingJob>> ListAllAsync(CancellationToken ct) =>
        _pending.ListAllAsync(ct);

    /// <summary>One user's in-flight jobs, so any of their devices can show what's rendering.</summary>
    public Task<IReadOnlyList<PendingJob>> ListForUserAsync(long userId, CancellationToken ct) =>
        _pending.ListForUserAsync(userId, ct);

    public Task RemoveAsync(long id, CancellationToken ct) => _pending.RemoveAsync(id, ct);
}
