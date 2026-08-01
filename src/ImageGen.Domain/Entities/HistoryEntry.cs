namespace ImageGen.Domain.Entities;

/// <summary>
/// One generated image in a user's history. We persist metadata only — the image bytes live on
/// ForgeGateway and are referenced by <see cref="GatewayImageId"/> (the SPA renders them via
/// {gateway}/image/{id}). Unique per (UserId, GatewayImageId).
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>Database surrogate key. 0 for a not-yet-persisted entry.</summary>
    public long Id { get; init; }

    public required long UserId { get; init; }

    /// <summary>Opaque ForgeGateway image id — the dedupe key within a user's library.</summary>
    public required string GatewayImageId { get; init; }

    /// <summary>The FINALIZED prompt the model actually rendered: markers stripped, underscores folded, random
    /// injections included. This is the text, not the intent — see <see cref="RawPrompt"/>.</summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// The prompt VERBATIM as submitted, in marker form ("#long_hair, @greg_rutkowski"), with any random tags/artist the
    /// worker injected appended in that same form. This is what the user would have to type to make this image again, so
    /// it is what the copy button, Reload and the Edit page hand back — loaded as-is, never reconstructed. Finalizing it
    /// reproduces <see cref="Prompt"/> and <see cref="Marks"/> exactly, which is what makes a reload faithful.
    ///
    /// Null only for rows the worker wrote before this column existed. Those were backfilled once (from the finalized
    /// prompt + marks, the best reconstruction available); nothing rebuilds it at read time any more.
    /// </summary>
    public string? RawPrompt { get; init; }

    /// <summary>
    /// The NEGATIVE prompt verbatim as submitted, in the same marker form (the negative box shares the '#'/'@'
    /// autocomplete, so its text carries markers and underscores too). Stored for the same reason as
    /// <see cref="RawPrompt"/>: Reload has to resubmit it and the edit boxes have to seed from it, and neither can be
    /// done from a finalized string. The finalized negative is never persisted — nothing needs it.
    ///
    /// Null means NO negative was submitted, which is not the same as an empty one: null leaves the model's built-in
    /// default negative alone, and that distinction decides what the picture looks like.
    /// </summary>
    public string? RawNegativePrompt { get; init; }

    /// <summary>
    /// The prompt exactly as the user TYPED it, before anything resolved it — <c>[a|b]</c> still a choice rather than
    /// the option that was rolled, an artist page's locked artist not yet appended, and none of the worker's sampled
    /// tags or artist added. <see cref="RawPrompt"/> is post-resolution despite its name, so this is the only record
    /// of the intent as opposed to the result.
    ///
    /// Null for every image made before this column existed, and it CANNOT be backfilled: the expansion happens in
    /// the browser before the request is sent, so the original was never transmitted, let alone stored. Callers must
    /// treat null as "not recorded" and say so, rather than substituting the resolved prompt for it.
    /// </summary>
    public string? OriginalPrompt { get; init; }

    /// <summary>Friendly model name shown in the UI (e.g. "Flux 1.0 Pro").</summary>
    public required string ModelFriendly { get; init; }

    /// <summary>Catalog model id used by the gateway.</summary>
    public required string ModelId { get; init; }

    /// <summary>"square" | "landscape" | "portrait".</summary>
    public required string Aspect { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Prompt-token metadata for this image. Empty when the gateway returned none.</summary>
    public IReadOnlyList<Mark> Marks { get; init; } = [];
}
