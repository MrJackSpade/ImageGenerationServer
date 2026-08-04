//TODO: CHECK FOR FALLBACKS
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using ImageGen.Application.Civitai;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImageGen.Comfy;

/// <summary>
/// Background filler of the CivitAI cache for LoRA files (see <see cref="ILoraMetaPopulator"/>). Surfaces call
/// <see cref="Request"/> and return a stub at once; this drains a queue one file at a time — resolving it on disk,
/// hashing it, looking it up on CivitAI, downloading its preview, and saving the result — while the client polls until
/// the file's row appears.
///
/// <para>Coalescing lives in <see cref="_queued"/>: a file already queued is not enqueued again, so repeated page
/// loads (or several surfaces showing the same file) start no duplicate job. The queue is drained serially on purpose —
/// hashing streams whole multi-hundred-megabyte files, and doing several at once would only thrash the disk; nothing is
/// dropped, just ordered.</para>
///
/// <para>A row is written LAST, after its preview bytes are cached, so "the row exists" — which is how the client and
/// the endpoints read "ready" — never races ahead of the preview it promises.</para>
/// </summary>
public sealed class LoraMetaPopulator(
    IServiceScopeFactory scopes, ComfyClient comfy, ICivitaiClient civitai, ILogger<LoraMetaPopulator> log)
    : BackgroundService, ILoraMetaPopulator
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ComfyUI's loras folder roots, resolved once — they don't move while the app runs.</summary>
    private IReadOnlyList<string>? _loraRoots;

    public void Request(IReadOnlyCollection<string> loraNames)
    {
        // Off means never touch CivitAI — the same gate the lookup itself enforces, applied here so nothing queues.
        if (!civitai.IsEnabled())
            return;
        foreach (var name in loraNames)
            if (!string.IsNullOrWhiteSpace(name) && _queued.TryAdd(name, 0))
                _queue.Writer.TryWrite(name);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var name in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await PopulateOneAsync(name, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One file's failure must not stall the queue. A transient cause (a locked file, CivitAI briefly
                // unreachable) leaves no row, so the next Request re-queues and retries; the error is surfaced here,
                // not swallowed.
                log.LogWarning(ex, "LoRA metadata population failed for {Name}", name);
            }
            finally
            {
                _queued.TryRemove(name, out _);
            }
        }
    }

    private async Task PopulateOneAsync(string name, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var meta = scope.ServiceProvider.GetRequiredService<ILoraMetaRepository>();
        var previews = scope.ServiceProvider.GetRequiredService<ILoraPreviewRepository>();

        var existing = (await meta.GetManyAsync([name], ct)).GetValueOrDefault(name);
        if (existing is not null)
        {
            // Already cached. The only thing that might still be missing is the preview BYTES — for a row written
            // before this box cached previews locally (it kept a CDN URL, not the media). Backfill just that.
            if (string.IsNullOrEmpty(existing.PreviewUrl))
                return;
            if ((await previews.GetContentTypesAsync([name], ct)).ContainsKey(name))
                return;
            if (!await CachePreviewAsync(previews, name, existing.PreviewUrl, ct))
                // The stored URL is dead/unreachable — stop promising a preview, so the file reads as ready and
                // isn't polled forever waiting for bytes that will never arrive.
                await meta.UpsertAsync(existing with { PreviewUrl = null }, ct);
            return;
        }

        var path = ResolveOnDisk(await LoraRootsAsync(ct), name);
        if (path is null)
        {
            // Enumerated by ComfyUI but not resolvable on this box (a remote renderer). Record an empty row so the
            // client stops waiting and the file isn't re-hashed on every visit.
            await meta.UpsertAsync(new LoraMeta(name, null, [], null, null, DateTime.UtcNow), ct);
            return;
        }

        string sha;
        await using (var fs = File.OpenRead(path))
        using (var alg = SHA256.Create())
            sha = Convert.ToHexString(await alg.ComputeHashAsync(fs, ct));

        var info = await civitai.LookupByHashAsync(sha, ct);
        // Preview first, row last: once the row exists, its promised preview is already on disk. The URL is kept ONLY
        // when its bytes were actually cached, so "row exists + PreviewUrl set" always implies a served preview — that
        // invariant is what lets the client stop polling exactly when the card is fully populated.
        var cached = !string.IsNullOrEmpty(info?.PreviewImageUrl)
                     && await CachePreviewAsync(previews, name, info!.PreviewImageUrl!, ct);
        await meta.UpsertAsync(
            new LoraMeta(name, sha, info?.TrainedWords ?? [], info?.ModelName, cached ? info!.PreviewImageUrl : null, DateTime.UtcNow), ct);
    }

    private async Task<bool> CachePreviewAsync(ILoraPreviewRepository previews, string name, string url, CancellationToken ct)
    {
        var p = await civitai.DownloadPreviewAsync(url, ct);
        if (p is null)
            return false;
        await previews.UpsertAsync(name, p.Bytes, p.ContentType, DateTime.UtcNow, ct);
        return true;
    }

    private async Task<IReadOnlyList<string>> LoraRootsAsync(CancellationToken ct)
    {
        if (_loraRoots is not null)
            return _loraRoots;
        var folders = await comfy.GetFolderPathsAsync(ct);
        return _loraRoots = folders.TryGetValue("loras", out var roots) ? roots : [];
    }

    private static string? ResolveOnDisk(IReadOnlyList<string> roots, string name)
    {
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
