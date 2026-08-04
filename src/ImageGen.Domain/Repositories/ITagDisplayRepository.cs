//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface ITagDisplayRepository
{
    /// <summary>The user's chosen portrait image for a tag, or null if they haven't set one.</summary>
    Task<TagDisplay?> GetAsync(long userId, string tagName, CancellationToken ct);

    /// <summary>The chosen portrait (gateway image id) per tag for the given names — only those set.</summary>
    Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        long userId, IReadOnlyCollection<string> tagNames, CancellationToken ct);

    /// <summary>Set (or replace) the user's portrait image for a tag.</summary>
    Task SetAsync(TagDisplay display, CancellationToken ct);

    /// <summary>Clear the user's portrait image for a tag.</summary>
    Task DeleteAsync(long userId, string tagName, CancellationToken ct);
}
