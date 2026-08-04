//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface IArtistDisplayRepository
{
    /// <summary>The user's chosen display image for an artist, or null if they haven't set one.</summary>
    Task<ArtistDisplay?> GetAsync(long userId, string artistName, CancellationToken ct);

    /// <summary>The chosen display image (gateway image id) per artist for the given names — only those set.</summary>
    Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        long userId, IReadOnlyCollection<string> artistNames, CancellationToken ct);

    /// <summary>Set (or replace) the user's display image for an artist.</summary>
    Task SetAsync(ArtistDisplay display, CancellationToken ct);

    /// <summary>Clear the override so the artist falls back to the user's most recent generation for it.</summary>
    Task DeleteAsync(long userId, string artistName, CancellationToken ct);
}
