using ImageGen.Application.Workflows;

namespace ImageGen.Application.Prompting.Tags;

/// <summary>
/// The tag service, configured ONCE with a model's tagging rules (does it speak tags/artists, keep the '@', fold
/// underscores). It joins the layers of issue #157:
/// <list type="bullet">
///   <item><see cref="Compile"/> — parse groups + resolve them: one <see cref="GeneratedTagGroup"/> per explode-combo,
///   choices picked. This is the ENQUEUE step (what the client used to do with <c>[a|b]</c>/<c>{a|b}</c>).</item>
///   <item><see cref="ImageModelPrompt"/> / <see cref="Marks"/> / <see cref="TagModelSeed"/> — render an already-resolved
///   (group-free) prompt for this model. This is the RENDER step, per slot.</item>
/// </list>
/// Marker-scoped routing that doesn't depend on the model's rendering rules (the negative exclusion, the inert/guide key
/// sets) is exposed as statics. Every layer is independently unit-testable: text → tree → generated groups → model text.
/// </summary>
public sealed class TagPromptService
{
    private readonly WorkflowTagging? _tagging;

    /// <summary>Configure the service for one model. Null/blank tagging = a prose model: groups still resolve, but the
    /// resolved text is rendered back byte-for-byte with no tag handling.</summary>
    public TagPromptService(WorkflowTagging? tagging) => _tagging = tagging;

    /// <summary>Parse the prompt's <c>[a|b]</c>/<c>{a|b}</c> groups and resolve them into one <see cref="GeneratedTagGroup"/>
    /// per explode-combo (choices picked via <paramref name="pick"/>, defaulting to a real RNG). Call once per copy so
    /// each copy re-rolls its choices, exactly as the client's per-slot expansion did.</summary>
    public static IReadOnlyList<GeneratedTagGroup> Compile(string? raw, Func<int, int>? pick = null) => TagGroup.Parse(raw).Generate(pick);

    /// <summary>Render an already-resolved (group-free) prompt for the image model.</summary>
    public string ImageModelPrompt(string? resolved) => GeneratedTagGroup.FromResolvedText(resolved ?? string.Empty).ToImageModel(_tagging);

    /// <summary>The marks map for an already-resolved prompt.</summary>
    public Dictionary<string, string> Marks(string? resolved) => GeneratedTagGroup.FromResolvedText(resolved ?? string.Empty).Marks(_tagging);

    /// <summary>The predictor seed + suppression keys for an already-resolved prompt.</summary>
    public (string Seed, HashSet<string> SuppressKeys) TagModelSeed(string? resolved) => GeneratedTagGroup.FromResolvedText(resolved ?? string.Empty).ToTagModel(_tagging);

    /// <summary>The canonical tag/artist keys a raw negative asks the TAG PREDICTOR not to sample. '#'/plain/'~' are tag
    /// exclusions; '@' an artist exclusion; '!' inert is image-side only and is skipped (issue #134). Weight-aware.</summary>
    public static (HashSet<string> Tags, HashSet<string> Artists) NegativeKeys(string? rawNegative)
    {
        HashSet<string> tags = new(StringComparer.Ordinal);
        HashSet<string> artists = new(StringComparer.Ordinal);
        foreach (ParsedTag t in TagParser.Parse(rawNegative))
        {
            if (t.Kind == TagKind.Inert || t.Key.Length == 0)
            {
                continue;
            }

            _ = (t.Kind == TagKind.Artist ? artists : tags).Add(t.Key);
        }

        return (tags, artists);
    }

    /// <summary>The canonical keys of the segments of <paramref name="raw"/> marked with <paramref name="kind"/>.</summary>
    public static HashSet<string> Keys(string? raw, TagKind kind) =>
        TagParser.Parse(raw).Where(t => t.Kind == kind && t.Key.Length > 0).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
}
