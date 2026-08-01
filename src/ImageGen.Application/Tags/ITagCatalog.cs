namespace ImageGen.Application.Tags;

/// <summary>
/// The in-memory booru tag/artist store behind the SPA's '#'/'@' autocomplete and the per-job random-artist pick.
/// Implemented by an adapter that loads the (large) tag data file; the application depends only on this port.
/// </summary>
public interface ITagCatalog
{
    /// <summary>Whether the store has finished loading its data file.</summary>
    bool Loaded { get; }

    /// <summary>Human-readable load status (for diagnostics), e.g. "loading" or "loaded N tags + M artists".</summary>
    string Status { get; }

    /// <summary>Number of standard tags loaded.</summary>
    int TagCount { get; }

    /// <summary>Number of artists loaded.</summary>
    int ArtistCount { get; }

    /// <summary>Top <paramref name="limit"/> entries whose name contains <paramref name="query"/> (case-insensitive),
    /// ranked by count. <paramref name="artist"/> selects the artist set vs the standard-tag set.</summary>
    IReadOnlyList<TagEntry> Query(string query, bool artist, int limit);

    /// <summary>Exact (case-insensitive) lookup of a tag/artist entry by name, or null — used to decorate a
    /// model-ranked name with its real category/count.</summary>
    TagEntry? Lookup(string name);

    /// <summary>
    /// Whether <paramref name="name"/> is a known ARTIST. <see cref="TagEntry.Type"/> alone cannot answer this: which
    /// category id means "artist" is the store's own configuration (the data file's artist category), so the decision
    /// belongs here rather than in a caller comparing against a hardcoded id. Callers attributing a bare name — the
    /// random-prompt sampler marking what the tag model returned — ask this to pick the token's marker.
    /// False for a name the catalog does not know, including while it is still loading, exactly as
    /// <see cref="Lookup"/> returns null: unknown is not artist.
    /// </summary>
    bool IsArtist(string name);

    /// <summary>A random artist name (underscores intact, no marker), weighted by usage count, never one whose
    /// canonical name is in <paramref name="exclude"/>; null if there are no artists (or all are excluded).</summary>
    string? RandomArtist(IReadOnlySet<string>? exclude);
}
