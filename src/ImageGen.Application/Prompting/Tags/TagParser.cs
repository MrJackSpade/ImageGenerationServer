using System.Globalization;
using System.Text;

namespace ImageGen.Application.Prompting.Tags;

/// <summary>
/// Stage 1 of tag handling: UNWRAP text into strongly-typed <see cref="ParsedTag"/>s. Model-independent — it reads the
/// marker (kind), the A1111/Comfy weight wrapper (strength), and the position (ordinal) off each comma-segment, however
/// they nest, without deciding anything about how a particular model will render them. The marker is found by peeling
/// the weight wrapper, so <c>(#tag:1.2)</c> parses identically to <c>#(tag:1.2)</c>. This stage is independently
/// testable: text in, the exact typed tags out.
/// </summary>
public static class TagParser
{
    /// <summary>A1111's implicit emphasis step: <c>(t)</c> is ×1.1, <c>[t]</c> is ÷1.1.</summary>
    private const double EmphasisStep = 1.1;

    /// <summary>Unwrap a whole prompt into its tags, in order (empty segments dropped; ordinals are dense).</summary>
    public static IReadOnlyList<ParsedTag> Parse(string? prompt)
    {
        List<ParsedTag> tags = [];
        foreach (string seg in (prompt ?? string.Empty).Split(','))
        {
            ParsedTag? t = ParseSegment(seg, tags.Count);
            if (t is not null)
            {
                tags.Add(t);
            }
        }

        return tags;
    }

    /// <summary>Unwrap ONE comma-segment at <paramref name="ordinal"/>, or null when it is empty/whitespace.</summary>
    public static ParsedTag? ParseSegment(string? segment, int ordinal)
    {
        string trimmed = (segment ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        int markerIdx = PromptMarkers.LeadingMarkerIndex(trimmed, 0);
        char marker = markerIdx >= 0 ? trimmed[markerIdx] : PromptMarkers.NoMarker;
        string noMarker = markerIdx >= 0 ? trimmed.Remove(markerIdx, 1) : trimmed;

        (Emphasis emphasis, string baseText) = DecomposeEmphasis(noMarker);
        return new ParsedTag(ordinal, KindOf(marker), PromptMarkers.Key(trimmed), baseText, emphasis);
    }

    private static TagKind KindOf(char marker) => marker switch
    {
        PromptMarkers.TagMarker => TagKind.Tag,
        PromptMarkers.ArtistMarker => TagKind.Artist,
        PromptMarkers.InertTagMarker => TagKind.Inert,
        PromptMarkers.GuideTagMarker => TagKind.Guide,
        _ => TagKind.Plain,
    };

    /// <summary>
    /// Peel the A1111/Comfy weight wrappers off a marker-free segment into (<see cref="Emphasis"/>, base tag). The
    /// bracket text on each side is kept EXACTLY (so the tag renders with its typed weight), and the numeric strength is
    /// derived alongside. Only a pair that wraps the WHOLE segment is weight — a native bracket (<c>miku_(vocaloid)</c>,
    /// <c>(a)_(b)</c>) is left in the base tag, using the same whole-segment matcher <see cref="PromptMarkers.Key"/> does.
    /// </summary>
    private static (Emphasis Emphasis, string Base) DecomposeEmphasis(string s)
    {
        int lo = 0, hi = s.Length;
        while (hi > lo && char.IsWhiteSpace(s[hi - 1]))
        {
            hi--;
        }

        while (lo < hi && char.IsWhiteSpace(s[lo]))
        {
            lo++;
        }

        StringBuilder open = new();
        string close = string.Empty;
        double weight = 1.0;
        while (hi - lo >= 2)
        {
            if (s[lo] == '(' && PromptMarkers.MatchingClose(s, lo, hi, '(', ')') == hi - 1)
            {
                int wStart = PromptMarkers.TrailingWeightStart(s, lo + 1, hi - 1);
                weight *= wStart < hi - 1 ? ParseWeight(s, wStart, hi - 1) : EmphasisStep;   // ':w' explicit, else '()' = ×1.1
                _ = open.Append('(');
                close = s[wStart..hi] + close;   // ':1.2)' (or ')') — prepend so nesting closes outermost-last
                lo++;
                hi = wStart;
                continue;
            }

            if (s[lo] == '[' && PromptMarkers.MatchingClose(s, lo, hi, '[', ']') == hi - 1)
            {
                weight *= 1.0 / EmphasisStep;   // '[t]' = ÷1.1
                _ = open.Append('[');
                close = "]" + close;
                lo++;
                hi--;
                continue;
            }

            break;
        }

        return (new Emphasis(open.ToString(), close, weight), s[lo..hi]);
    }

    /// <summary>The weight value in a <c>:number</c> tail at <c>[colon, end)</c> (e.g. ":1.2" -&gt; 1.2), or 1.0.</summary>
    private static double ParseWeight(string s, int colon, int end)
    {
        string num = s[(colon + 1)..end].Trim();
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : 1.0;
    }
}
