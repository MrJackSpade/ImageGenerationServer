//TODO: CHECK FOR FALLBACKS
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageGen.TagModel;

/// <summary>
/// The model's tag vocabulary: every tag it can be conditioned on, with its gelbooru category and corpus count.
///
/// <para>This is the single source of truth for tag data in the app. It replaces <c>tags.json</c> (a 54 MB file
/// derived from a gelbooru dump by a separate script) and is a strict superset of it — same names, same counts, same
/// categories, plus every tag the model knows that the old file omitted. Critically it is the vocabulary the
/// checkpoint was TRAINED against, pinned to it, so the ids here cannot drift from the ids the model emits. The old
/// arrangement re-derived categories from a file the training run never saw, and it did diverge.</para>
/// </summary>
public sealed class TagVocab
{
    /// <summary>Detects a tag name that is still HTML-encoded, which means the vocab was built before the capture-layer decode.</summary>
    private static readonly Regex HtmlEntity = new(@"&(#[0-9]+|#x[0-9a-fA-F]+|amp|lt|gt|quot|apos);", RegexOptions.Compiled);

    private readonly Dictionary<string, int> _byName;

    private TagVocab(string[] tags, long[] counts, byte[] types, long rowCount)
    {
        Tags = tags;
        Counts = counts;
        Types = types;
        RowCount = rowCount;

        _byName = new Dictionary<string, int>(tags.Length, StringComparer.Ordinal);
        for (var i = 0; i < tags.Length; i++)
            _byName[tags[i]] = i;

        Lowercase = new string[tags.Length];
        for (var i = 0; i < tags.Length; i++)
            Lowercase[i] = tags[i].ToLowerInvariant();

        // Base rate per tag, clamped off {0,1} exactly as cvae/vocab.py does, so a rare tag's log-odds stays finite
        // and the "lift" a suggestion reports matches what the Python server reported for the same tag.
        Marginal = new float[tags.Length];
        var eps = 1.0 / (rowCount + 2.0);
        for (var i = 0; i < tags.Length; i++)
            Marginal[i] = (float)Math.Clamp(counts[i] / (double)rowCount, eps, 1.0 - eps);
    }

    /// <summary>Tag name by vocab id.</summary>
    public string[] Tags { get; }

    /// <summary>Lowercased names, for the substring scan autocomplete does on every keystroke.</summary>
    public string[] Lowercase { get; }

    /// <summary>Corpus occurrence count by vocab id.</summary>
    public long[] Counts { get; }

    /// <summary>Gelbooru category by vocab id (0 general, 1 artist, 3 copyright, 4 character, 5 meta).</summary>
    public byte[] Types { get; }

    /// <summary>Corpus size the counts were measured over — the denominator behind <see cref="Marginal"/>.</summary>
    public long RowCount { get; }

    /// <summary>P(tag) across the corpus, clamped off 0 and 1.</summary>
    public float[] Marginal { get; }

    /// <summary>Number of tags.</summary>
    public int Count => Tags.Length;

    /// <summary>Vocab id for an exact tag name, or null when the vocabulary has no such tag.</summary>
    public int? IdOf(string tag) => _byName.TryGetValue(tag, out var id) ? id : null;

    /// <summary>True when this tag is a gelbooru artist tag — what decides '@' versus '#' for a sampled name.</summary>
    public bool IsArtist(int id) => Types[id] == TypeMask.CategoryArtist;

    /// <summary>
    /// Read <c>vocab_s2srec2.json</c>: <c>{ n_rows, tags[], counts[], types[] }</c>.
    ///
    /// <para>Streamed rather than deserialized into objects: the file is ~16 MB of three parallel arrays, and
    /// materialising it as a JSON document first costs several times that in transient allocation for no gain.</para>
    /// </summary>
    public static TagVocab Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var rowCount = root.GetProperty("n_rows").GetInt64();
        var tags = ReadStrings(root, "tags");
        var counts = ReadInt64s(root, "counts");
        var types = ReadBytes(root, "types");

        if (counts.Length != tags.Length)
            throw new InvalidDataException($"{path}: {counts.Length} counts for {tags.Length} tags.");
        if (types.Length != tags.Length)
            throw new InvalidDataException(
                $"{path}: {types.Length} types for {tags.Length} tags. A vocab without an id-aligned type array "
                + "cannot tell an artist from a subject, so suppressing a category would be a silent no-op.");
        if (rowCount <= 0)
            throw new InvalidDataException($"{path}: n_rows is {rowCount}; base rates would be meaningless.");

        // Refuse an HTML-encoded vocab instead of compensating for it. Decoding here cannot be made safe: it is not
        // idempotent, so it would corrupt an already-decoded vocab ('&ether' becomes 'ðer'). A literal '&' is normal
        // in real tags ('tiger_&_bunny'); an ENTITY never is, so this is a build mistake and has to fail loudly.
        var encoded = tags.Where(t => HtmlEntity.IsMatch(t)).Take(3).ToArray();
        if (encoded.Length > 0)
            throw new InvalidDataException(
                $"{path}: tag names are still HTML-encoded (e.g. {string.Join(", ", encoded)}). This vocab predates "
                + "the capture-layer decode; decode it once at rest rather than at load.");

        return new TagVocab(tags, counts, types, rowCount);
    }

    private static string[] ReadStrings(JsonElement root, string name)
    {
        var array = root.GetProperty(name);
        var result = new string[array.GetArrayLength()];
        var i = 0;
        foreach (var element in array.EnumerateArray())
            result[i++] = element.GetString()!;
        return result;
    }

    private static long[] ReadInt64s(JsonElement root, string name)
    {
        var array = root.GetProperty(name);
        var result = new long[array.GetArrayLength()];
        var i = 0;
        foreach (var element in array.EnumerateArray())
            result[i++] = element.GetInt64();
        return result;
    }

    private static byte[] ReadBytes(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array))
            return [];
        var result = new byte[array.GetArrayLength()];
        var i = 0;
        foreach (var element in array.EnumerateArray())
            result[i++] = (byte)element.GetInt32();
        return result;
    }
}
