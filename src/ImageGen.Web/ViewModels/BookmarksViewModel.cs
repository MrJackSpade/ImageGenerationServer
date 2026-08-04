namespace ImageGen.Web.ViewModels;

public sealed class BookmarksViewModel
{
    /// <summary>The bookmark groups in render order: the "Global" (uncategorized) group first, then one per category
    /// (name-sorted). A bookmark filed under several categories appears in each of those groups.</summary>
    public required IReadOnlyList<BookmarkGroup> Groups { get; init; }
}

/// <summary>One rendered section on the bookmarks page — either the Global bucket or a single named category.</summary>
public sealed record BookmarkGroup
{
    /// <summary>The heading ("Global" or the category name).</summary>
    public required string Title { get; init; }
    public required bool IsGlobal { get; init; }
    /// <summary>Artists in this group, pinned ones first (most recently pinned first), then the rest in saved order.</summary>
    public required IReadOnlyList<ArtistCard> Artists { get; init; }
    public required IReadOnlyList<TagCard> Tags { get; init; }
    public required IReadOnlyList<ImageBookmarkView> Images { get; init; }
}

/// <summary>A bookmarked tag: its name, the booru category slug that colors its border (null = neutral), and the user's
/// resolved portrait image (null when they've set none). A portrait for parity with <see cref="ArtistCard"/> — the tag
/// is still clicked to filter by it, and still carries the star/remove/categorise controls.</summary>
public sealed record TagCard(string Name, string? Category, string? DisplayImageId);

/// <summary>A bookmarked artist with its resolved display image (null when the user has no generation for it).</summary>
public sealed record ArtistCard
{
    public required string Name { get; init; }
    public required string? DisplayImageId { get; init; }
    public bool Pinned { get; init; }
}

public sealed class BookmarkFilterViewModel
{
    public required string Token { get; init; }
    /// <summary>"artist" | "tag".</summary>
    public required string Kind { get; init; }
    public required IReadOnlyList<HistoryItemView> Items { get; init; }
}
