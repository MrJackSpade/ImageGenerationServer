using System.Security.Cryptography;
using ImageGen.Application.Civitai;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace ImageGen.Comfy;

/// <summary>
/// Populates and serves the CivitAI cache for LoRA files: cached metadata comes straight from <see cref="ILoraMetaRepository"/>;
/// a file not yet cached is located on disk (via ComfyUI's folder roots), SHA256-hashed, and looked up on CivitAI, then
/// cached. Hashing only happens when CivitAI lookups are enabled — off, this returns whatever is already cached.
/// </summary>
public sealed class LoraMetaCatalog(
    ComfyClient comfy, ICivitaiClient civitai, ILoraMetaRepository repo, ILogger<LoraMetaCatalog> log) : ILoraMetaCatalog
{
    public async Task<IReadOnlyDictionary<string, LoraMeta>> EnsureAndGetAsync(IReadOnlyList<string> loraNames, CancellationToken ct)
    {
        var cached = new Dictionary<string, LoraMeta>(await repo.GetManyAsync(loraNames, ct), StringComparer.OrdinalIgnoreCase);
        var missing = loraNames.Where(n => !cached.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0 || !civitai.IsEnabled())
            return cached;

        var folders = await comfy.GetFolderPathsAsync(ct);
        var loraRoots = folders.TryGetValue("loras", out var lr) ? lr : [];

        foreach (var name in missing)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveInRoots(loraRoots, name);
            if (path is null) continue;   // enumerated by ComfyUI but not resolvable here (remote renderer) — skip

            string sha;
            try
            {
                await using var fs = File.OpenRead(path);
                using var alg = SHA256.Create();
                sha = Convert.ToHexString(await alg.ComputeHashAsync(fs, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogInformation("Could not hash LoRA {Name}: {Reason}", name, ex.Message);
                continue;
            }

            // A null result (not on CivitAI) is cached too — with the hash and empty words — so the file isn't
            // re-hashed on every visit. Enabling is the gate; a 404 is a real "no metadata", not a transient miss.
            var info = await civitai.LookupByHashAsync(sha, ct);
            var meta = new LoraMeta(name, sha, info?.TrainedWords ?? [], info?.ModelName, info?.PreviewImageUrl, DateTime.UtcNow);
            await repo.UpsertAsync(meta, ct);
            cached[name] = meta;
        }
        return cached;
    }

    private static string? ResolveInRoots(IReadOnlyList<string> roots, string name)
    {
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
