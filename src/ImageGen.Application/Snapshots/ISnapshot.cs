namespace ImageGen.Application.Snapshots;

/// <summary>
/// The injected read surface for one machine-global cached value, rebuilt by the single <see cref="SnapshotSyncWorker"/>.
/// A source caches a primitive I/O result (a ComfyUI probe sweep, a machine-scoped SQL read, gen-timing averages) that
/// changes only on discrete events; endpoint shaping stays per-request in-memory work above this port.
///
/// <para>Read semantics (see <see cref="GetAsync"/>): a fresh value returns immediately with no I/O; a stale,
/// never-loaded, or faulted entry blocks on a worker rebuild and surfaces its outcome. The cache never masks an error —
/// a stale or faulted entry never serves its old value as if healthy (#187 guardrail).</para>
/// </summary>
/// <typeparam name="T">The cached value type — a whole immutable snapshot, replaced wholesale on each rebuild.</typeparam>
public interface ISnapshot<T>
{
    /// <summary>
    /// The current value.
    ///
    /// <list type="bullet">
    /// <item>Fresh — returns the held value immediately, no I/O.</item>
    /// <item>Stale or never-loaded — enqueues a rebuild on the worker and awaits it, then returns the rebuilt value
    /// (or throws if that rebuild failed).</item>
    /// <item>Faulted — enqueues a fresh rebuild attempt (recovery is one read away); returns the value if it now
    /// succeeds, otherwise throws the loader's real exception so callers keep their existing error mapping
    /// (e.g. <see cref="System.Net.Http.HttpRequestException"/> → 502).</item>
    /// </list>
    /// </summary>
    ValueTask<T> GetAsync(CancellationToken ct);

    /// <summary>
    /// The last successfully-loaded value, WITHOUT scheduling or awaiting a rebuild. This exists for one specific
    /// caller: a snapshot loader that runs ON the single sync worker and needs another source's current value —
    /// awaiting <see cref="GetAsync"/> there would deadlock, because the worker can't run that source's rebuild while
    /// blocked inside this one. A Fresh or Stale entry returns its held value (best-effort; a concurrent rebuild will
    /// re-run this loader with the newer value); a Faulted entry rethrows the loader's real exception (so the caller
    /// faults too, rather than hanging); a never-loaded entry throws. Do NOT use this from request handlers — use
    /// <see cref="GetAsync"/>, which blocks for freshness.
    /// </summary>
    T PeekCurrent();

    /// <summary>
    /// Marks the entry stale and signals the worker to rebuild. Cheap and safe from any thread; this is the single hook
    /// every trigger uses (write endpoints, ComfyUI restart/patch hooks, file watchers, refresh buttons). After this
    /// call the next <see cref="GetAsync"/> reflects post-invalidation state — a pre-invalidation value is never served
    /// again.
    /// </summary>
    void Invalidate();
}
