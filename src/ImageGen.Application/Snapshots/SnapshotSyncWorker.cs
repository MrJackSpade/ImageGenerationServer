using System.Threading.Channels;

namespace ImageGen.Application.Snapshots;

/// <summary>
/// The ONE single-threaded rebuild loop for every registered snapshot source. It stays a plain class (the Web host
/// adapts <see cref="RunAsync"/> to a <c>BackgroundService</c>, so the Application layer keeps no dependency on the
/// generic host — the same split as the render orchestrator).
///
/// <list type="bullet">
/// <item>Rebuilds run strictly serially off one channel — no concurrent probe storms, no interleaving.</item>
/// <item>Coalescing lives in <see cref="SnapshotEntry"/>: invalidations while a rebuild is queued or in flight collapse
/// into one run, and all awaiting readers share one in-flight task.</item>
/// <item>Warm at startup: every registered source is rebuilt once on boot, so the first page load is a hit and any
/// push-into-in-memory-state a loader performs has happened before the first submit.</item>
/// <item>Per-source backstop tick: a registration with a <see cref="SnapshotOptions.BackstopInterval"/> is invalidated
/// on that cadence — the safety net for out-of-band change. The tick only invalidates; the actual loader run still
/// happens serially on the channel loop.</item>
/// </list>
/// </summary>
public sealed class SnapshotSyncWorker
{
    private readonly IReadOnlyList<SnapshotEntry> _entries;
    private readonly ILogger<SnapshotSyncWorker> _logger;
    private readonly Channel<SnapshotEntry> _channel =
        Channel.CreateUnbounded<SnapshotEntry>(new UnboundedChannelOptions { SingleReader = true });

    /// <param name="entries">Every registered source (resolved from DI); the worker attaches itself to each.</param>
    /// <param name="logger">Diagnostics.</param>
    public SnapshotSyncWorker(IEnumerable<SnapshotEntry> entries, ILogger<SnapshotSyncWorker> logger)
    {
        _entries = [.. entries];
        _logger = logger;
        foreach (SnapshotEntry entry in _entries)
        {
            entry.Attach(this);
        }
    }

    /// <summary>Queue one rebuild for an entry. Idempotent at the entry level (the entry guards against double-queue).</summary>
    internal void Enqueue(SnapshotEntry entry) => _channel.Writer.TryWrite(entry);

    /// <summary>
    /// The rebuild loop: warm every source once, arm the backstop ticks, then drain rebuild requests one at a time
    /// until shutdown. Adapted to a hosted service by the Web host.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Snapshot sync worker starting; warming {Count} source(s).", _entries.Count);
        foreach (SnapshotEntry entry in _entries)
        {
            entry.EnsureInitialLoad();
        }

        List<Task> backstops = [];
        foreach (SnapshotEntry entry in _entries)
        {
            if (entry.BackstopInterval is TimeSpan interval)
            {
                backstops.Add(BackstopLoop(entry, interval, ct));
            }
        }

        try
        {
            await foreach (SnapshotEntry entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await entry.RebuildAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            await Task.WhenAll(backstops).ConfigureAwait(false);
        }
    }

    private static async Task BackstopLoop(SnapshotEntry entry, TimeSpan interval, CancellationToken ct)
    {
        using PeriodicTimer timer = new(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                entry.Invalidate();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
