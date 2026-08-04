namespace ImageGen.Domain.Entities;

/// <summary>
/// A tag or artist the user has banned for a specific model. Banned tokens are excluded from auto-gen
/// (random prompt/artist) for that model only — they still condition the tag model's inference and a
/// manual generate is untouched. The name is canonical (lowercase, underscored), matching the gateway's
/// marks map. Unique per (UserId, ModelId, Name, Kind).
/// </summary>
public sealed class BannedToken
{
    public long Id { get; init; }

    public required long UserId { get; init; }

    public required string ModelId { get; init; }

    public required string Name { get; init; }

    public required TokenKind Kind { get; init; }

    public required DateTime SavedAtUtc { get; init; }
}
