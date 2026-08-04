namespace ImageGen.Application.Civitai;

/// <summary>What CivitAI knows about a local model file, looked up by its file hash.</summary>
/// <param name="ModelName">The model's display name on CivitAI (empty when unknown).</param>
/// <param name="TrainedWords">The activation/trigger words the LoRA was trained with. May be empty (a "no trigger" LoRA).</param>
/// <param name="PreviewImageUrl">A representative preview media URL (image or a short clip), or null.</param>
public sealed record CivitaiLoraInfo(string? ModelName, IReadOnlyList<string> TrainedWords, string? PreviewImageUrl);

/// <summary>A LoRA's preview media fetched from the CivitAI CDN: the raw bytes and the content type the CDN reported
/// (image/* or video/*). Cached on this box so the browser never hotlinks CivitAI.</summary>
public sealed record CivitaiPreview(byte[] Bytes, string ContentType);

/// <summary>
/// Looks a local model file up on CivitAI by its file hash (SHA256). Gated by the <c>Civitai:Enabled</c> machine
/// setting — an outbound call to a third party, opt-OUT (default on) like the update check. Returns null when the
/// setting is off, CivitAI is unreachable, or the file isn't published there; the caller treats that as "nothing to add".
/// </summary>
public interface ICivitaiClient
{
    /// <summary>Whether CivitAI lookups are turned on (the <c>Civitai:Enabled</c> setting). Lets a caller skip the
    /// expensive hashing step entirely when the feature is off.</summary>
    bool IsEnabled();

    Task<CivitaiLoraInfo?> LookupByHashAsync(string sha256, CancellationToken ct);

    /// <summary>Download a preview media URL (returned by <see cref="LookupByHashAsync"/>) so it can be cached and
    /// served from this box. Returns null when lookups are off, the URL is blank, or the fetch can't run — degrading,
    /// never throwing, exactly like the lookup: a missing preview is not an error, just nothing to cache.</summary>
    Task<CivitaiPreview?> DownloadPreviewAsync(string url, CancellationToken ct);
}
