using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>
/// Durable, write-through storage for render jobs (<see cref="JobRecord"/> + its slots). The in-memory job queue is a
/// cache over these rows: every state transition is persisted so a job survives an app restart, the cross-session
/// active feed is consistent, and a finalized job is recoverable by id after it leaves memory.
/// </summary>
public interface IJobRepository
{
    /// <summary>Insert or update a job and all of its slots in one transaction (write-through on every transition).
    /// The job's <c>Slots</c> are upserted by <c>(JobId, SlotIndex)</c>; slots are never deleted here (a job's slot set
    /// is fixed at enqueue).</summary>
    Task UpsertAsync(JobRecord job, CancellationToken ct);

    /// <summary>One job by id with its slots ordered by index, or null if unknown. Used for the client's
    /// "the job vanished from the active feed — fetch its finalized result" lookup; reads across instances (durable).</summary>
    Task<JobRecord?> GetAsync(string jobId, CancellationToken ct);

    /// <summary>This instance's still-active jobs (Status=Active) with their slots, oldest first — loaded on startup to
    /// rehydrate the in-memory queue so an app restart resumes in-flight work instead of orphaning it.</summary>
    Task<IReadOnlyList<JobRecord>> ListActiveForMachineAsync(string machineName, CancellationToken ct);

    /// <summary>
    /// How many images the user's most recent job has PRODUCED so far (0 when they have never generated). This is what
    /// sizes the compose page's Recent strip to "the current-or-last batch": produced rather than Total, because a job
    /// that made 10 of 50 must size to 10 — sizing to what it will eventually make pads the strip out with images from
    /// before the batch started. Not scoped to a machine: the user's images are theirs wherever they were rendered.
    /// </summary>
    Task<int> CountLatestBatchImagesAsync(long userId, CancellationToken ct);

    /// <summary>One page of this machine's jobs (every owner, every status), newest first — the cross-user queue/history
    /// view. Each job carries lightweight slots (index, kind, state, image id) for the row's kind badge + produced count.
    /// Prompts stay private: only the <paramref name="viewerUserId"/>'s OWN jobs have their prompt decrypted; every other
    /// owner's prompt is returned blank. Returns the page plus the total job count (for the pager).</summary>
    Task<PagedResult<JobRecord>> ListPageAsync(
        string machineName, long viewerUserId, int page, int pageSize, CancellationToken ct);

    /// <summary>Finalize a job as failed: every non-terminal slot becomes Error with <paramref name="reason"/>, and the
    /// job goes Status=Error with FinishedAtUtc set. For a job this instance owns but cannot bring back into memory —
    /// without it the row stays Active forever, uncancellable (Cancel only knows in-memory jobs) and unfinishable.</summary>
    Task FailAsync(string jobId, string reason, CancellationToken ct);

    /// <summary>Finalize a job as CANCELLED: every non-terminal slot becomes Cancelled, and the job goes
    /// Status=Cancelled with FinishedAtUtc set. Same shape as <see cref="FailAsync"/> and deliberately not the same
    /// call — a job the user stopped did not fail, and a row that says Error cannot be told apart from one that did.</summary>
    Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>Drop this job's slots whose produced image no longer exists, and the job itself if that empties it.
    /// Called once at finalization: a slot is only deletable when its job is finalized (a live job re-upserts its whole
    /// slot set), so an image deleted while its batch was still running leaves a slot behind until this runs.</summary>
    Task SweepDeletedImageSlotsAsync(string jobId, CancellationToken ct);

    /// <summary>The generation request (parameters incl. the seed) that produced a given image, decrypted, plus the
    /// owning user id (so the caller can gate access to the owner). Null if no slot produced that image id. This is how
    /// the slot's stored spec is retrieved by the produced image. Assembled from its typed columns and reference
    /// rows, not read out of a blob.</summary>
    Task<ImageRequestRecord?> GetRequestByImageAsync(string imageId, CancellationToken ct);
}
