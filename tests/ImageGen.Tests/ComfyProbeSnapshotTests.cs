using ImageGen.Application.Snapshots;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// The ComfyUI capability-probe snapshot pieces (#198): the derived flat present-files union, and the model-directory
/// watcher's extension filtering / local-root gating / fire-on-model-file behavior. ComfyUI HTTP itself is exercised by
/// the live smoke tests; here we pin the deterministic logic.
/// </summary>
public sealed class ComfyProbeSnapshotTests
{
    /// <summary>A stand-in snapshot that records invalidations and lets a test await the next one.</summary>
    private sealed class FakeSnapshot<T> : ISnapshot<T>
    {
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Invalidations { get; private set; }

        public Task NextInvalidation
        {
            get
            {
                lock (this)
                {
                    return _next.Task;
                }
            }
        }

        public ValueTask<T> GetAsync(CancellationToken ct) => throw new NotSupportedException();

        public T PeekCurrent() => throw new NotSupportedException();

        public void Invalidate()
        {
            lock (this)
            {
                Invalidations++;
                TaskCompletionSource fired = _next;
                _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = fired.TrySetResult();
            }
        }
    }

    [Fact]
    public void Files_by_kind_derives_the_flat_union_across_kinds()
    {
        Dictionary<RequirementKind, IReadOnlyList<string>> byKind = new()
        {
            [RequirementKind.Checkpoint] = ["a.safetensors", "b.safetensors"],
            [RequirementKind.Lora] = ["l1.safetensors", "b.safetensors"],   // b overlaps checkpoint
            [RequirementKind.Vae] = ["ae.safetensors"],
        };

        ComfyFilesByKind snapshot = new(byKind);

        Assert.Equal(
            new HashSet<string>(["a.safetensors", "b.safetensors", "l1.safetensors", "ae.safetensors"], StringComparer.OrdinalIgnoreCase),
            snapshot.AllFiles);
        Assert.Equal(["l1.safetensors", "b.safetensors"], snapshot.ForKind(RequirementKind.Lora));
        Assert.Empty(snapshot.ForKind(RequirementKind.ControlNet));   // absent kind → empty, not a throw
    }

    [Theory]
    [InlineData("model.safetensors", true)]
    [InlineData("MODEL.SAFETENSORS", true)]
    [InlineData("weights.ckpt", true)]
    [InlineData("t.pt", true)]
    [InlineData("q.gguf", true)]
    [InlineData("model.safetensors.part", false)]   // in-progress download
    [InlineData("model.tmp", false)]
    [InlineData("readme.txt", false)]
    [InlineData("noext", false)]
    public void Watcher_treats_only_model_extensions_as_model_files(string name, bool expected) =>
        Assert.Equal(expected, ComfyModelDirectoryWatcher.IsModelFile(name));

    [Fact]
    public void Watcher_skips_roots_that_do_not_exist_locally()
    {
        FakeSnapshot<ComfyFilesByKind> files = new();
        using ComfyModelDirectoryWatcher watcher = new(files, NullLogger<ComfyModelDirectoryWatcher>.Instance);

        // A remote ComfyUI reports absolute roots that aren't on THIS box — they must be skipped, not error.
        watcher.Sync([Path.Combine(Path.GetTempPath(), "imggen-does-not-exist-" + Guid.NewGuid().ToString("N"))]);

        Assert.Equal(0, watcher.WatchedCount);
        Assert.Equal(0, files.Invalidations);
    }

    [Fact]
    public void Watcher_prunes_roots_no_longer_reported_and_keeps_the_ones_still_reported()
    {
        string a = Path.Combine(Path.GetTempPath(), "imggen-watch-a-" + Guid.NewGuid().ToString("N"));
        string b = Path.Combine(Path.GetTempPath(), "imggen-watch-b-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(a);
        _ = Directory.CreateDirectory(b);
        try
        {
            FakeSnapshot<ComfyFilesByKind> files = new();
            using ComfyModelDirectoryWatcher watcher = new(files, NullLogger<ComfyModelDirectoryWatcher>.Instance);

            watcher.Sync([a, b]);
            Assert.Equal(2, watcher.WatchedCount);

            // A later report drops b (renderer repointed / model path reconfigured) — the watched set must follow it,
            // not only grow.
            watcher.Sync([a]);
            Assert.Equal(1, watcher.WatchedCount);

            // Re-adding is idempotent: a already watched stays a single watcher.
            watcher.Sync([a]);
            Assert.Equal(1, watcher.WatchedCount);
        }
        finally
        {
            foreach (string dir in new[] { a, b })
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // best-effort cleanup
                }
            }
        }
    }

    [Fact]
    public async Task Watcher_invalidates_when_a_model_file_appears_in_a_watched_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "imggen-watch-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            FakeSnapshot<ComfyFilesByKind> files = new();
            using ComfyModelDirectoryWatcher watcher = new(files, NullLogger<ComfyModelDirectoryWatcher>.Instance);
            watcher.Sync([root]);
            Assert.Equal(1, watcher.WatchedCount);

            Task fired = files.NextInvalidation;
            await File.WriteAllTextAsync(Path.Combine(root, "fresh-download.safetensors"), "weights");

            // Event-driven: if the watcher never fires, this awaits forever (a hang the developer sees) rather than
            // passing on a lucky sleep.
            await fired;
            Assert.True(files.Invalidations >= 1);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }
}
