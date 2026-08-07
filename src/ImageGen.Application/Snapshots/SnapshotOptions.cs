using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Application.Snapshots;

/// <summary>
/// Per-source registration options. The core ships NO default interval and must not invent one — every value is set
/// explicitly by the implementation ticket that registers the source (#187/#197).
/// </summary>
public sealed class SnapshotOptions
{
    /// <summary>
    /// The backstop refresh cadence: the worker re-runs the loader on this interval and swaps in the result, as a
    /// safety net for out-of-band change the flush triggers can't see. Null = no timer; the source refreshes only via
    /// <see cref="ISnapshot{T}.Invalidate"/>. A backstop failure faults the entry rather than keeping the stale value
    /// alive.
    /// </summary>
    [AllowNullable("null means the source has no backstop timer at all — it refreshes only on Invalidate(); no interval value can express 'never'.")]
    public TimeSpan? BackstopInterval { get; init; }
}

/// <summary>The lifecycle state of a snapshot entry, exposed for diagnostics and tests.</summary>
public enum SnapshotState
{
    /// <summary>No loader run has settled yet.</summary>
    NeverLoaded,

    /// <summary>The held value reflects the latest requested version — served immediately.</summary>
    Fresh,

    /// <summary>Invalidated since the last settle; the held value is not served, a rebuild is (or will be) queued.</summary>
    Stale,

    /// <summary>The last loader run threw; the held exception is rethrown on read after one fresh recovery attempt.</summary>
    Faulted,
}
