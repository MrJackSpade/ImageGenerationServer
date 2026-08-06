using ImageGen.Application.Tags;
using ImageGen.Domain;

namespace ImageGen.TagModel;

/// <summary>
/// <see cref="ITagCatalog"/> over the model's own vocabulary.
///
/// <para>The vocabulary is PINNED to the checkpoint — the same names, counts and categories the model was trained on
/// — so the categories cannot drift from the ones the model emits. A tag file derived independently, from a gelbooru
/// dump by a separate script, could drift.</para>
/// </summary>
public sealed class VocabTagCatalog : ITagCatalog
{
    private readonly TagVocab _vocab;

    /// <summary>Ids of standard (non-artist) tags, count-descending — the '#' autocomplete's ranking order.</summary>
    private readonly int[] _tagsByCount;

    /// <summary>Ids of artist tags, count-descending — the '@' autocomplete's ranking order.</summary>
    private readonly int[] _artistsByCount;

    /// <summary>Case-insensitive name → id, for <see cref="Lookup"/> and <see cref="IsArtist"/>.</summary>
    private readonly Dictionary<string, int> _byName;

    /// <summary>
    /// Cumulative counts over <see cref="_artistsByCount"/>, so a weighted random artist is one binary search rather
    /// than a scan. Long, because ~294k artist counts summed overflow an int.
    /// </summary>
    private readonly long[] _artistCumulative;

    /// <summary>Build the catalog from a loaded vocabulary.</summary>
    public VocabTagCatalog(TagVocab vocab)
    {
        _vocab = vocab;

        List<int> tags = [];
        List<int> artists = [];
        for (int id = 0; id < vocab.Count; id++)
        {
            (vocab.IsArtist(id) ? artists : tags).Add(id);
        }

        _tagsByCount = [.. tags.OrderByDescending(id => vocab.Counts[id])];
        _artistsByCount = [.. artists.OrderByDescending(id => vocab.Counts[id])];

        _byName = new Dictionary<string, int>(vocab.Count, StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < vocab.Count; id++)
        {
            _ = _byName.TryAdd(vocab.Tags[id], id);
        }

        _artistCumulative = new long[_artistsByCount.Length];
        long running = 0;
        for (int i = 0; i < _artistsByCount.Length; i++)
        {
            // Floor of 1: an artist with a zero corpus count would otherwise be unreachable, which reads as the
            // catalog quietly having fewer artists than it reports.
            running += Math.Max(1, vocab.Counts[_artistsByCount[i]]);
            _artistCumulative[i] = running;
        }
    }

    /// <summary>
    /// Always true. The vocabulary is loaded before the app serves its first request, so there is no "not ready yet"
    /// window for callers to cope with.
    /// </summary>
    public bool Loaded => true;

    /// <inheritdoc />
    public string Status => $"loaded {TagCount:N0} tags + {ArtistCount:N0} artists from the model vocabulary";

    /// <inheritdoc />
    public int TagCount => _tagsByCount.Length;

    /// <inheritdoc />
    public int ArtistCount => _artistsByCount.Length;

    /// <inheritdoc />
    public IReadOnlyList<TagEntry> Query(string query, bool artist, int limit)
    {
        _ = Ensure.GreaterThanZero(limit);   // an empty ask is the caller's mistake to see, not a silent [] (as the model path also refuses)
        string needle = query.Trim();

        // Pre-sorted by count, so the first `limit` substring matches ARE the top `limit` by count -- no scoring pass
        // over ~639k entries per keystroke.
        int[] source = artist ? _artistsByCount : _tagsByCount;
        List<TagEntry> results = new(Math.Min(limit, 32));
        foreach (int id in source)
        {
            if (needle.Length > 0 &&
                !_vocab.Tags[id].Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(Entry(id));
            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public TagEntry? Lookup(string name) =>
        name is not null && _byName.TryGetValue(name, out int id) ? Entry(id) : null;

    /// <inheritdoc />
    public bool IsArtist(string name) =>
        name is not null && _byName.TryGetValue(name, out int id) && _vocab.IsArtist(id);

    /// <inheritdoc />
    public string? RandomArtist(IReadOnlySet<string>? exclude)
    {
        if (_artistsByCount.Length == 0)
        {
            return null;
        }

        long total = _artistCumulative[^1];
        // Bounded retries on the weighted draw: with a handful of exclusions against ~294k artists a redraw almost
        // always lands immediately, and this avoids rebuilding the cumulative table per call.
        for (int attempt = 0; attempt < 24; attempt++)
        {
            long target = Random.Shared.NextInt64(total);
            int index = UpperBound(_artistCumulative, target);
            string name = _vocab.Tags[_artistsByCount[index]];
            if (exclude is null || !exclude.Contains(name))
            {
                return name;
            }
        }

        // The draw kept hitting excluded artists, so fall back to an exact weighted pick over what is left. Reached
        // only when the exclusion set covers most of the corpus weight -- a user who has banned the popular artists.
        int[] eligible = [.. _artistsByCount.Where(id => exclude is null || !exclude.Contains(_vocab.Tags[id]))];
        if (eligible.Length == 0)
        {
            return null;
        }

        long remaining = 0;
        foreach (int id in eligible)
        {
            remaining += Math.Max(1, _vocab.Counts[id]);
        }

        long pick = Random.Shared.NextInt64(remaining);
        foreach (int id in eligible)
        {
            pick -= Math.Max(1, _vocab.Counts[id]);
            if (pick < 0)
            {
                return _vocab.Tags[id];
            }
        }

        return _vocab.Tags[eligible[^1]];
    }

    private TagEntry Entry(int id) =>
        new(_vocab.Tags[id], (int)Math.Min(int.MaxValue, _vocab.Counts[id]), _vocab.Types[id]);

    /// <summary>First index whose cumulative total exceeds <paramref name="target"/>.</summary>
    private static int UpperBound(long[] cumulative, long target)
    {
        int lo = 0;
        int hi = cumulative.Length - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (cumulative[mid] > target)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}