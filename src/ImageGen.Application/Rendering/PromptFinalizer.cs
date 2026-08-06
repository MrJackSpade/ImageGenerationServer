using ImageGen.Application.Prompting;
using ImageGen.Application.Prompting.Tags;
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
        // The one render path: the tag service reassembles the parsed tags for this model. On the render path the prompt
        // is already resolved (groups expanded at enqueue), so this is a group-free reassembly — the image prompt and
        // its marks from the SAME parse, so they cannot disagree. A prose model gets the text back byte-for-byte.
        GeneratedTagGroup g = GeneratedTagGroup.FromResolvedText(rawPrompt ?? string.Empty);
        return new FinalizedPrompt(g.ToImageModel(tg), g.Marks(tg));
    }

    /// <summary>Canonical bookmark key for a token: trim, whitespace-&gt;underscores, lowercase, no marker. The inverse
    /// direction (a stored prompt back into marker form) reads the same key, so both come from <see cref="PromptMarkers"/>.</summary>
    public static string Normalize(string? s) => PromptMarkers.Key(s);

    /// <summary>
    /// The canonical tag/artist keys a raw negative prompt asks the TAG PREDICTOR not to sample — the tag-model-side
    /// half of the negative. A tag the user negated must never be handed back to them as a randomly-chosen positive.
    ///
    /// <para>The negative mirrors the positive prompt's marker VISIBILITY as suppression, so which side a negated
    /// segment binds is scoped by its marker — exactly as the same marker scopes visibility in the positive box:
    /// <list type="bullet">
    ///   <item>'#'-marked and plain-typed — suppressed in BOTH: excluded here AND sent as negative conditioning by
    ///   <see cref="Finalize"/>. ('@' likewise, as an artist exclusion.)</item>
    ///   <item>'~' guide — suppressed in the TAG MODEL ONLY: excluded here, while <see cref="Finalize"/> →
    ///   <see cref="PromptMarkers.WithoutGuides"/> strips it out of the negative conditioning so the image model never
    ///   sees it. This is how a user stops the predictor forcing a tag WITHOUT negatively conditioning the picture
    ///   (issue #134).</item>
    ///   <item>'!' inert — suppressed in the IMAGE MODEL ONLY, so it is SKIPPED here. '!' means "the predictor doesn't
    ///   deal with this tag", so a negated inert tag is not a tagger exclusion; it still reaches the negative
    ///   conditioning, because <see cref="Finalize"/> strips its marker like any other.</item>
    /// </list></para>
    /// Keys are canonical (lowercased, spaces-&gt;underscores) so they match the ban sets the tag model and RandomArtist
    /// already honour.
    /// </summary>
    public static (HashSet<string> Tags, HashSet<string> Artists) NegativeKeys(string? rawNegative) =>
        TagPromptService.NegativeKeys(rawNegative);

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
        List<string> tokens = [];
        foreach (string name in sampled ?? [])
        {
            string key = Normalize(name);
            if (key.Length == 0 || banned.Contains(key))
            {
                continue;
            }

            tokens.Add((isArtist(key) ? PromptMarkers.ArtistMarker : PromptMarkers.TagMarker) + key);
        }

        return tokens;
    }

    /// <summary>Comma-join <paramref name="segment"/> onto <paramref name="prompt"/>. A prompt the user left with a
    /// trailing separator ("1girl,") must not render as "1girl,, next_tag", so strip any straggling commas/whitespace
    /// off the tail first.</summary>
    public static string Append(string? prompt, string segment)
    {
        string p = (prompt ?? string.Empty).TrimEnd(Separators);
        return p.Length == 0 ? segment : p + ", " + segment;
    }

    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];
}
