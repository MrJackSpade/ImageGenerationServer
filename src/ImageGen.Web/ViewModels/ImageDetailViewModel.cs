using ImageGen.Application.Prompting;
using ImageGen.Domain;

namespace ImageGen.Web.ViewModels;

public sealed class ImageDetailViewModel
{
    /// <summary>Placeholder chip text shown when the image has no prompt to display.</summary>
    private const string NoPromptText = "(no prompt)";

    /// <summary>Delimiter a run of plain prompt segments is rejoined on — byte-identical to the split delimiter.</summary>
    private const string SegmentDelimiter = ",";

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
    /// border. Tags the catalog doesn't know are absent and treated as general.</summary>
    public IReadOnlyDictionary<string, int> TagTypeByToken { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public long Ts => new DateTimeOffset(DateTime.SpecifyKind(Entry.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>marks as the SPA's token-&gt;("tag"|"artist") map, for the client record blob.</summary>
    public IReadOnlyDictionary<string, string> MarksMap => Entry.Marks;

    /// <summary>
    /// The prompt as display chips, IN THE ORDER THE USER TYPED IT. A comma segment whose canonical key is in the marks
    /// map is a bookmarkable tag/artist and becomes its own interactive chip; everything else is plain natural-language
    /// text, preserved BYTE-FOR-BYTE — consecutive plain segments stay ONE chip (their commas and spacing intact), never
    /// split into a chip per comma and never reordered. A chip's bookmark / ban / category only STYLE it; they no longer
    /// move it. Comma-segment management is a booru-tag operation and must never reflow a prompt's prose — the same rule
    /// the finalizer follows for a non-tag model (see PromptFinalizerGatingTests).
    /// </summary>
    public IReadOnlyList<PromptChip> Chips
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Entry.Prompt))
                return [new PromptChip(NoPromptText, null, string.Empty)];

            var marks = Entry.Marks;
            var chips = new List<PromptChip>();
            var plain = new List<string>();

            void FlushPlain()
            {
                if (plain.Count == 0) return;
                // Rejoin the run on the original delimiter — split-then-join is identity, so the text is verbatim; only
                // the run's outer edges are trimmed for display.
                var text = string.Join(SegmentDelimiter, plain).Trim();
                if (text.Length > 0) chips.Add(new PromptChip(text, null, string.Empty));
                plain.Clear();
            }

            foreach (var seg in Entry.Prompt.Split(','))
            {
                var key = PromptMarkers.Key(seg);
                if (key.Length > 0 && marks.TryGetValue(key, out var kind))
                {
                    FlushPlain();
                    var isArtist = kind == TokenKinds.Artist;
                    var banned = isArtist ? BannedArtists.Contains(key) : BannedTags.Contains(key);
                    var bookmarked = isArtist ? BookmarkedArtists.Contains(key) : BookmarkedTags.Contains(key);
                    var category = isArtist ? null : TagCategory.Slug(TagTypeByToken.GetValueOrDefault(key));
                    chips.Add(new PromptChip(seg.Trim(), kind, key, banned, bookmarked, category));
                }
                else
                {
                    plain.Add(seg);
                }
            }
            FlushPlain();

            return chips.Count > 0 ? chips : [new PromptChip(NoPromptText, null, string.Empty)];
        }
    }
}

public sealed record PromptChip(
    string Text, string? Kind, string Key, bool Banned = false, bool Bookmarked = false, string? Category = null);

/// <summary>Maps a raw booru category id to the chip-border slug. Only the notable categories get a color; general (0),
/// deprecated (6), and anything unknown return null (neutral border). Artists are colored by kind, not this.</summary>
public static class TagCategory
{
    public static string? Slug(int type) => type switch
    {
        3 => "copyright",
        4 => "character",
        5 => "meta",
        _ => null,
    };
}
