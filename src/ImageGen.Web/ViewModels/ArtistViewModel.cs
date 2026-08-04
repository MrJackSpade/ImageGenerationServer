namespace ImageGen.Web.ViewModels;

public sealed class ArtistViewModel
{
    public required string Name { get; init; }

    /// <summary>The gateway image id to show for this artist (override or latest gen), or null if no generations.</summary>
    public required string? DisplayImageId { get; init; }

    /// <summary>True when <see cref="DisplayImageId"/> is the user's manual pick rather than the latest-gen fallback.</summary>
    public required bool HasOverride { get; init; }

    public required IReadOnlyList<HistoryItemView> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}
