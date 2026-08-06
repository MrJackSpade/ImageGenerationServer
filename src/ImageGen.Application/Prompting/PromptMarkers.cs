using System.Text;
using System.Text.RegularExpressions;

namespace ImageGen.Application.Prompting;

/// <summary>The result of <see cref="PromptMarkers.Parse"/>: the one parse of a comma-segment every consumer routes
/// through. <paramref name="Marker"/> is <see cref="PromptMarkers.NoMarker"/> when the segment is unmarked.</summary>
/// <param name="Marker">The leading marker '#'/'@'/'!'/'~', or <see cref="PromptMarkers.NoMarker"/>.</param>
/// <param name="MarkerIndex">The marker's index in the original segment, or -1 when unmarked.</param>
/// <param name="Key">The canonical base tag (weight, marker and casing stripped; whitespace to underscores).</param>
/// <param name="Rendered">The segment with ONLY the marker removed — weight/emphasis, escapes and whitespace kept.</param>
public readonly record struct MarkedSegment(char Marker, int MarkerIndex, string Key, string Rendered)
{
    /// <summary>True when the segment carries a leading marker.</summary>
    public bool HasMarker => MarkerIndex >= 0;
}

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

    /// <summary>Absence of a leading marker (the <see cref="MarkedSegment.Marker"/> of a plain, unmarked segment).</summary>
    public const char NoMarker = '\0';

    /// <summary>True when <paramref name="c"/> is a marker character.</summary>
    public static bool IsMarker(char c) =>
        c is TagMarker or ArtistMarker or InertTagMarker or GuideTagMarker;

    /// <summary>
    /// The ONE parse of a comma-segment — the single extraction every consumer routes through (the image prompt, the
    /// tag-model seed/exclusion, marks, bookmarks, bans), so the key, the rendered prompt and the tag-model tags can
    /// never disagree for the same input (issue #157). It reports:
    /// <list type="bullet">
    ///   <item><see cref="MarkedSegment.Marker"/> — the leading '#'/'@'/'!'/'~', found by peeling any A1111/Comfy
    ///   weight wrapper so a marker INSIDE one (<c>(#tag:1.2)</c>) is recognised exactly like one OUTSIDE
    ///   (<c>#(tag:1.2)</c>, the canonical spelling), or <see cref="NoMarker"/> when there is none;</item>
    ///   <item><see cref="MarkedSegment.Key"/> — the canonical base tag (see <see cref="Key"/>);</item>
    ///   <item><see cref="MarkedSegment.Rendered"/> — the segment with ONLY the marker removed: weight/emphasis,
    ///   escapes and surrounding whitespace are all preserved, so the picture is unchanged.</item>
    /// </list>
    /// </summary>
    public static MarkedSegment Parse(string? segment)
    {
        string seg = segment ?? string.Empty;
        int lead = 0;
        while (lead < seg.Length && char.IsWhiteSpace(seg[lead]))
        {
            lead++;
        }

        int idx = LeadingMarkerIndex(seg, lead);
        char marker = idx >= 0 ? seg[idx] : NoMarker;
        string rendered = idx >= 0 ? seg.Remove(idx, 1) : seg;   // remove the marker in place; weight + whitespace stay
        return new MarkedSegment(marker, idx, Key(seg), rendered);
    }

    /// <summary>The index in <paramref name="s"/> of the leading marker, or -1 when the segment is unmarked. Peels
    /// whole-segment weight wrappers (the SAME rule <see cref="StripWeight"/> uses) from <paramref name="from"/> inward
    /// so a marker sitting inside them is found; the trailing weight/close of each wrapper is at the far end and does
    /// not move the front index.</summary>
    internal static int LeadingMarkerIndex(string s, int from)
    {
        int lo = from, hi = s.Length;
        while (hi > lo && char.IsWhiteSpace(s[hi - 1]))
        {
            hi--;   // trailing whitespace must not defeat the "wrapper closes at the end" test
        }

        while (hi - lo >= 2)
        {
            if (s[lo] == '(' && MatchingClose(s, lo, hi, '(', ')') == hi - 1)
            {
                hi = TrailingWeightStart(s, lo + 1, hi - 1);   // narrow past a trailing ':weight' inside the '(...)'
                lo++;
                continue;
            }

            if (s[lo] == '[' && MatchingClose(s, lo, hi, '[', ']') == hi - 1)
            {
                lo++;
                hi--;
                continue;
            }

            break;
        }

        while (lo < hi && char.IsWhiteSpace(s[lo]))
        {
            lo++;
        }

        return lo < hi && IsMarker(s[lo]) ? lo : -1;
    }

    /// <summary>The index of the close bracket matching an <paramref name="open"/> at <paramref name="lo"/> within
    /// <c>[lo, hi)</c> (balanced nesting, '\'-escaped brackets skipped), or -1 — the ONE bracket matcher, shared by the
    /// whole-segment peel (<see cref="TryPeelWrapper"/>), the marker scan, and the tag parser's emphasis decomposition.</summary>
    internal static int MatchingClose(string s, int lo, int hi, char open, char close)
    {
        if (lo >= hi || s[lo] != open)
        {
            return -1;
        }

        int depth = 0;
        for (int i = lo; i < hi; i++)
        {
            char c = s[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The start index of a trailing <c>:weight</c> number inside <c>[lo, hi)</c> (so the marker scan and the
    /// emphasis decomposition can narrow past it), or <paramref name="hi"/> when there is none.</summary>
    internal static int TrailingWeightStart(string s, int lo, int hi)
    {
        if (hi <= lo)
        {
            return hi;
        }

        Match m = TrailingWeight().Match(s[lo..hi]);
        return m.Success ? lo + m.Index : hi;
    }

    /// <summary>
    /// The canonical key of a prompt segment: peel any A1111/Comfy emphasis-weight wrapper down to the base tag, drop a
    /// leading marker, collapse whitespace runs to '_', lowercase. This is the key marks, bookmarks and bans are all
    /// stored under — and, with its marker in front, the token the random samplers append.
    /// <see cref="Rendering.PromptFinalizer.Normalize"/> is this.
    /// <para>Weight/emphasis is IDENTITY-INVISIBLE: <c>tag</c>, <c>(tag)</c>, <c>(tag:1.2)</c> and <c>[tag]</c> are the
    /// same tag, so favouriting or banning one recognizes them all (issue #133). Only the KEY is stripped — the rendered
    /// prompt (built by <see cref="Rendering.PromptFinalizer.Finalize"/>) keeps the weight the user typed, so the picture
    /// is unchanged; solely who the segment MATCHES against changes. The wrapper is peeled before AND after the marker so
    /// the marker may sit either side of it (<c>#(tag:1.2)</c> and <c>(#tag:1.2)</c> both reduce to <c>tag</c>).</para>
    /// </summary>
    public static string Key(string? segment)
    {
        string s = StripWeight((segment ?? string.Empty).Trim());
        if (s.Length > 0 && IsMarker(s[0]))
        {
            s = StripWeight(s[1..]);
        }

        return Whitespace().Replace(s.Trim(), Tokens.Underscore).ToLowerInvariant();
    }

    /// <summary>
    /// Peel A1111/Comfy emphasis-weight syntax off a segment down to its base tag: an UNESCAPED bracket pair that wraps
    /// the WHOLE segment — <c>(tag)</c>/<c>[tag]</c> emphasis, <c>(tag:1.2)</c> explicit weight, and nested forms
    /// <c>((tag:1.1):1.2)</c> — is removed; an escaped <c>\(</c>/<c>\)</c> is a literal character and is unescaped in
    /// place so the escaped and bare spellings resolve to one key.
    /// <para>Only a pair that encloses the entire segment counts, so a booru tag that natively carries parens
    /// (<c>hatsune_miku_(vocaloid)</c>, <c>@_@</c>, <c>(a)_(b)</c>) is untouched — its bracket does not wrap the whole
    /// string. This mirrors A1111's own rule that literal parens must be escaped; an unescaped wrapping pair is weight.</para>
    /// </summary>
    private static string StripWeight(string s)
    {
        s = s.Trim();
        // Peel one wrapper per turn: '(...)' also carries an optional trailing ':weight'; '[...]' is bare de-emphasis.
        while (s.Length >= 2)
        {
            if (TryPeelWrapper(s, '(', ')', out string inner))
            {
                s = StripTrailingWeight(inner);
                continue;
            }

            if (TryPeelWrapper(s, '[', ']', out inner))
            {
                s = inner.Trim();
                continue;
            }

            break;
        }

        return Unescape(s);
    }

    /// <summary>True when <paramref name="s"/> is entirely wrapped by an unescaped <paramref name="open"/>/<paramref
    /// name="close"/> pair; <paramref name="inner"/> is the content between them. The opening bracket at index 0 must
    /// match the FINAL character with balanced nesting in between — so <c>(a)_(b)</c> (whose first '(' closes mid-string)
    /// is not a wrapper — and a '\'-escaped bracket is skipped, never counted.</summary>
    private static bool TryPeelWrapper(string s, char open, char close, out string inner)
    {
        inner = s;
        // A wrapper only counts when the opening bracket's match is the segment's FINAL character — otherwise the '('
        // closes mid-string (e.g. '(a)_(b)') and is a native bracket, not weight. One bracket matcher, shared with the
        // marker scan, so the two agree on what a whole-segment wrapper is.
        int c = MatchingClose(s, 0, s.Length, open, close);
        if (c != s.Length - 1)
        {
            return false;
        }

        inner = s[1..c];
        return true;
    }

    /// <summary>Drop a single trailing <c>:weight</c> emphasis number from a peeled '(...)' body: <c>tag:1.2</c> -&gt;
    /// <c>tag</c>. The tail must be a bare number, so a booru tag that ends in a non-numeric colon segment
    /// (<c>re:zero</c>) keeps it.</summary>
    private static string StripTrailingWeight(string inner)
    {
        string t = inner.Trim();
        Match m = TrailingWeight().Match(t);
        return (m.Success ? t[..m.Index] : t).Trim();
    }

    /// <summary>Unescape the bracket escapes A1111 uses for literal brackets in a tag name ('\(' -> '('), leaving every
    /// other backslash as-is.</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }

        StringBuilder sb = new(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && IsBracket(s[i + 1]))
            {
                _ = sb.Append(s[++i]);
                continue;
            }

            _ = sb.Append(s[i]);
        }

        return sb.ToString();
    }

    private static bool IsBracket(char c) => c is '(' or ')' or '[' or ']';

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
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string seg in Segments(rawPrompt))
        {
            MarkedSegment p = Parse(seg);   // weight-aware: '(!pig:1.3)' is found exactly like '!pig'
            if (p.Marker == marker && p.Key.Length > 0)
            {
                _ = keys.Add(p.Key);
            }
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
        if (!raw.Contains(GuideTagMarker))
        {
            return raw;
        }

        return string.Join(',', raw.Split(',').Select(seg =>
        {
            MarkedSegment p = Parse(seg);   // weight-aware: rewrites the '~' wherever it sits, e.g. '(~t:1.1)' -> '(#t:1.1)'
            return p.Marker == GuideTagMarker
                ? string.Concat(seg.AsSpan(0, p.MarkerIndex), TagMarker.ToString(), seg.AsSpan(p.MarkerIndex + 1))
                : seg;
        }));
    }

    /// <summary>The prompt with every '~' guide segment REMOVED — what the image model is allowed to see.</summary>
    public static string WithoutGuides(string? rawPrompt)
    {
        string raw = rawPrompt ?? string.Empty;
        if (!raw.Contains(GuideTagMarker))
        {
            return raw;
        }

        return string.Join(',', raw.Split(',').Where(seg => Parse(seg).Marker != GuideTagMarker));
    }

    /// <summary>The canonical-key text tokens (as strings, distinct from the <c>const char</c> markers above).</summary>
    private static class Tokens
    {
        /// <summary>Whitespace is collapsed to this in a canonical bookmark key.</summary>
        public const string Underscore = "_";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A trailing ':weight' emphasis number ('(tag:1.2)' -> the ':1.2'), matched only when the tail is a bare
    /// number so a real ':' inside a tag name is never mistaken for one.</summary>
    [GeneratedRegex(@":\s*-?\d+(?:\.\d+)?\s*$")]
    private static partial Regex TrailingWeight();
}
