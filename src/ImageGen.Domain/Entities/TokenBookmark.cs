using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Domain.Entities;

/// <summary>
/// A starred artist or tag. The name is canonical (lowercase, underscored), matching the gateway's
/// marks map. Unique per (UserId, Name, Kind).
/// </summary>
public sealed class TokenBookmark
{
    public long Id { get; init; }

    public required long UserId { get; init; }

    public required string Name { get; init; }

    public required TokenKind Kind { get; init; }

    public required DateTime SavedAtUtc { get; init; }

    /// <summary>When the user pinned this bookmark to the top of the bookmarks page, or null if unpinned.</summary>
    [AllowNullable("null = unpinned; mirrors the nullable dbo.TokenBookmark column. There is no default timestamp that means \"not pinned\"")]
    public DateTime? PinnedAtUtc { get; init; }

    /// <summary>The named categories this bookmark is filed under (empty = the "Global"/uncategorized bucket).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];
}
