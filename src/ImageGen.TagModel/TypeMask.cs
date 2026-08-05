namespace ImageGen.TagModel;

/// <summary>
/// Which tag categories a generation may contain, as a bitmask. The numbers are load-bearing:
/// <b>the mask int IS the row index into the model's conditioning embedding</b>, so it is
/// not an app-side convention that could be renumbered. Bit <c>c</c> means "category c is allowed", using gelbooru's
/// own category numbers so there is no remap table to get wrong. Bit 2 is simply never set — gelbooru does not use it.
/// </summary>
public static class TypeMask
{
    /// <summary>Ordinary visual tags.</summary>
    public const int CategoryGeneral = 0;

    /// <summary>Artist names. ~46% of the vocabulary.</summary>
    public const int CategoryArtist = 1;

    /// <summary>Franchise/series titles.</summary>
    public const int CategoryCopyright = 3;

    /// <summary>Named characters.</summary>
    public const int CategoryCharacter = 4;

    /// <summary>Metadata-ish tags that still carry visual meaning (highres, traditional_media).</summary>
    public const int CategoryMeta = 5;

    /// <summary>Merged into <see cref="CategoryGeneral"/> at the type join, so no tag carries it.</summary>
    public const int CategoryDeprecated = 6;

    /// <summary>Reserved for tags with no dump row. No tag carries it — such tags are excluded at vocab build.</summary>
    public const int CategoryUnknown = 7;

    /// <summary>Categories 0..7; bit 2 unused.</summary>
    public const int CategoryCount = 8;

    /// <summary>Every category allowed. The honest unrestricted condition, used for scoring.</summary>
    public const int AllTypes = (1 << CategoryCount) - 1;

    /// <summary>
    /// The categories that may be switched off. Every category that actually has members — which is what the
    /// checkpoint was conditioned to drop, so any other mask is a conditioning-embedding row it never saw.
    /// </summary>
    public static readonly int[] Droppable =
        [CategoryGeneral, CategoryArtist, CategoryCopyright, CategoryCharacter, CategoryMeta];

    /// <summary>Names as the wire protocol spells them, for the caller's <c>types</c> list.</summary>
    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [CategoryGeneral] = "general",
        [CategoryArtist] = "artist",
        [CategoryCopyright] = "copyright",
        [CategoryCharacter] = "character",
        [CategoryMeta] = "meta",
        [CategoryDeprecated] = "deprecated",
        [CategoryUnknown] = "unknown",
    };

    /// <summary>
    /// The standing default for generation: everything except artists.
    ///
    /// <para>An artist is a style, not a subject, and the caller chooses one separately. Nearly half this vocabulary
    /// is artist names and the sampler is perfectly happy to emit one, which would arrive in the user's prompt as a
    /// '#tag' — an artist they never asked for, wearing a subject tag's clothes.</para>
    /// </summary>
    public const int NoArtist = AllTypes & ~(1 << CategoryArtist);

    /// <summary>Separator joining type names in an exception message.</summary>
    private const string NameListSeparator = ", ";

    /// <summary>Separator joining suppressed category names in the compact <see cref="Describe"/> output.</summary>
    private const string CategorySeparator = ",";

    /// <summary>True when <paramref name="mask"/> permits category <paramref name="category"/>.</summary>
    public static bool Allows(int mask, int category) => ((mask >> category) & 1) != 0;

    /// <summary>
    /// Turn the caller's allow-list of category names into a mask.
    ///
    /// <para><b>The contract that catches callers out:</b> the list names the categories that stay ALLOWED, so a
    /// droppable category the caller does not mention is switched OFF. A client that hardcoded its list against an
    /// older, smaller droppable set therefore silently disables whatever was added since: a standing
    /// <c>character,copyright,meta</c> reads as no-general-no-artist and generation collapses to
    /// <c>[highres, original]</c>.</para>
    ///
    /// <para>A null list means <see cref="NoArtist"/>. An unrecognised name throws rather than being dropped: a typo
    /// would otherwise read as "off" and quietly change what the model may emit.</para>
    /// </summary>
    public static int FromAllowedNames(IReadOnlyCollection<string>? allowedNames)
    {
        if (allowedNames is null)
            return NoArtist;

        var allowed = allowedNames
            .Select(n => n.Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var suppressible = Droppable.ToDictionary(c => Names[c], c => c, StringComparer.Ordinal);
        var unknown = allowed.Where(n => !suppressible.ContainsKey(n)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                $"unknown tag type(s) {string.Join(NameListSeparator, unknown)}; the suppressible types are "
                + string.Join(NameListSeparator, suppressible.Keys.Order(StringComparer.Ordinal)),
                nameof(allowedNames));

        var mask = AllTypes;
        foreach (var (name, category) in suppressible)
            if (!allowed.Contains(name))
                mask &= ~(1 << category);
        return mask;
    }

    /// <summary>'all', or 'no:artist,character' — for logs and diagnostics.</summary>
    public static string Describe(int mask)
    {
        var missing = Enumerable.Range(0, CategoryCount)
            .Where(c => Names.ContainsKey(c) && !Allows(mask, c))
            .Select(c => Names[c])
            .ToArray();
        return missing.Length == 0 ? "all" : "no:" + string.Join(CategorySeparator, missing);
    }
}
