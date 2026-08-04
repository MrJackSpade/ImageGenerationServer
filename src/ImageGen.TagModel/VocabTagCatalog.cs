//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Tags;

namespace ImageGen.TagModel;

/// <summary>
/// <see cref="ITagCatalog"/> over the model's own vocabulary, replacing the <c>TagStore</c> that read <c>tags.json</c>.
///
/// <para>Two vocabularies of the same data used to be loaded in two processes: a 54 MB <c>tags.json</c>, built from a
/// gelbooru dump by a separate script, in the app; and the checkpoint's own <c>vocab_s2srec2.json</c> in Python. The
/// model's is a strict superset — same names, counts and categories, plus every tag the old file omitted — and it is
/// PINNED to the checkpoint, so the categories cannot drift from the ones the model was trained on. The old file could
/// drift, and did.</para>
///
/// <para>Consequently <c>tags.json</c>, <c>tags.example.json</c>, <c>build-tags-json.py</c> and the <c>_tags.dat</c>
/// dump they depended on are all gone.</para>
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

        var tags = new List<int>();
        var artists = new List<int>();
        for (var id = 0; id < vocab.Count; id++)
            (vocab.IsArtist(id) ? artists : tags).Add(id);

        _tagsByCount = [.. tags.OrderByDescending(id => vocab.Counts[id])];
        _artistsByCount = [.. artists.OrderByDescending(id => vocab.Counts[id])];

        _byName = new Dictionary<string, int>(vocab.Count, StringComparer.OrdinalIgnoreCase);
        for (var id = 0; id < vocab.Count; id++)
            _byName.TryAdd(vocab.Tags[id], id);

        _artistCumulative = new long[_artistsByCount.Length];
        long running = 0;
        for (var i = 0; i < _artistsByCount.Length; i++)
        {
            // Floor of 1: an artist with a zero corpus count would otherwise be unreachable, which reads as the
            // catalog quietly having fewer artists than it reports.
            running += Math.Max(1, vocab.Counts[_artistsByCount[i]]);
            _artistCumulative[i] = running;
        }
    }

    /// <summary>
    /// Always true. The old store loaded a large file in a background task, so callers had to cope with "not ready
    /// yet"; the vocabulary is loaded before the app serves its first request, so there is no such window.
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
        if (limit < 1) return [];
        var needle = (query ?? "").Trim();

        // Pre-sorted by count, so the first `limit` substring matches ARE the top `limit` by count -- no scoring pass
        // over ~639k entries per keystroke.
        var source = artist ? _artistsByCount : _tagsByCount;
        var results = new List<TagEntry>(Math.Min(limit, 32));
        foreach (var id in source)
        {
            if (needle.Length > 0 &&
                !_vocab.Tags[id].Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(Entry(id));
            if (results.Count >= limit) break;
        }
        return results;
    }

    /// <inheritdoc />
    public TagEntry? Lookup(string name) =>
        name is not null && _byName.TryGetValue(name, out var id) ? Entry(id) : null;

    /// <inheritdoc />
    public bool IsArtist(string name) =>
        name is not null && _byName.TryGetValue(name, out var id) && _vocab.IsArtist(id);

    /// <inheritdoc />
    public string? RandomArtist(IReadOnlySet<string>? exclude)
    {
        if (_artistsByCount.Length == 0) return null;

        var total = _artistCumulative[^1];
        // Bounded retries on the weighted draw: with a handful of exclusions against ~294k artists a redraw almost
        // always lands immediately, and this avoids rebuilding the cumulative table per call.
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var target = Random.Shared.NextInt64(total);
            var index = UpperBound(_artistCumulative, target);
            var name = _vocab.Tags[_artistsByCount[index]];
            if (exclude is null || !exclude.Contains(name))
                return name;
        }

        // The draw kept hitting excluded artists, so fall back to an exact weighted pick over what is left. Reached
        // only when the exclusion set covers most of the corpus weight -- a user who has banned the popular artists.
        var eligible = _artistsByCount.Where(id => exclude is null || !exclude.Contains(_vocab.Tags[id])).ToArray();
        if (eligible.Length == 0) return null;

        long remaining = 0;
        foreach (var id in eligible) remaining += Math.Max(1, _vocab.Counts[id]);
        var pick = Random.Shared.NextInt64(remaining);
        foreach (var id in eligible)
        {
            pick -= Math.Max(1, _vocab.Counts[id]);
            if (pick < 0) return _vocab.Tags[id];
        }
        return _vocab.Tags[eligible[^1]];
    }

    private TagEntry Entry(int id) =>
        new(_vocab.Tags[id], (int)Math.Min(int.MaxValue, _vocab.Counts[id]), _vocab.Types[id]);

    /// <summary>First index whose cumulative total exceeds <paramref name="target"/>.</summary>
    private static int UpperBound(long[] cumulative, long target)
    {
        var lo = 0;
        var hi = cumulative.Length - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (cumulative[mid] > target) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}
