using ImageGen.Application.Prompting;
using ImageGen.Application.Workflows;
using ImageGen.Domain;

namespace ImageGen.Application.Rendering;

/// <summary>The prompt actually rendered by the model, plus a map of which tokens were bookmarkable tags vs artists.</summary>
/// <param name="Rendered">The finalized prompt text (markers stripped per the model's rules).</param>
/// <param name="Marks">{ canonicalName -&gt; "tag"|"artist" } for the explicitly-marked tokens.</param>
public sealed record FinalizedPrompt(string Rendered, Dictionary<string, string> Marks);

/// <summary>
/// Turns the RAW prompt the SPA sends (booru tags/artists still carrying their '#'/'@' autocomplete markers) into the
/// prompt actually rendered, and emits a map of which tokens were bookmarkable tags vs artists. The marker is the
/// source of truth: a comma-segment beginning with '#' is a tag, '@' an artist. Rendering rules mirror the SPA's
/// client-side finalize: '#' stripped unconditionally; '@' stripped unless the model documents '@artist'; underscores
/// become spaces for models that want it (score_ tags excepted). Models without a tagging block pass through unchanged.
/// </summary>
public static class PromptFinalizer
{
    /// <summary>Render <paramref name="rawPrompt"/> for the model and return it plus a { canonicalName -&gt; "tag"|"artist" }
    /// map of the explicitly-marked tokens.</summary>
    public static FinalizedPrompt Finalize(string? rawPrompt, WorkflowTagging? tg)
    {
        // A model with no tagging block — or one that speaks neither tags nor artists — gets its prompt back
        // BYTE-FOR-BYTE. Comma is sentence punctuation to a natural-language model, not a tag delimiter, so NONE of the
        // comma-segment management below runs for it: not marker stripping, not underscore folding, and not '~' guide
        // removal. A '~'-led segment therefore renders exactly as typed here — '~' means "guide tag" only inside the
        // tagging gate, the sole place it is ever read (the predictor seed in TagSeed, off the RAW prompt).
        var marks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tg is null || (!tg.Tags && !tg.Artists)) return new FinalizedPrompt(rawPrompt ?? string.Empty, marks);

        // Tag model only: '~' GUIDE TAGS are dropped up front — they steer the predictor's seed (see TagSeed) but the
        // image model never sees one — and everything below operates on the guide-free prompt.
        var raw = PromptMarkers.WithoutGuides(rawPrompt);

        // 1) Marks: a leading '#'/'@'/'!' on a comma-segment declares a bookmarkable tag/artist. '!' is an INERT TAG —
        //    it marks as a plain tag here on purpose. Its inertness is a fact about ONE consumer (the seed handed to
        //    the tag predictor, which reads it via PromptMarkers.InertKeys off the raw string); to chips, bookmarks and
        //    bans it is an ordinary tag, so it must not become a third kind in this map.
        //    '~' guide tags cannot appear here at all — they were removed above. That is deliberate and not an
        //    oversight: marks describe the PRODUCED IMAGE, and a guide tag is by definition not in it. Marking one
        //    would chip it on the card, offer it as a bookmark, and file a ban under a tag the picture never had.
        foreach (var seg in raw.Split(','))
        {
            var t = seg.TrimStart();
            if (t.Length == 0) continue;
            var m = t[0];
            if (!PromptMarkers.IsMarker(m)) continue;
            var canonical = Normalize(t[1..]);
            if (canonical.Length > 0) marks[canonical] = m == PromptMarkers.ArtistMarker ? TokenKinds.Artist : TokenKinds.Tag;
        }

        // 2) Render: strip the LEADING '#' of a segment always; the leading '@'
        //    unless kept; '_'->space per non-score_ segment when the model wants spaces.
        var s = string.Join(SegmentSeparator, raw.Split(',').Select(seg => StripMarker(seg, tg.KeepArtistMarker)));
        if (tg.UnderscoresToSpaces)
            s = string.Join(SegmentSeparator, s.Split(',').Select(seg =>
                seg.TrimStart().StartsWith(ScorePrefix) ? seg : seg.Replace('_', ' ')));
        return new FinalizedPrompt(s, marks);
    }

    /// <summary>
    /// Drop a comma-segment's LEADING marker, preserving the segment's leading whitespace. Only position 0 (after
    /// whitespace) is a marker — the same rule step 1 reads marks by. A '#'/'@' anywhere else is part of the token and
    /// must survive: booru tags natively contain all three ('#compass', 'genei_ibunroku_#fe', '@_@', 'j@ck', '!?',
    /// '!-shaped_pupils'), and HTML entities in scraped tag names carry a '#' too ('&amp;#039;'). A blanket Replace would
    /// eat all of those. A tag that genuinely BEGINS with a marker is written with its own marker in front ('#!!', '#@_@').
    /// </summary>
    private static string StripMarker(string seg, bool keepArtistMarker)
    {
        var i = 0;
        while (i < seg.Length && char.IsWhiteSpace(seg[i])) i++;
        if (i == seg.Length) return seg;
        var m = seg[i];
        // '!' is stripped unconditionally, exactly like '#': it is a tag marker, and by this point the seed build has
        // already read it off the RAW prompt. Nothing about it should reach the image model.
        var strip = m == PromptMarkers.TagMarker || m == PromptMarkers.InertTagMarker
                 || (m == PromptMarkers.ArtistMarker && !keepArtistMarker);
        return strip ? seg[..i] + seg[(i + 1)..] : seg;
    }

    /// <summary>Canonical bookmark key for a token: trim, whitespace-&gt;underscores, lowercase, no marker. The inverse
    /// direction (a stored prompt back into marker form) reads the same key, so both come from <see cref="PromptMarkers"/>.</summary>
    public static string Normalize(string? s) => PromptMarkers.Key(s);

    /// <summary>The canonical tag/artist keys a raw negative prompt asks NOT to see. Every comma-segment counts: '@'
    /// declares an artist, everything else (whether marked '#' or typed plain) is a tag. Random generation treats these
    /// as exclusions — something the user pushed into the negative must never be sampled back in as a positive.</summary>
    public static (HashSet<string> Tags, HashSet<string> Artists) NegativeKeys(string? rawNegative)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var artists = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seg in (rawNegative ?? string.Empty).Split(','))
        {
            var t = seg.Trim();
            if (t.Length == 0) continue;
            var key = Normalize(t.TrimStart(PromptMarkers.TagMarker, PromptMarkers.ArtistMarker,
                                             PromptMarkers.InertTagMarker, PromptMarkers.GuideTagMarker));
            if (key.Length > 0) (t[0] == '@' ? artists : tags).Add(key);
        }
        return (tags, artists);
    }

    /// <summary>
    /// The marker-form tokens for the bare names a random sampler produced — "long_hair" -&gt; "#long_hair",
    /// "kazaana" -&gt; "@kazaana" — canonicalized, with the empty and the <paramref name="banned"/> dropped. The raw
    /// prompt is written in the marker dialect and the samplers append to it in that same dialect, so this is where a
    /// sampled name acquires its kind.
    ///
    /// The marker comes from the NAME's own category (<paramref name="isArtist"/>), never from which sampler emitted
    /// it. Everything downstream reads the kind off the marker and nothing re-derives it: the marks map, the chip a
    /// name draws as, and the key a bookmark or a ban is filed under. So an artist the tag model returned — which it
    /// does whenever the generation mask has artists on — marked '#' is an artist RECORDED AS A TAG, in the marks, in
    /// the chips, and in dbo.BannedToken. That is a data defect, not a display one: it outlives the generation.
    /// </summary>
    public static List<string> MarkSampled(IEnumerable<string>? sampled, IReadOnlySet<string> banned, Func<string, bool> isArtist)
    {
        var tokens = new List<string>();
        foreach (var name in sampled ?? [])
        {
            var key = Normalize(name);
            if (key.Length == 0 || banned.Contains(key)) continue;
            tokens.Add((isArtist(key) ? PromptMarkers.ArtistMarker : PromptMarkers.TagMarker) + key);
        }
        return tokens;
    }

    /// <summary>Comma-join <paramref name="segment"/> onto <paramref name="prompt"/>. A prompt the user left with a
    /// trailing separator ("1girl,") must not render as "1girl,, next_tag", so strip any straggling commas/whitespace
    /// off the tail first.</summary>
    public static string Append(string? prompt, string segment)
    {
        var p = (prompt ?? string.Empty).TrimEnd(Separators);
        return p.Length == 0 ? segment : p + ", " + segment;
    }

    private const string SegmentSeparator = ",";
    private const string ScorePrefix = "score_";

    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];
}
