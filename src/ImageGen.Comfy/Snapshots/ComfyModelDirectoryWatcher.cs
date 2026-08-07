using ImageGen.Application.Snapshots;

namespace ImageGen.Comfy.Snapshots;

/// <summary>
/// Watches ComfyUI's on-disk model roots (as reported by the <see cref="ComfyFolderPaths"/> snapshot) and invalidates
/// <see cref="ComfyFilesByKind"/> the moment a model file appears, disappears, or is renamed — so a finished download
/// surfaces in the picker without waiting out the 5-minute backstop (#198).
///
/// <list type="bullet">
/// <item>Created / Deleted / Renamed only — NOT Changed: an in-progress multi-GB download fires Changed per write
/// chunk and would invalidate continuously. The worker's coalescing bounds that to one in-flight rebuild, but there is
/// no reason to generate the storm. There is no time-based debounce — coalescing is the throttle.</item>
/// <item>Only model extensions (.safetensors/.ckpt/.pt/.gguf) count, so temp/partial download names are ignored.</item>
/// <item>A reported root that does not exist on THIS machine (remote ComfyUI) is skipped — the backstop covers it.</item>
/// <item>A watcher error (buffer overflow) re-arms and invalidates once rather than dying silently.</item>
/// </list>
///
/// <para>An event proves the DISK changed, not that ComfyUI's own folder listing reflects it yet; ComfyUI mtime-checks
/// its listings, so the re-probe should see the file, and if a rebuild lands before ComfyUI notices, the backstop
/// corrects it.</para>
/// </summary>
public sealed class ComfyModelDirectoryWatcher(ISnapshot<ComfyFilesByKind> filesByKind, ILogger<ComfyModelDirectoryWatcher> log)
    : IDisposable
{
    private static readonly string[] ModelExtensions = [".safetensors", ".ckpt", ".pt", ".gguf"];

    private readonly ISnapshot<ComfyFilesByKind> _filesByKind = filesByKind;
    private readonly ILogger<ComfyModelDirectoryWatcher> _log = log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Arm a recursive watcher on each reported root that exists locally, skipping roots already watched and roots that
    /// belong to another machine. Called from the <see cref="ComfyFolderPaths"/> rebuild, so the watched set follows
    /// whatever roots ComfyUI reports.
    /// </summary>
    public void Sync(IEnumerable<string> roots)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Reconcile to the reported set: the roots that exist on THIS machine (remote roots skipped — the backstop
            // covers them). Anything else is dropped, so the watched set follows what ComfyUI reports rather than only
            // ever growing.
            HashSet<string> desired = new(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (Directory.Exists(root))
                {
                    _ = desired.Add(Path.GetFullPath(root));
                }
            }

            // Drop watchers for roots no longer reported (a renderer repoint, a reconfigured model path), so a stale
            // directory can't keep firing invalidations or pin its handle for the process lifetime.
            foreach (string stale in _watchers.Keys.Where(k => !desired.Contains(k)).ToList())
            {
                Detach(_watchers[stale]);
                _ = _watchers.Remove(stale);
                _log.LogInformation("Stopped watching ComfyUI model root (no longer reported): {Root}", stale);
            }

            // Arm the newly-reported roots; already-watched ones are left in place.
            foreach (string full in desired)
            {
                if (_watchers.ContainsKey(full))
                {
                    continue;
                }

                if (TryCreate(full) is { } watcher)
                {
                    _watchers[full] = watcher;
                    _log.LogInformation("Watching ComfyUI model root for changes: {Root}", full);
                }
            }
        }
    }

    private FileSystemWatcher? TryCreate(string directory)
    {
        try
        {
            FileSystemWatcher watcher = new(directory)
            {
                IncludeSubdirectories = true,
                // FileName/DirectoryName cover create/delete/rename; NotifyFilters.Size/LastWrite are deliberately
                // absent so an in-progress download's per-chunk writes never fire an event.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            watcher.Created += OnEntryChanged;
            watcher.Deleted += OnEntryChanged;
            watcher.Renamed += OnEntryRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogWarning(ex, "Could not watch ComfyUI model root {Root}; relying on the backstop for it.", directory);
            return null;
        }
    }

    private void OnEntryChanged(object sender, FileSystemEventArgs e)
    {
        if (IsModelFile(e.FullPath))
        {
            _filesByKind.Invalidate();
        }
    }

    private void OnEntryRenamed(object sender, RenamedEventArgs e)
    {
        // Either side mattering — a rename INTO a model name (finished download) or OUT of one — is a change.
        if (IsModelFile(e.FullPath) || IsModelFile(e.OldFullPath))
        {
            _filesByKind.Invalidate();
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _log.LogWarning(e.GetException(), "A ComfyUI model-root watcher errored; re-arming and re-probing once.");
        if (sender is FileSystemWatcher failed)
        {
            ReArm(failed);
        }

        // The buffer overflowed, so events were lost — re-probe unconditionally rather than trust the partial stream.
        _filesByKind.Invalidate();
    }

    private void ReArm(FileSystemWatcher failed)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;   // Dispose() tears every watcher down
            }

            // Locate the failed watcher by IDENTITY, not by path: a second Error callback for the same watcher (a
            // double buffer-overflow) must not evict the healthy replacement the first callback already installed at
            // that path. If it is no longer tracked, the first callback already replaced-and-disposed it — do nothing.
            string? key = _watchers.FirstOrDefault(kv => ReferenceEquals(kv.Value, failed)).Key;
            if (key is null)
            {
                return;
            }

            Detach(failed);
            _ = _watchers.Remove(key);
            if (Directory.Exists(key) && TryCreate(key) is { } replacement)
            {
                _watchers[key] = replacement;
            }
        }
    }

    /// <summary>Whether a path names a model file (by extension) — so temp/partial download names never invalidate.</summary>
    internal static bool IsModelFile(string path) =>
        ModelExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>How many roots are currently watched — for tests to confirm remote roots are skipped.</summary>
    internal int WatchedCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    private void Detach(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;   // throws if the watcher was already disposed (a raced Error/Dispose)
        }
        catch (ObjectDisposedException)
        {
            // Already torn down elsewhere; the handler removals and Dispose below are idempotent.
        }

        watcher.Created -= OnEntryChanged;
        watcher.Deleted -= OnEntryChanged;
        watcher.Renamed -= OnEntryRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (FileSystemWatcher watcher in _watchers.Values)
            {
                Detach(watcher);
            }

            _watchers.Clear();
        }
    }
}
