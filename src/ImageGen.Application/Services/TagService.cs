using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

/// <summary>
/// Per-user tag portrait images — the picture that represents a tag on the bookmarks page. A portrait is the user's
/// manual pick (<see cref="ITagDisplayRepository"/>), one of their own generations. Mirrors the display-image half of
/// <see cref="ArtistService"/>; there's no latest-generation fallback (a tag without a set portrait shows a placeholder).
/// </summary>
public sealed class TagService(ITagDisplayRepository displays, IHistoryRepository history)
{
    private readonly ITagDisplayRepository _displays = displays;
    private readonly IHistoryRepository _history = history;

    /// <summary>The portrait image ids the user has set for the given tag names — only those with a pick.</summary>
    public Task<IReadOnlyDictionary<string, string>> ResolveManyAsync(
        long userId, IReadOnlyCollection<string> tagNames, CancellationToken ct) =>
        _displays.GetManyAsync(userId, tagNames, ct);

    /// <summary>Set the user's portrait image for a tag. Returns false if the image isn't in the user's history.</summary>
    public async Task<bool> SetAsync(long userId, string tagName, string gatewayImageId, DateTime nowUtc, CancellationToken ct)
    {
        var entry = await _history.GetByGatewayImageIdAsync(userId, gatewayImageId, ct);
        if (entry is null)
            return false;

        await _displays.SetAsync(new TagDisplay
        {
            UserId = userId,
            TagName = tagName,
            GatewayImageId = gatewayImageId,
            SetAtUtc = nowUtc,
        }, ct);
        return true;
    }

    /// <summary>Clear the user's portrait image for a tag.</summary>
    public Task ClearAsync(long userId, string tagName, CancellationToken ct) =>
        _displays.DeleteAsync(userId, tagName, ct);
}
