//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// A generation/edit a client handed to ForgeGateway but whose result has not yet been written to the
/// user's history. The originating browser may close (or the gen was started on another device) before
/// it sees the result, so the server-side reconciler polls the gateway for these and writes the final
/// <see cref="HistoryEntry"/> itself. Carries the catalog-level metadata the gateway doesn't know
/// (friendly model name, catalog model id, aspect) — the gateway supplies the image id, effective prompt
/// and marks on completion. Cleared once recorded (or once the job fails / ages out). Unique per (UserId, JobId).
/// </summary>
public sealed class PendingJob
{
    /// <summary>Database surrogate key. 0 for a not-yet-persisted entry.</summary>
    public long Id { get; init; }

    public required long UserId { get; init; }

    /// <summary>ForgeGateway job id (doubles as its promptId) — what the reconciler polls /result/{JobId} with.</summary>
    public required string JobId { get; init; }

    /// <summary>The raw prompt the client sent; used only as a fallback when the gateway returns no effective prompt.</summary>
    public required string Prompt { get; init; }

    /// <summary>Friendly model name shown in the UI (the gateway only knows the checkpoint title).</summary>
    public required string ModelFriendly { get; init; }

    /// <summary>Catalog model id (the gateway has no concept of it).</summary>
    public required string ModelId { get; init; }

    /// <summary>"square" | "landscape" | "portrait", or "" for an edit.</summary>
    public required string Aspect { get; init; }

    /// <summary>When the job was registered (≈ submit time); the reconciler ages out rows older than its backstop.</summary>
    public required DateTime CreatedAtUtc { get; init; }
}
