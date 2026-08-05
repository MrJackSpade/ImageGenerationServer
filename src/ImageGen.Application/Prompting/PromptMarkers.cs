using System.Text.RegularExpressions;

namespace ImageGen.Application.Prompting;

/// <summary>
/// MARKER FORM — the prompt dialect every prompt box in the app speaks: comma segments where a booru tag carries '#'
/// and an artist '@', both on the CANONICAL UNDERSCORED token ("#long_hair, @greg_rutkowski, a plain phrase"). It is
/// what the composer's tag box writes, what /generate and /edit accept, the form the random samplers append to, and the
/// only form <see cref="Rendering.PromptFinalizer"/> can recover marks from.
///
/// It is STORED, verbatim, as <see cref="Domain.Entities.HistoryEntry.RawPrompt"/>. The surfaces that hand a prompt back
/// to a prompt box (the card's copy button, its Reload, the Edit page's inpaint box) load that string as-is.
///
/// There is deliberately no inverse here. Rebuilding the marker form from a finalized prompt is lossy — finalization
/// folds underscores and strips markers, and no amount of care recovers underscores inside unmarked prose or the
/// original casing — so copy, the inpaint box and Reload would each end up with a different answer for the same
/// image ("#long_hair", "#long hair", "long hair"). If something needs the raw prompt, store it; do not reconstruct it.
/// </summary>
public static partial class PromptMarkers
{
    public const char TagMarker = '#';
    public const char ArtistMarker = '@';

    /// <summary>
    /// INERT TAG — '!' marks a tag that reaches the image model but is INVISIBLE to the tag predictor. It is a tag in
    /// every other respect: it renders, it marks, it chips, it bookmarks and bans exactly like '#'.
    ///
    /// It exists because the predictor conditions on co-occurrence, so a strong, common subject drags the whole sampled
    /// set toward its own corpus neighbourhood — seed "#pig" and the fantasy tags you actually wanted lose out to
    /// barnyard ones. Writing "!pig" keeps the pig in the picture and lets the rest of the prompt steer the sample.
    ///
    /// Concretely it does two things, both by reusing machinery '@' and the ban set already had: the segment is
    /// subtracted from the seed handed to the predictor, and its key is added to that call's ban set. The ban half is
    /// NOT optional — the predictor never echoes its own seed, so a tag hidden FROM the seed becomes a tag it may
    /// freely sample, and "!pig" would come back as "!pig, ..., #pig".
    /// </summary>
    public const char InertTagMarker = '!';

    /// <summary>
    /// GUIDE TAG — '~' is the exact mirror of <see cref="InertTagMarker"/>: it is VISIBLE to the tag predictor and
    /// never reaches the image model. '!' keeps a thing in the picture while hiding it from the suggester; '~' steers
    /// the suggester toward a thing that is deliberately NOT in the picture.
    ///
    /// That makes subject swapping expressible in one prompt. "!1girl, ~1boy" samples tags from the neighbourhood of
    /// 1boy — the poses, clothing and framing that co-occur with it — and renders them onto a girl, because 1boy is
    /// dropped before the prompt reaches the model and 1girl was hidden from the seed so it could not drag the sample
    /// back toward its own corpus.
    ///
    /// Concretely: the segment is REMOVED from the finalized prompt (so nothing about it renders), it is NOT marked
    /// (marks describe the produced image, and this is not in it — chipping or bookmarking it would be a lie), it is
    /// added to the seed handed to the predictor, and its key is banned for that call. The ban is not redundant with
    /// the seed: the predictor is asked for tags AROUND this one, and one it echoed would be appended as an ordinary
    /// '#' tag and reach the model — which is the one thing '~' promises cannot happen.
    /// </summary>
    public const char GuideTagMarker = '~';

    /// <summary>True when <paramref name="c"/> is a leading marker. Position 0 of a comma-segment is the ONLY place any
    /// of them mean anything — booru tags natively contain all three ('#compass', '@_@', '!?', '!-shaped_pupils').</summary>
    public static bool IsMarker(char c) =>
        c == TagMarker || c == ArtistMarker || c == InertTagMarker || c == GuideTagMarker;

    /// <summary>True when a comma-segment carries <paramref name="marker"/> — position 0 after any leading
    /// whitespace, the only place a marker means anything.</summary>
    public static bool IsMarkedWith(string? segment, char marker)
    {
        ReadOnlySpan<char> s = (segment ?? string.Empty).AsSpan().TrimStart();
        return s.Length > 0 && s[0] == marker;
    }

    /// <summary>
    /// The canonical key of a prompt segment: trim, drop a leading marker, collapse whitespace runs to '_', lowercase.
    /// This is the key marks, bookmarks and bans are all stored under — and, with its marker in front, the token the
    /// random samplers append. <see cref="Rendering.PromptFinalizer.Normalize"/> is this.
    /// </summary>
    public static string Key(string? segment)
    {
        string s = (segment ?? string.Empty).Trim();
        if (s.Length > 0 && IsMarker(s[0]))
            s = s[1..];
        return Whitespace().Replace(s.Trim(), Underscore).ToLowerInvariant();
    }

    /// <summary>The comma segments of a prompt, trimmed, empties dropped.</summary>
    public static string[] Segments(string? prompt) =>
        (prompt ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The canonical keys of the '!'-marked segments of a RAW (marker-form) prompt — the tags the user asked the
    /// predictor not to see. Read off the raw string rather than a <see cref="Rendering.FinalizedPrompt"/>'s marks,
    /// because marks deliberately carry only "tag"/"artist": an inert tag IS a tag everywhere downstream (chips,
    /// bookmarks, bans), and widening that map would push a third kind through the wire and the ban tables for a
    /// distinction only the seed build cares about.
    /// </summary>
    public static HashSet<string> InertKeys(string? rawPrompt) => KeysMarkedWith(rawPrompt, InertTagMarker);

    /// <summary>The canonical keys of the '~'-marked segments — the tags the user wants the predictor STEERED BY but
    /// kept out of the picture. Read off the raw string for the same reason as <see cref="InertKeys"/>: finalization
    /// has already removed them by the time anything downstream could look.</summary>
    public static HashSet<string> GuideKeys(string? rawPrompt) => KeysMarkedWith(rawPrompt, GuideTagMarker);

    private static HashSet<string> KeysMarkedWith(string? rawPrompt, char marker)
    {
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string seg in Segments(rawPrompt))
        {
            if (seg[0] != marker) continue;
            string key = Key(seg);
            if (key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// The prompt with every '~' guide segment rewritten as an ordinary '#' tag, in place.
    /// <para>This is how the predictor's seed is built. Rewriting and then finalizing normally keeps each guide tag in
    /// the position the user wrote it and in the same rendered form as its neighbours — appending the keys to the end
    /// of an already-finalized seed would reorder the prompt and mix underscored keys into a seed whose other tags had
    /// their underscores folded.</para>
    /// </summary>
    public static string GuidesAsTags(string? rawPrompt)
    {
        string raw = rawPrompt ?? string.Empty;
        if (!raw.Contains(GuideTagMarker)) return raw;
        return string.Join(',', raw.Split(',').Select(seg =>
        {
            int i = 0;
            while (i < seg.Length && char.IsWhiteSpace(seg[i])) i++;
            return i < seg.Length && seg[i] == GuideTagMarker
                ? string.Concat(seg.AsSpan(0, i), TagMarker.ToString(), seg.AsSpan(i + 1))
                : seg;
        }));
    }

    /// <summary>The prompt with every '~' guide segment REMOVED — what the image model is allowed to see.</summary>
    public static string WithoutGuides(string? rawPrompt)
    {
        string raw = rawPrompt ?? string.Empty;
        if (!raw.Contains(GuideTagMarker)) return raw;
        return string.Join(',', raw.Split(',').Where(seg => !IsMarkedWith(seg, GuideTagMarker)));
    }

    private const string Underscore = "_";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
