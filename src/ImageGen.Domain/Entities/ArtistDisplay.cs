//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// A user's manually chosen display image for an artist — what represents that artist on the bookmarks
/// page and the artist page. Per-user (a <see cref="GatewayImageId"/> from the user's own history), so one
/// user never sees another's pick. When absent, the artist falls back to the user's most recent generation
/// for that artist. Unique per (UserId, ArtistName); ArtistName is the canonical token (lowercase, underscored).
/// </summary>
public sealed class ArtistDisplay
{
    public long Id { get; init; }
    public required long UserId { get; init; }
    public required string ArtistName { get; init; }
    public required string GatewayImageId { get; init; }
    public required DateTime SetAtUtc { get; init; }
}
