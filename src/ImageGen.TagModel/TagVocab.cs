using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageGen.TagModel;

/// <summary>
/// The model's tag vocabulary: every tag it can be conditioned on, with its gelbooru category and corpus count.
///
/// <para>This is the single source of truth for tag data in the app. Critically it is the vocabulary the checkpoint
/// was TRAINED against, pinned to it, so the ids here cannot drift from the ids the model emits — deriving categories
/// from any separate file the training run never saw is exactly what would let them diverge.</para>
/// </summary>
public sealed class TagVocab
{
    /// <summary>Regex patterns matched against tag names.</summary>
    private static class Patterns
    {
        /// <summary>Pattern matching an HTML entity, the mark of a vocab built before the capture-layer decode.</summary>
        public const string HtmlEntityPattern = @"&(#[0-9]+|#x[0-9a-fA-F]+|amp|lt|gt|quot|apos);";
    }

    /// <summary>The vocab file's JSON property names.</summary>
    private static class Props
    {
        /// <summary>JSON key for the corpus row count.</summary>
        public const string NRowsProperty = "n_rows";

        /// <summary>JSON key for the tag-name array.</summary>
        public const string TagsProperty = "tags";

        /// <summary>JSON key for the per-tag corpus count array.</summary>
        public const string CountsProperty = "counts";

        /// <summary>JSON key for the per-tag gelbooru category array.</summary>
        public const string TypesProperty = "types";
    }

    /// <summary>Delimiters used when composing diagnostic text.</summary>
    private static class Separators
    {
        /// <summary>Separator joining sample encoded tags in the diagnostic message.</summary>
        public const string Separator = ", ";
    }

    /// <summary>Detects a tag name that is still HTML-encoded, which means the vocab was built before the capture-layer decode.</summary>
    private static readonly Regex HtmlEntity = new(Patterns.HtmlEntityPattern, RegexOptions.Compiled);

    private readonly Dictionary<string, int> _byName;

    private TagVocab(string[] tags, long[] counts, byte[] types, long rowCount)
    {
        Tags = tags;
        Counts = counts;
        Types = types;
        RowCount = rowCount;

        _byName = new Dictionary<string, int>(tags.Length, StringComparer.Ordinal);
        for (int i = 0; i < tags.Length; i++)
            _byName[tags[i]] = i;

        Lowercase = new string[tags.Length];
        for (int i = 0; i < tags.Length; i++)
            Lowercase[i] = tags[i].ToLowerInvariant();

        // Base rate per tag, clamped off {0,1} exactly as cvae/vocab.py does, so a rare tag's log-odds stays finite
        // and the "lift" a suggestion reports matches what the Python server reported for the same tag.
        Marginal = new float[tags.Length];
        double eps = 1.0 / (rowCount + 2.0);
        for (int i = 0; i < tags.Length; i++)
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
    public int? IdOf(string tag) => _byName.TryGetValue(tag, out int id) ? id : null;

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
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        long rowCount = root.GetProperty(Props.NRowsProperty).GetInt64();
        string[] tags = ReadStrings(root, Props.TagsProperty);
        long[] counts = ReadInt64s(root, Props.CountsProperty);
        byte[] types = ReadBytes(root, Props.TypesProperty);

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
        string[] encoded = tags.Where(t => HtmlEntity.IsMatch(t)).Take(3).ToArray();
        if (encoded.Length > 0)
            throw new InvalidDataException(
                $"{path}: tag names are still HTML-encoded (e.g. {string.Join(Separators.Separator, encoded)}). This vocab predates "
                + "the capture-layer decode; decode it once at rest rather than at load.");

        return new TagVocab(tags, counts, types, rowCount);
    }

    private static string[] ReadStrings(JsonElement root, string name)
    {
        JsonElement array = root.GetProperty(name);
        string[] result = new string[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement element in array.EnumerateArray())
            result[i++] = element.GetString()
                ?? throw new JsonException($"Vocab array '{name}' contains a non-string element.");
        return result;
    }

    private static long[] ReadInt64s(JsonElement root, string name)
    {
        JsonElement array = root.GetProperty(name);
        long[] result = new long[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement element in array.EnumerateArray())
            result[i++] = element.GetInt64();
        return result;
    }

    private static byte[] ReadBytes(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement array))
            return [];
        byte[] result = new byte[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement element in array.EnumerateArray())
            result[i++] = (byte)element.GetInt32();
        return result;
    }
}
