using ImageGen.Application.Models;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

public sealed class BookmarkService(IBookmarkRepository bookmarks, TimeProvider clock)
{
    private readonly IBookmarkRepository _bookmarks = bookmarks;
    private readonly TimeProvider _clock = clock;

    public Task<IReadOnlyList<TokenBookmark>> GetTokensAsync(long userId, CancellationToken ct) =>
        _bookmarks.GetTokensAsync(userId, ct);

    public Task<IReadOnlyList<ImageBookmark>> GetImagesAsync(long userId, CancellationToken ct) =>
        _bookmarks.GetImagesAsync(userId, ct);

    public Task<bool> IsImageBookmarkedAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _bookmarks.IsImageBookmarkedAsync(userId, gatewayImageId, ct);

    public Task<bool> AddTokenAsync(long userId, string name, TokenKind kind, CancellationToken ct) =>
        _bookmarks.AddTokenAsync(
            new TokenBookmark { UserId = userId, Name = name, Kind = kind, SavedAtUtc = Now() }, ct);

    public Task<bool> RemoveTokenAsync(long userId, string name, TokenKind kind, CancellationToken ct) =>
        _bookmarks.RemoveTokenAsync(userId, name, kind, ct);

    public Task<bool> SetTokenPinnedAsync(long userId, string name, TokenKind kind, bool pinned, CancellationToken ct) =>
        _bookmarks.SetTokenPinnedAsync(userId, name, kind, pinned ? Now() : null, ct);

    public Task<bool> AddImageAsync(AddImageBookmarkCommand command, CancellationToken ct) =>
        _bookmarks.AddImageAsync(command.ToEntity(Now()), ct);

    public Task<bool> RemoveImageAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _bookmarks.RemoveImageAsync(userId, gatewayImageId, ct);

    public Task<IReadOnlyList<string>> GetAllCategoriesAsync(long userId, CancellationToken ct) =>
        _bookmarks.GetAllCategoriesAsync(userId, ct);

    public Task<IReadOnlyList<string>> GetTokenCategoriesAsync(long userId, string name, TokenKind kind, CancellationToken ct) =>
        _bookmarks.GetTokenCategoriesAsync(userId, name, kind, ct);

    public Task<IReadOnlyList<string>> GetImageCategoriesAsync(long userId, string gatewayImageId, CancellationToken ct) =>
        _bookmarks.GetImageCategoriesAsync(userId, gatewayImageId, ct);

    public Task SetTokenCategoriesAsync(
        long userId, string name, TokenKind kind, IReadOnlyList<string> categories, CancellationToken ct) =>
        _bookmarks.SetTokenCategoriesAsync(
            new TokenBookmark { UserId = userId, Name = name, Kind = kind, SavedAtUtc = Now() }, categories, ct);

    public Task SetImageCategoriesAsync(
        AddImageBookmarkCommand command, IReadOnlyList<string> categories, CancellationToken ct) =>
        _bookmarks.SetImageCategoriesAsync(command.ToEntity(Now()), categories, ct);

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
