using ImageGen.Application.Prompting;
using ImageGen.Domain;

namespace ImageGen.Web.ViewModels;

public sealed class ImageDetailViewModel
{
    public required ImageDetailView Entry { get; init; }
    public required bool IsBookmarked { get; init; }
    public string? NewerId { get; init; }
    public string? OlderId { get; init; }

    /// <summary>
    /// This image's prompt VERBATIM as it was submitted, in marker form ("#bad_anatomy, @greg_rutkowski, a plain
    /// phrase") — <see cref="ImageGen.Domain.Entities.HistoryEntry.RawPrompt"/>, loaded as-is from the row the worker
    /// wrote. It rides the record blob and is what the card's copy button and its Reload both submit. Null (NOT "")
    /// for a row written before the column existed and not yet backfilled — carried as the null RawPrompt actually is,
    /// so "no marker prompt" stays a distinct state (the client's `||` then falls back to the finalized prompt).
    /// </summary>
    public string? MarkerPrompt { get; init; }

    /// <summary>
    /// This image's NEGATIVE prompt verbatim, in the same marker form — <c>HistoryEntry.RawNegativePrompt</c>, loaded
    /// as-is. Null when no negative was submitted, which Reload must preserve: a null leaves the model's built-in
    /// default negative alone, and sending "" instead is a different picture.
    /// </summary>
    public string? MarkerNegativePrompt { get; init; }

    /// <summary>
    /// This image's prompt as the user TYPED it — <c>HistoryEntry.OriginalPrompt</c>, loaded as-is. Despite its name
    /// <see cref="MarkerPrompt"/> is post-resolution: the composer collapses <c>[a|b]</c>, fans <c>{a|b}</c> into
    /// separate images and appends an artist page's artist before submitting, and the worker then appends its sampled
    /// tags. This is the only record of what was asked for.
    /// <para>Null for every image made before it was recorded, and it cannot be backfilled — the pre-expansion text
    /// was discarded in the browser and never sent. Surfaces must say "not recorded", never substitute the resolved
    /// prompt, or a copy would hand back a different string than the one requested.</para>
    /// </summary>
    public string? OriginalPrompt { get; init; }

    /// <summary>Canonical tag/artist names banned for this image's model — chips matching these render "banned".</summary>
    public IReadOnlySet<string> BannedTags { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> BannedArtists { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Canonical tag/artist names the user has bookmarked — chips matching these render "on" (starred).</summary>
    public IReadOnlySet<string> BookmarkedTags { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> BookmarkedArtists { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Canonical tag token → raw booru category id (see <see cref="TagCategory"/>), for coloring the chip
    /// border and ordering the chips by type. Tags the catalog doesn't know are absent and treated as general.</summary>
    public IReadOnlyDictionary<string, int> TagTypeByToken { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public long Ts => new DateTimeOffset(DateTime.SpecifyKind(Entry.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>marks as the SPA's token-&gt;("tag"|"artist") map, for the client record blob.</summary>
    public IReadOnlyDictionary<string, string> MarksMap => Entry.Marks;

    /// <summary>
    /// The prompt as display chips. A comma segment whose canonical key is in the marks map is a bookmarkable tag/artist
    /// and becomes its own interactive chip; everything else is plain natural-language text, preserved BYTE-FOR-BYTE —
    /// consecutive plain segments stay ONE chip (their commas and spacing intact), never split into a chip per comma and
    /// never reordered among themselves.
    /// <para>The <b>chips</b> are grouped ahead of the plain prose and ordered by state (bookmarked, untouched, banned),
    /// then by type (artist, meta, copyright, character, general, deprecated), then by name — so the same tag lands in the
    /// same place on every card. That ordering is a booru-tag operation and applies ONLY to chips: the plain prose is
    /// never split, alphabetized, or interleaved with the tags — it is emitted verbatim as the final group, in the order
    /// its runs were written (the finalizer's non-tag rule; see PromptFinalizerGatingTests).</para>
    /// </summary>
    public IReadOnlyList<PromptChip> Chips
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Entry.Prompt))
            {
                return [new PromptChip(Labels.NoPromptText, null, string.Empty)];
            }

            IReadOnlyDictionary<string, string> marks = Entry.Marks;
            List<(PromptChip Chip, int TypeRank)> chips = [];
            List<string> plain = [];

            void FlushPlain()
            {
                if (plain.Count == 0)
                {
                    return;
                }
                // Rejoin the run on the original delimiter — split-then-join is identity, so the text is verbatim; only
                // the run's outer edges are trimmed for display. Plain runs carry no type rank; they group last by Kind.
                string text = string.Join(Delimiters.SegmentDelimiter, plain).Trim();
                if (text.Length > 0)
                {
                    chips.Add((new PromptChip(text, null, string.Empty), 0));
                }

                plain.Clear();
            }

            foreach (string seg in Entry.Prompt.Split(','))
            {
                string key = PromptMarkers.Key(seg);
                if (key.Length > 0 && marks.TryGetValue(key, out string? kind))
                {
                    FlushPlain();
                    bool isArtist = kind == TokenKinds.Artist;
                    bool banned = isArtist ? BannedArtists.Contains(key) : BannedTags.Contains(key);
                    bool bookmarked = isArtist ? BookmarkedArtists.Contains(key) : BookmarkedTags.Contains(key);
                    // Provenance is an orthogonal axis: true dashes the chip's border to mark it auto-generated. Unknown
                    // (pre-provenance rows carry no set) renders no dash — never a guess.
                    bool generated = Entry.GeneratedTokens?.Contains(key) == true;
                    int type = isArtist ? TagCategory.ArtistType : TagTypeByToken.GetValueOrDefault(key);
                    // Artists are distinguished by their kind (data-kind + .tagchip.artist), not a booru category, so
                    // they carry no data-category; every real tag resolves to a name (general when the catalog is silent).
                    string? category = isArtist ? null : TagCategory.Name(type);
                    // The chip's DISPLAY name drops the escaping backslashes literal delimiters carry for the image
                    // model ('ganyu \(genshin impact\)' -> 'ganyu (genshin impact)'); the finalized prompt keeps them.
                    chips.Add((new PromptChip(PromptMarkers.DisplayName(seg.Trim()), kind, key, banned, bookmarked, category, generated), TagCategory.DisplayRank(type)));
                }
                else
                {
                    plain.Add(seg);
                }
            }

            FlushPlain();

            if (chips.Count == 0)
            {
                return [new PromptChip(Labels.NoPromptText, null, string.Empty)];
            }

            // Chips (Kind non-null) first, plain prose (Kind null) last; within the chips: state, then type, then name.
            // OrderBy is stable, so plain runs keep the order they were written in — prose is grouped, never reordered.
            return [.. chips
                .OrderBy(c => c.Chip.Kind is null ? 1 : 0)
                .ThenBy(c => StateRank(c.Chip))
                .ThenBy(c => c.TypeRank)
                .ThenBy(c => c.Chip.Key, StringComparer.Ordinal)
                .Select(c => c.Chip)];
        }
    }

    /// <summary>
    /// Chip display order within the tag group: bookmarked, untouched, banned. A banned token is one the user has
    /// deliberately pushed out of auto-gen, so it trails the untouched tags — but it is still a chip and stays ahead of
    /// the plain prose. Bookmarked wins when a token is somehow both: the chip's click cycle makes the two exclusive, but
    /// the bookmark and ban stores are independent and nothing stops a token being written to both.
    /// </summary>
    private static int StateRank(PromptChip chip) => chip.Bookmarked ? 0 : chip.Banned ? 2 : 1;

    /// <summary>User-facing chip labels.</summary>
    private static class Labels
    {
        /// <summary>Placeholder chip text shown when the image has no prompt to display.</summary>
        public const string NoPromptText = "(no prompt)";
    }

    /// <summary>Prompt-segment delimiters.</summary>
    private static class Delimiters
    {
        /// <summary>Delimiter a run of plain prompt segments is rejoined on — byte-identical to the split delimiter.</summary>
        public const string SegmentDelimiter = ",";
    }
}

public sealed record PromptChip(
    string Text, string? Kind, string Key, bool Banned = false, bool Bookmarked = false, string? Category = null,
    bool Generated = false);

/// <summary>Maps a raw booru category id to its category name. Every tag resolves to a name (general is the default),
/// so a chip always carries a non-empty <c>data-category</c>. Only the notable names have a color rule in CSS; general,
/// deprecated and unknown fall through to the neutral border. Artists are colored by kind, not this.</summary>
public static class TagCategory
{
    /// <summary>The synthetic category id used for artist chips — they aren't booru tags but still order first.</summary>
    public const int ArtistType = 1;

    /// <summary>Booru category names, one per raw category id. These are a fixed vocabulary, not free text.</summary>
    private static class Names
    {
        public const string General = "general";
        public const string Copyright = "copyright";
        public const string Character = "character";
        public const string Meta = "meta";
        public const string Deprecated = "deprecated";
    }

    /// <summary>The resolved category name for a tag, always non-null — the value a chip's <c>data-category</c> carries.</summary>
    public static string Name(int type) => type switch
    {
        3 => Names.Copyright,
        4 => Names.Character,
        5 => Names.Meta,
        6 => Names.Deprecated,
        _ => Names.General,
    };

    /// <summary>Display order of a tag type within the chip group: artist, meta, copyright, character, general,
    /// deprecated.</summary>
    public static int DisplayRank(int type) => type switch
    {
        ArtistType => 0,  // artist
        5 => 1,           // meta
        3 => 2,           // copyright
        4 => 3,           // character
        6 => 5,           // deprecated
        _ => 4,           // general / unknown
    };
}