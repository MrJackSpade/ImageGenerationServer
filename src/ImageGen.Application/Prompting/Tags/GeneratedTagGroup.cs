using ImageGen.Application.Workflows;
using ImageGen.Domain;

namespace ImageGen.Application.Prompting.Tags;

/// <summary>
/// A fully-RESOLVED prompt: one explode-combo with its choices picked, parsed into strongly-typed tags. It is the unit
/// a single render produces — <see cref="ToImageModel"/> gives the image model's prompt, <see cref="ToTagModel"/> gives
/// the predictor's seed + exclusion, both derived from the same tags so they cannot diverge. <see cref="RawResolved"/>
/// is the resolved marker-form text: it is what gets recorded with the image, and reloading it (no groups left) yields
/// the same picture. Rendering is settings-driven so the SAME resolved tags render correctly for any model.
/// </summary>
public sealed class GeneratedTagGroup
{
    /// <summary>The resolved marker-form prompt (groups gone, choices picked) — recorded and reload-safe.</summary>
    public string RawResolved { get; }

    /// <summary>The resolved prompt's tags, in order.</summary>
    public IReadOnlyList<ParsedTag> Tags { get; }

    private GeneratedTagGroup(string rawResolved, IReadOnlyList<ParsedTag> tags)
    {
        RawResolved = rawResolved;
        Tags = tags;
    }

    /// <summary>Parse an already-resolved (group-free) marker prompt into a group.</summary>
    public static GeneratedTagGroup FromResolvedText(string resolved) => new(resolved, TagParser.Parse(resolved));

    /// <summary>The prompt the IMAGE MODEL renders for <paramref name="tg"/>: '~' guides removed, markers stripped ('@'
    /// kept when the model wants it), underscores folded per the model, weight preserved. A prose model gets the
    /// resolved text back unchanged.</summary>
    public string ToImageModel(WorkflowTagging? tg)
    {
        if (tg is not { } m || !(m.Tags || m.Artists))
        {
            return RawResolved;
        }

        return string.Join(Tokens.SegmentSeparator, Tags.Select(t => RenderImage(t, m)).OfType<string>());
    }

    /// <summary>The predictor's seed (the finalized positive with '~' guides kept as tags and '@' artists + '!' inert
    /// subtracted) and the keys it must ban for the call (inert ∪ guide). Empty seed for a prose model.</summary>
    public (string Seed, HashSet<string> SuppressKeys) ToTagModel(WorkflowTagging? tg)
    {
        HashSet<string> suppress = KeysOfKind(TagKind.Inert);
        suppress.UnionWith(KeysOfKind(TagKind.Guide));
        if (tg is not { } m || !(m.Tags || m.Artists))
        {
            return (string.Empty, suppress);
        }

        string seed = string.Join(Tokens.SegmentSeparator, Tags
            .Where(t => t.Kind is not (TagKind.Artist or TagKind.Inert) && t.Key.Length > 0)
            .Select(t => RenderSeed(t, m)));
        return (seed, suppress);
    }

    /// <summary>{ canonicalName -&gt; "tag"|"artist" } for the marked, rendered tokens (inert marks as a tag; guides and
    /// plain words don't mark). Empty for a prose model.</summary>
    public Dictionary<string, string> Marks(WorkflowTagging? tg)
    {
        Dictionary<string, string> marks = new(StringComparer.Ordinal);
        if (tg is null || !(tg.Tags || tg.Artists))
        {
            return marks;
        }

        foreach (ParsedTag t in Tags)
        {
            if (t.Key.Length == 0)
            {
                continue;
            }

            if (t.Kind is TagKind.Tag or TagKind.Inert)
            {
                marks[t.Key] = TokenKinds.Tag;
            }
            else if (t.Kind == TagKind.Artist)
            {
                marks[t.Key] = TokenKinds.Artist;
            }
        }

        return marks;
    }

    private static string? RenderImage(ParsedTag t, WorkflowTagging tg)
    {
        if (t.Kind == TagKind.Guide || t.Key.Length == 0)
        {
            return null;   // guides never render; a bare marker is nothing
        }

        string body = Fold(t.BaseText, tg);
        if (t.Kind == TagKind.Artist && tg.KeepArtistMarker)
        {
            body = PromptMarkers.ArtistMarker + body;
        }

        return t.Emphasis.Wrap(body);
    }

    private static string RenderSeed(ParsedTag t, WorkflowTagging tg) => t.Emphasis.Wrap(Fold(t.BaseText, tg));

    private static string Fold(string baseText, WorkflowTagging tg) =>
        tg.UnderscoresToSpaces && !baseText.StartsWith(Tokens.ScorePrefix, StringComparison.Ordinal) ? baseText.Replace('_', ' ') : baseText;

    private HashSet<string> KeysOfKind(TagKind kind) =>
        Tags.Where(t => t.Kind == kind && t.Key.Length > 0).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Literal tokens used while reassembling a resolved prompt.</summary>
    private static class Tokens
    {
        /// <summary>The tag separator the reassembled prompt is joined on.</summary>
        public const string SegmentSeparator = ", ";

        /// <summary>The quality-score tag prefix (<c>score_9</c>, …) whose underscores are kept as-is.</summary>
        public const string ScorePrefix = "score_";
    }
}
