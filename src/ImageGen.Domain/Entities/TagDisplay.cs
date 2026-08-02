namespace ImageGen.Domain.Entities;

/// <summary>
/// A user's manually chosen portrait image for a tag — mirrors <see cref="ArtistDisplay"/>. Per-user (a
/// <see cref="GatewayImageId"/> from the user's own history); unique per (UserId, TagName). TagName is the canonical
/// token (lowercase, underscored). When absent, the tag shows a placeholder rather than a portrait.
/// </summary>
public sealed class TagDisplay
{
    public long Id { get; init; }
    public required long UserId { get; init; }
    public required string TagName { get; init; }
    public required string GatewayImageId { get; init; }
    public required DateTime SetAtUtc { get; init; }
}
