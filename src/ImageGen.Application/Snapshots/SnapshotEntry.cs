using ImageGen.Domain.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ImageGen.Application.Snapshots;

/// <summary>
/// One registered source's state and rebuild machinery — the non-generic surface the <see cref="SnapshotSyncWorker"/>
/// drives (warm, backstop, serial rebuild) without knowing the value type. All mutation is under <see cref="_gate"/>.
///
/// <para>Coalescing and post-write freshness ride on two monotonic version counters: <see cref="_targetVersion"/> (the
/// highest version anyone has requested — bumped by <see cref="Invalidate"/> and by the initial load) and
/// <see cref="_settledVersion"/> (the version the held value reflects). A reader is satisfied only by a settle whose
/// captured start version is at least the version outstanding when the reader arrived, so a read after
/// <see cref="Invalidate"/> can never be answered by a rebuild that began before it. Many invalidations before a
/// rebuild starts collapse into one run (the run captures the latest target); many readers awaiting the same version
/// share one settle signal.</para>
/// </summary>
public abstract class SnapshotEntry
{
    private readonly Lock _gate = new();
    private readonly Func<CancellationToken, Task<object?>> _loader;

    /// <summary>Highest requested version; 0 = never requested. Bumped by Invalidate and the initial load.</summary>
    private long _targetVersion;

    /// <summary>Version the held value reflects; 0 = never settled.</summary>
    private long _settledVersion;

    private SnapshotState _state = SnapshotState.NeverLoaded;
    private object? _value;
    private ExceptionDispatchInfo? _error;

    /// <summary>Whether a rebuild for this entry is already queued on the worker channel (guards double-queue).</summary>
    private bool _scheduled;

    private SnapshotSyncWorker? _worker;

    /// <summary>
    /// Completed (and replaced) each time a rebuild settles. Readers capture the current instance under the lock and
    /// await it; the settling rebuild completes exactly that instance under the same lock, so no wakeup is lost.
    /// </summary>
    private TaskCompletionSource _settle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="name">A stable identifier for logs (the value type name).</param>
    /// <param name="backstopInterval">The owner-chosen backstop cadence, or null for flush-only.</param>
    /// <param name="loader">Produces the cached value; its exception is preserved and rethrown to readers on failure.</param>
    protected SnapshotEntry(string name, TimeSpan? backstopInterval, Func<CancellationToken, Task<object?>> loader)
    {
        Name = Domain.Ensure.NotNullOrWhiteSpace(name);
        BackstopInterval = backstopInterval;
        _loader = Domain.Ensure.NotNull(loader);
    }

    /// <summary>A stable identifier for logs — the cached value's type name.</summary>
    public string Name { get; }

    /// <summary>The owner-chosen backstop cadence, or null when the source refreshes only on invalidation.</summary>
    [AllowNullable("null means this source has no backstop timer — it refreshes only on Invalidate(); no interval expresses 'never'.")]
    public TimeSpan? BackstopInterval { get; }

    /// <summary>The current lifecycle state, for diagnostics and tests. Stale is derived from the version counters.</summary>
    public SnapshotState State
    {
        get
        {
            lock (_gate)
            {
                if (_settledVersion == 0)
                {
                    return SnapshotState.NeverLoaded;
                }

                if (_targetVersion > _settledVersion)
                {
                    return SnapshotState.Stale;
                }

                return _state;
            }
        }
    }

    /// <inheritdoc cref="ISnapshot{T}.Invalidate"/>
    public void Invalidate()
    {
        lock (_gate)
        {
            _targetVersion++;
            Schedule();
        }
    }

    /// <summary>Binds the entry to its worker and flushes any work requested before the worker existed.</summary>
    internal void Attach(SnapshotSyncWorker worker)
    {
        lock (_gate)
        {
            _worker = worker;
            Schedule();
        }
    }

    /// <summary>Startup warm: request an initial load if none has been requested yet, then schedule it.</summary>
    internal void EnsureInitialLoad()
    {
        lock (_gate)
        {
            if (_targetVersion == 0)
            {
                _targetVersion = 1;
            }

            Schedule();
        }
    }

    /// <summary>The read path shared by every generic entry; returns the settled value or rethrows the loader error.</summary>
    private protected async ValueTask<object?> GetCoreAsync(CancellationToken ct)
    {
        long need;
        lock (_gate)
        {
            if (_targetVersion == 0)
            {
                _targetVersion = 1;   // never requested → request the initial load
            }

            if (_state == SnapshotState.Fresh && _settledVersion >= _targetVersion)
            {
                return _value;        // fresh and up to date — no I/O
            }

            // An up-to-date faulted entry gets ONE fresh recovery attempt per read (recovery is one read away). Bump
            // only when nothing newer is already pending, so concurrent readers of a faulted source coalesce onto the
            // one recovery run rather than each forcing their own.
            if (_state == SnapshotState.Faulted && _targetVersion == _settledVersion)
            {
                _targetVersion++;
            }

            need = _targetVersion;
            Schedule();
        }

        while (true)
        {
            Task settle;
            lock (_gate)
            {
                if (_settledVersion >= need)
                {
                    if (_state == SnapshotState.Fresh)
                    {
                        return _value;
                    }

                    // The run covering our version failed — rethrow the loader's real exception (Faulted implies _error set).
                    Domain.Ensure.NotNull(_error).Throw();
                }

                Schedule();
                settle = _settle.Task;
            }

            await settle.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The last-settled value with no scheduling or awaiting — see <see cref="ISnapshot{T}.PeekCurrent"/>.</summary>
    private protected object? PeekCore()
    {
        lock (_gate)
        {
            if (_settledVersion == 0)
            {
                throw new InvalidOperationException(
                    $"Snapshot '{Name}' has not loaded yet; it cannot be peeked before its first rebuild settles.");
            }

            if (_state == SnapshotState.Faulted)
            {
                // Rethrow the last fault so an on-worker caller faults too (e.g. ComfyUI-down → HttpRequestException),
                // rather than the caller silently using nothing. Faulted implies _error set (see State field invariants).
                Domain.Ensure.NotNull(_error).Throw();
            }

            return _value;   // Fresh, or Stale (holding the last good value) — best-effort by contract
        }
    }

    /// <summary>Runs the loader once and settles the entry. Called only by the worker, strictly serially.</summary>
    internal async Task RebuildAsync(CancellationToken ct)
    {
        long start;
        lock (_gate)
        {
            _scheduled = false;
            start = _targetVersion;
            if (start <= _settledVersion)
            {
                return;   // a newer settle already covered this request (coalesced away)
            }
        }

        object? value = null;
        ExceptionDispatchInfo? error = null;
        SnapshotState state;
        try
        {
            value = await _loader(ct).ConfigureAwait(false);
            state = SnapshotState.Fresh;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown — do not fault the entry, let the worker loop unwind
        }
        catch (Exception ex)
        {
            error = ExceptionDispatchInfo.Capture(ex);
            state = SnapshotState.Faulted;
        }

        lock (_gate)
        {
            _settledVersion = start;
            _state = state;
            _value = value;
            _error = error;

            TaskCompletionSource settled = _settle;
            _settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = settled.TrySetResult();

            if (_targetVersion > _settledVersion)
            {
                Schedule();   // invalidated mid-build — the just-built value may be stale, rebuild again
            }
        }
    }

    /// <summary>Enqueues one rebuild on the worker if work is outstanding and none is already queued. Under <see cref="_gate"/>.</summary>
    private void Schedule()
    {
        if (_settledVersion >= _targetVersion)
        {
            return;   // nothing outstanding
        }

        if (_scheduled || _worker is null)
        {
            return;   // already queued, or the worker will pick it up on Attach / warm
        }

        _scheduled = true;
        _worker.Enqueue(this);
    }
}

/// <summary>The typed entry: implements <see cref="ISnapshot{T}"/> and adapts a typed loader to the base machinery.</summary>
/// <typeparam name="T">The cached value type.</typeparam>
public sealed class SnapshotEntry<T> : SnapshotEntry, ISnapshot<T>
{
    /// <param name="name">A stable identifier for logs (the value type name).</param>
    /// <param name="backstopInterval">The owner-chosen backstop cadence, or null for flush-only.</param>
    /// <param name="loader">Produces the cached value.</param>
    public SnapshotEntry(string name, TimeSpan? backstopInterval, Func<CancellationToken, Task<T>> loader)
        : base(name, backstopInterval, async ct => await loader(ct).ConfigureAwait(false))
    {
    }

    /// <inheritdoc/>
    public async ValueTask<T> GetAsync(CancellationToken ct)
    {
        object? value = await GetCoreAsync(ct).ConfigureAwait(false);
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Snapshot '{Name}' produced a value that is not a {typeof(T).Name}.");
    }

    /// <inheritdoc/>
    public T PeekCurrent()
    {
        object? value = PeekCore();
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Snapshot '{Name}' produced a value that is not a {typeof(T).Name}.");
    }
}
