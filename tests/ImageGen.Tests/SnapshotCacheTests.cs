using ImageGen.Application.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.Tests;

/// <summary>
/// The snapshot-cache core semantics (#197): warm-on-startup, read-blocks-until-rebuilt after invalidation, coalescing
/// of many invalidations + many readers into one loader run, faulted-rethrows-and-recovers, and the per-registration
/// backstop tick (a backstop failure faults the entry). Every wait here is event-driven off a
/// <see cref="TaskCompletionSource"/> the loader signals — never a wall-clock sleep — so a broken build hangs rather
/// than passing on a lucky delay.
/// </summary>
public sealed class SnapshotCacheTests
{
    /// <summary>Spins up a worker over the registered sources and cancels it on dispose.</summary>
    private sealed class Harness(ServiceProvider provider, CancellationTokenSource cts, Task run) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ISnapshot<T> Snapshot<T>() => Provider.GetRequiredService<ISnapshot<T>>();

        public SnapshotEntry<T> Entry<T>() => Provider.GetRequiredService<SnapshotEntry<T>>();

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }

            cts.Dispose();
            await Provider.DisposeAsync();
        }
    }

    private static Harness Start(Action<IServiceCollection> register)
    {
        ServiceCollection services = new();
        _ = services.AddLogging();
        register(services);
        ServiceProvider provider = services.BuildServiceProvider();

        // Resolving the worker attaches every entry to it; then run the loop under a cancelable token.
        SnapshotSyncWorker worker = provider.GetRequiredService<SnapshotSyncWorker>();
        CancellationTokenSource cts = new();
        Task run = Task.Run(() => worker.RunAsync(cts.Token));
        return new Harness(provider, cts, run);
    }

    [Fact]
    public async Task Startup_warms_every_registered_source_once_before_any_read()
    {
        int runsA = 0, runsB = 0;
        TaskCompletionSource warmedA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource warmedB = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using Harness h = Start(services =>
        {
            _ = services.AddSnapshot<string>((_, _) =>
            {
                _ = Interlocked.Increment(ref runsA);
                _ = warmedA.TrySetResult();
                return Task.FromResult("A");
            }, new SnapshotOptions());
            _ = services.AddSnapshot<int>((_, _) =>
            {
                _ = Interlocked.Increment(ref runsB);
                _ = warmedB.TrySetResult();
                return Task.FromResult(42);
            }, new SnapshotOptions());
        });

        await Task.WhenAll(warmedA.Task, warmedB.Task);

        // Both loaders ran on boot with no GetAsync yet.
        Assert.Equal(1, runsA);
        Assert.Equal(1, runsB);
        Assert.Equal(SnapshotState.Fresh, h.Entry<string>().State);
        Assert.Equal(SnapshotState.Fresh, h.Entry<int>().State);
    }

    [Fact]
    public async Task Fresh_read_returns_warmed_value_without_reloading()
    {
        int runs = 0;
        TaskCompletionSource warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness h = Start(services => services.AddSnapshot<string>((_, _) =>
        {
            _ = Interlocked.Increment(ref runs);
            _ = warmed.TrySetResult();
            return Task.FromResult("value");
        }, new SnapshotOptions()));

        await warmed.Task;
        Assert.Equal("value", await h.Snapshot<string>().GetAsync(CancellationToken.None));
        Assert.Equal("value", await h.Snapshot<string>().GetAsync(CancellationToken.None));
        Assert.Equal(1, runs);   // reads hit the warmed value; no extra loader runs
    }

    [Fact]
    public async Task Read_after_invalidate_blocks_for_rebuild_and_never_serves_the_pre_write_value()
    {
        int runs = 0;
        TaskCompletionSource warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness h = Start(services => services.AddSnapshot<string>((_, _) =>
        {
            int c = Interlocked.Increment(ref runs);
            _ = warmed.TrySetResult();
            return Task.FromResult($"v{c}");
        }, new SnapshotOptions()));

        await warmed.Task;
        Assert.Equal("v1", await h.Snapshot<string>().GetAsync(CancellationToken.None));

        h.Snapshot<string>().Invalidate();
        // The very next read reflects post-invalidation state — it awaits the rebuild, never re-serving v1.
        Assert.Equal("v2", await h.Snapshot<string>().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Many_invalidations_and_many_readers_collapse_into_one_rebuild()
    {
        int runs = 0;
        TaskCompletionSource warmEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource warmRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using Harness h = Start(services => services.AddSnapshot<string>(async (_, _) =>
        {
            int c = Interlocked.Increment(ref runs);
            if (c == 1)
            {
                _ = warmEntered.TrySetResult();   // warm run is now in flight...
                await warmRelease.Task;       // ...and held here while we pile up invalidations + readers
            }

            return $"v{c}";
        }, new SnapshotOptions()));

        await warmEntered.Task;

        // While the warm rebuild is in flight, fire many invalidations and start many concurrent readers.
        ISnapshot<string> snap = h.Snapshot<string>();
        for (int i = 0; i < 5; i++)
        {
            snap.Invalidate();
        }

        Task<string>[] readers = [.. Enumerable.Range(0, 5).Select(_ => snap.GetAsync(CancellationToken.None).AsTask())];

        // Release the warm run; the accumulated invalidations collapse into exactly one follow-up rebuild.
        _ = warmRelease.TrySetResult();
        string[] results = await Task.WhenAll(readers);

        Assert.All(results, r => Assert.Equal("v2", r));   // every reader sees the single coalesced rebuild
        Assert.Equal(2, runs);                             // warm (1) + one coalesced rebuild (2); not 6
    }

    private sealed class LoaderBoom : Exception
    {
        public LoaderBoom() : base("loader boom") { }
    }

    [Fact]
    public async Task Faulted_entry_rethrows_the_loader_exception_then_recovers_on_the_next_successful_rebuild()
    {
        bool fail = true;
        TaskCompletionSource warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness h = Start(services => services.AddSnapshot<string>((_, _) =>
        {
            _ = warmed.TrySetResult();
            return fail ? throw new LoaderBoom() : Task.FromResult("recovered");
        }, new SnapshotOptions()));

        await warmed.Task;

        // A read of a faulted source forces one fresh attempt; it still fails, so the loader's real exception surfaces.
        _ = await Assert.ThrowsAsync<LoaderBoom>(async () => await h.Snapshot<string>().GetAsync(CancellationToken.None));

        // Recovery is one read away: flip the loader to succeed and the next read rebuilds and returns.
        fail = false;
        Assert.Equal("recovered", await h.Snapshot<string>().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Peek_returns_the_held_value_without_blocking_on_a_pending_rebuild()
    {
        TaskCompletionSource warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource rebuildEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        await using Harness h = Start(services => services.AddSnapshot<string>(async (_, _) =>
        {
            int c = Interlocked.Increment(ref runs);
            if (c == 1)
            {
                _ = warmed.TrySetResult();
                return "v1";
            }

            _ = rebuildEntered.TrySetResult();   // second rebuild is now in flight, held...
            await release.Task;
            return "v2";
        }, new SnapshotOptions()));

        await warmed.Task;
        Assert.Equal("v1", h.Entry<string>().PeekCurrent());   // fresh peek returns the held value

        h.Snapshot<string>().Invalidate();
        await rebuildEntered.Task;   // the rebuild is in flight (stale entry) and blocked

        // Peek must return the last good value immediately — it must NOT await the in-flight rebuild (that is what lets a
        // loader running on the single worker read another source without deadlocking it).
        Assert.Equal("v1", h.Entry<string>().PeekCurrent());

        _ = release.TrySetResult();
        Assert.Equal("v2", await h.Snapshot<string>().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Peek_rethrows_the_loader_exception_when_the_source_is_faulted()
    {
        TaskCompletionSource warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness h = Start(services => services.AddSnapshot<string>((_, _) =>
        {
            _ = warmed.TrySetResult();
            return Task.FromException<string>(new LoaderBoom());
        }, new SnapshotOptions()));

        await warmed.Task;
        // Drive it to a settled Faulted state (GetAsync forces one recovery attempt, which also fails), then peek.
        _ = await Assert.ThrowsAsync<LoaderBoom>(async () => await h.Snapshot<string>().GetAsync(CancellationToken.None));
        _ = Assert.Throws<LoaderBoom>(() => h.Entry<string>().PeekCurrent());
    }

    [Fact]
    public async Task A_loader_that_peeks_another_source_resolves_through_the_worker_without_deadlock()
    {
        // The bindings source's real shape: source B's loader reads source A via PeekCurrent (never GetAsync), so B
        // rebuilding on the single worker never blocks waiting for A's rebuild — the reentrant-deadlock guard.
        await using Harness h = Start(services =>
        {
            _ = services.AddSnapshot<string>((_, _) => Task.FromResult("AAAA"), new SnapshotOptions());
            _ = services.AddSnapshot<int>((sp, _) =>
                Task.FromResult(sp.GetRequiredService<ISnapshot<string>>().PeekCurrent().Length), new SnapshotOptions());
        });

        // Warm processes A before B (registration/FIFO order), so B peeks A's freshly-settled value.
        Assert.Equal(4, await h.Snapshot<int>().GetAsync(CancellationToken.None));

        // Invalidating both and reading B still completes — B peeks A's held value rather than awaiting its rebuild.
        h.Snapshot<int>().Invalidate();
        h.Snapshot<string>().Invalidate();
        Assert.Equal(4, await h.Snapshot<int>().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Backstop_tick_reruns_the_loader_and_a_backstop_failure_faults_the_entry()
    {
        int runs = 0;
        TaskCompletionSource backstopFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness h = Start(services => services.AddSnapshot<string>((_, _) =>
        {
            int c = Interlocked.Increment(ref runs);
            if (c == 1)
            {
                return Task.FromResult("ok");   // warm succeeds
            }

            _ = backstopFailed.TrySetResult();       // a later (backstop-driven) run fails
            return Task.FromException<string>(new LoaderBoom());
        }, new SnapshotOptions { BackstopInterval = TimeSpan.FromMilliseconds(50) }));

        Assert.Equal("ok", await h.Snapshot<string>().GetAsync(CancellationToken.None));

        // The backstop tick fires on its own cadence and re-runs the loader; this run fails.
        await backstopFailed.Task;

        // A backstop failure faults the entry rather than keeping the stale value alive — the read surfaces the error
        // (its forced recovery attempt fails too, since the loader keeps failing after warm).
        _ = await Assert.ThrowsAsync<LoaderBoom>(async () => await h.Snapshot<string>().GetAsync(CancellationToken.None));
    }
}
