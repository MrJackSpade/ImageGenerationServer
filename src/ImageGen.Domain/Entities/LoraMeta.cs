namespace ImageGen.Domain.Entities;

/// <summary>
/// Machine-level cache of what CivitAI knows about a LoRA file, looked up by its hash. Not per-user — it's a property
/// of the file on this box. <see cref="TrainedWords"/> are the LoRA's activation words (may be empty for a
/// "no trigger" LoRA); <see cref="PreviewUrl"/> is a representative CivitAI image used as a default cover.
/// </summary>
public sealed record LoraMeta(
    string LoraName,
    string? Sha256,
    IReadOnlyList<string> TrainedWords,
    string? ModelName,
    string? PreviewUrl,
    DateTime FetchedAtUtc);
