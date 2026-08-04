//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface IBookmarkRepository
{
    Task<IReadOnlyList<TokenBookmark>> GetTokensAsync(long userId, CancellationToken ct);

    Task<IReadOnlyList<ImageBookmark>> GetImagesAsync(long userId, CancellationToken ct);

    Task<bool> IsImageBookmarkedAsync(long userId, string gatewayImageId, CancellationToken ct);

    /// <summary>Insert an artist/tag bookmark. Returns false if (UserId, Name, Kind) already exists.</summary>
    Task<bool> AddTokenAsync(TokenBookmark bookmark, CancellationToken ct);

    Task<bool> RemoveTokenAsync(long userId, string name, TokenKind kind, CancellationToken ct);

    /// <summary>Pin (non-null timestamp) or unpin (null) an artist/tag bookmark. Returns false if it doesn't exist.</summary>
    Task<bool> SetTokenPinnedAsync(long userId, string name, TokenKind kind, DateTime? pinnedAtUtc, CancellationToken ct);

    /// <summary>Insert an image bookmark (with its marks). Returns false if (UserId, GatewayImageId) already exists.</summary>
    Task<bool> AddImageAsync(ImageBookmark bookmark, CancellationToken ct);

    Task<bool> RemoveImageAsync(long userId, string gatewayImageId, CancellationToken ct);

    /// <summary>All distinct category names the user has used (across artist/tag and image bookmarks), name-sorted.</summary>
    Task<IReadOnlyList<string>> GetAllCategoriesAsync(long userId, CancellationToken ct);

    /// <summary>The categories a specific artist/tag bookmark is filed under (empty if it isn't bookmarked).</summary>
    Task<IReadOnlyList<string>> GetTokenCategoriesAsync(long userId, string name, TokenKind kind, CancellationToken ct);

    /// <summary>The categories a specific image bookmark is filed under (empty if it isn't bookmarked).</summary>
    Task<IReadOnlyList<string>> GetImageCategoriesAsync(long userId, string gatewayImageId, CancellationToken ct);

    /// <summary>Ensure the artist/tag bookmark exists, then replace its category set with <paramref name="categories"/>.</summary>
    Task SetTokenCategoriesAsync(TokenBookmark bookmark, IReadOnlyList<string> categories, CancellationToken ct);

    /// <summary>Ensure the image bookmark exists, then replace its category set with <paramref name="categories"/>.</summary>
    Task SetImageCategoriesAsync(ImageBookmark bookmark, IReadOnlyList<string> categories, CancellationToken ct);
}
