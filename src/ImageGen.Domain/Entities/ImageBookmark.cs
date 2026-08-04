//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// A starred image. Kept as a self-contained copy (not a foreign key to history) so a bookmarked
/// image survives deletion from history — faithful to the SPA's behavior. Unique per
/// (UserId, GatewayImageId).
/// </summary>
public sealed class ImageBookmark
{
    public long Id { get; init; }

    public required long UserId { get; init; }

    public required string GatewayImageId { get; init; }

    public required string Prompt { get; init; }

    public required string ModelFriendly { get; init; }

    public required string ModelId { get; init; }

    public required string Aspect { get; init; }

    /// <summary>When the image was originally generated (the source history entry's timestamp).</summary>
    public required DateTime OriginalCreatedAtUtc { get; init; }

    /// <summary>When the user bookmarked it.</summary>
    public required DateTime SavedAtUtc { get; init; }

    public IReadOnlyList<Mark> Marks { get; init; } = [];

    /// <summary>The named categories this bookmark is filed under (empty = the "Global"/uncategorized bucket).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];
}
