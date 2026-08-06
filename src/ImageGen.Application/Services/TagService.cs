using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

/// <summary>
/// Per-user tag portrait images — the picture that represents a tag on the bookmarks page. A portrait is the user's
/// manual pick (<see cref="ITagDisplayRepository"/>) or, failing that, their most recent generation carrying the tag;
/// a tag with neither shows a placeholder. Mirrors the display-image half of <see cref="ArtistService"/>, but the
/// latest-generation fallback is NOT single-tag: tags are additive descriptors, so an image legitimately carries many
/// at once and any of them may claim it as its latest.
/// </summary>
public sealed class TagService(ITagDisplayRepository displays, IHistoryRepository history)
{
    private readonly ITagDisplayRepository _displays = displays;
    private readonly IHistoryRepository _history = history;

    /// <summary>Resolve a display image (manual pick else latest generation) for many tags at once — the bookmarks grid.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveManyAsync(
        long userId, IReadOnlyCollection<string> tagNames, CancellationToken ct)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (tagNames.Count == 0)
        {
            return result;
        }

        IReadOnlyDictionary<string, string> overrides = await _displays.GetManyAsync(userId, tagNames, ct);
        IReadOnlyDictionary<string, string> latest = await _history.GetLatestImageIdsForTagsAsync(userId, tagNames, ct);
        foreach (string name in tagNames)
        {
            if (overrides.TryGetValue(name, out string? ov))
            {
                result[name] = ov;
            }
            else if (latest.TryGetValue(name, out string? l))
            {
                result[name] = l;
            }
        }

        return result;
    }

    /// <summary>Set the user's portrait image for a tag. Returns false if the image isn't in the user's history.</summary>
    public async Task<bool> SetAsync(long userId, string tagName, string gatewayImageId, DateTime nowUtc, CancellationToken ct)
    {
        HistoryEntry? entry = await _history.GetByGatewayImageIdAsync(userId, gatewayImageId, ct);
        if (entry is null)
        {
            return false;
        }

        await _displays.SetAsync(new TagDisplay
        {
            UserId = userId,
            TagName = tagName,
            GatewayImageId = gatewayImageId,
            SetAtUtc = nowUtc,
        }, ct);
        return true;
    }

    /// <summary>Clear the manual pick so the tag falls back to the user's most recent generation carrying it.</summary>
    public Task ClearAsync(long userId, string tagName, CancellationToken ct) =>
        _displays.DeleteAsync(userId, tagName, ct);
}
