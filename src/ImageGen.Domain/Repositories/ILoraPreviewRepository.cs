//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>
/// The machine-level cache of LoRA preview media (dbo.LoraPreview): the representative image or clip CivitAI returns
/// for a file, downloaded once and served from this box instead of hotlinking the CivitAI CDN. Keyed by the plain
/// subfolder-qualified filename, like <see cref="ILoraMetaRepository"/> — a shared machine asset, nothing encrypted.
/// </summary>
public interface ILoraPreviewRepository
{
    /// <summary>The cached preview bytes + content type for one LoRA, or null when none has been cached.</summary>
    Task<LoraPreviewBlob?> GetAsync(string loraName, CancellationToken ct);

    /// <summary>The content types of the cached previews for the given LoRAs — only those actually cached. Lets a
    /// listing decide, per file, whether a preview exists and whether it is a video, without pulling the bytes.</summary>
    Task<IReadOnlyDictionary<string, string>> GetContentTypesAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct);

    /// <summary>Insert or replace one LoRA's cached preview media.</summary>
    Task UpsertAsync(string loraName, byte[] bytes, string contentType, DateTime nowUtc, CancellationToken ct);

    /// <summary>Drop the cached preview media for these LoRA files (the "refresh" action re-downloads them).</summary>
    Task DeleteAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct);
}
