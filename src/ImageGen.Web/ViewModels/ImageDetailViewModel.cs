//TODO: CHECK FOR FALLBACKS
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
    /// wrote. It rides the record blob and is what the card's copy button and its Reload both submit. Empty only for a
    /// row written before the column existed and not yet backfilled.
    /// </summary>
    public required string MarkerPrompt { get; init; }

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

    /// <summary>The prompt split into chips: marked tags/artists become interactive, the rest plain text. Displayed by
    /// state (bookmarked, untouched, banned), then by type (artist, meta, copyright, character, general, deprecated,
    /// plain text), then by name. Prompt order does not survive: every level of the sort is a property of the token, so
    /// the same tag lands in the same place on every card it appears on.</summary>
    public IReadOnlyList<PromptChip> Chips
    {
        get
        {
            var marks = Entry.Marks;
            var segments = PromptMarkers.Segments(Entry.Prompt);
            if (segments.Length == 0)
                return [new PromptChip(Entry.Prompt ?? "(no prompt)", null, "")];

            var chips = new List<(PromptChip Chip, int Rank)>(segments.Length);
            foreach (var seg in segments)
            {
                var key = PromptMarkers.Key(seg);
                if (marks.TryGetValue(key, out var kind))
                {
                    var isArtist = kind == TokenKinds.Artist;
                    var banned = isArtist ? BannedArtists.Contains(key) : BannedTags.Contains(key);
                    var bookmarked = isArtist ? BookmarkedArtists.Contains(key) : BookmarkedTags.Contains(key);
                    var type = isArtist ? 1 : TagTypeByToken.GetValueOrDefault(key);
                    var category = isArtist ? null : TagCategory.Slug(type);
                    chips.Add((new PromptChip(seg, kind, key, banned, bookmarked, category), TagCategory.DisplayRank(type)));
                }
                else
                {
                    chips.Add((new PromptChip(seg, null, key), TagCategory.PlainTextRank));
                }
            }
            return chips
                .OrderBy(c => StateRank(c.Chip))
                .ThenBy(c => c.Rank)
                .ThenBy(c => c.Chip.Key, StringComparer.Ordinal)
                .Select(c => c.Chip)
                .ToList();
        }
    }

    /// <summary>
    /// Top-level display order: bookmarked, untouched, banned. A banned token is one the user has deliberately pushed
    /// out of auto-gen, so it belongs at the END of the card rather than sitting among the tags it was banned from.
    /// Bookmarked wins when a token is somehow both — the chip's click cycle makes the two exclusive, but the bookmark
    /// and ban stores are independent and nothing stops a token being written to both.
    /// </summary>
    private static int StateRank(PromptChip chip) => chip.Bookmarked ? 0 : chip.Banned ? 2 : 1;
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

    /// <summary>Chips that aren't tags at all (plain prompt text) display after every tag.</summary>
    public const int PlainTextRank = 6;

    /// <summary>Display order of a tag type on the image card: artist, meta, copyright, character, general, deprecated.</summary>
    public static int DisplayRank(int type) => type switch
    {
        1 => 0,  // artist
        5 => 1,  // meta
        3 => 2,  // copyright
        4 => 3,  // character
        6 => 5,  // deprecated
        _ => 4,  // general / unknown
    };
}
