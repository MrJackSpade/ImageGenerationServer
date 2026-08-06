using ImageGen.Application.Models;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

/// <summary>
/// Artist pages and their display image. The display image is the user's manual pick (<see cref="IArtistDisplayRepository"/>)
/// or, failing that, their most recent generation for the artist. Everything is per-user, so one user's
/// generations and pick are never visible to another.
/// <para>
/// The latest-generation fallback is single-artist only, matching the artist grid: a blend of two styles is
/// evidence of neither, so it represents no individual artist. A MANUAL pick is deliberately not filtered —
/// the user chose that image for this artist, and overriding their choice on the user's behalf is not this
/// resolver's call. Clearing the override falls back to the filtered latest.
/// </para>
/// </summary>
public sealed class ArtistService(IArtistDisplayRepository displays, IHistoryRepository history)
{
    private readonly IArtistDisplayRepository _displays = displays;
    private readonly IHistoryRepository _history = history;

    /// <summary>The artist's display image and whether it's a manual override (vs. the latest-generation fallback).</summary>
    public async Task<ArtistDisplayResult> GetDisplayAsync(long userId, string artistName, CancellationToken ct)
    {
        ArtistDisplay? chosen = await _displays.GetAsync(userId, artistName, ct);
        if (chosen is not null)
        {
            return new ArtistDisplayResult(chosen.GatewayImageId, true);
        }

        IReadOnlyDictionary<string, string> latest = await _history.GetLatestImageIdsForArtistsAsync(userId, [artistName], ct);
        return new ArtistDisplayResult(latest.GetValueOrDefault(artistName), false);
    }

    /// <summary>Resolve a display image (override else latest generation) for many artists at once — the bookmarks grid.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveManyAsync(
        long userId, IReadOnlyCollection<string> artistNames, CancellationToken ct)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (artistNames.Count == 0)
        {
            return result;
        }

        IReadOnlyDictionary<string, string> overrides = await _displays.GetManyAsync(userId, artistNames, ct);
        IReadOnlyDictionary<string, string> latest = await _history.GetLatestImageIdsForArtistsAsync(userId, artistNames, ct);
        foreach (string name in artistNames)
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

    /// <summary>Set the user's display image for an artist. Returns false if the image isn't in the user's history.</summary>
    public async Task<bool> SetAsync(long userId, string artistName, string gatewayImageId, DateTime nowUtc, CancellationToken ct)
    {
        HistoryEntry? entry = await _history.GetByGatewayImageIdAsync(userId, gatewayImageId, ct);
        if (entry is null)
        {
            return false;
        }

        await _displays.SetAsync(new ArtistDisplay
        {
            UserId = userId,
            ArtistName = artistName,
            GatewayImageId = gatewayImageId,
            SetAtUtc = nowUtc,
        }, ct);
        return true;
    }

    /// <summary>Clear the override so the artist falls back to the user's most recent generation for it.</summary>
    public Task ClearAsync(long userId, string artistName, CancellationToken ct) =>
        _displays.DeleteAsync(userId, artistName, ct);

    /// <summary>A page of the user's generations that used this artist (newest first).</summary>
    public Task<PagedResult<HistoryEntry>> GetGensAsync(long userId, string artistName, int page, int pageSize, CancellationToken ct) =>
        _history.GetPageAsync(new HistoryQuery(userId, page, pageSize, Artist: artistName), ct);
}
