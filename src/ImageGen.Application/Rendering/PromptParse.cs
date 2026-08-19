using ImageGen.Application.Prompting.Tags;
using ImageGen.Application.Workflows;

namespace ImageGen.Application.Rendering;

/// <summary>
/// The full parse of an already-RESOLVED (group-free) raw prompt against a model's tagging config, in ONE call — the
/// testable surface of issue #157: for any input it returns exactly what will be sent to the image model AND what will
/// be sent to the tag model, both from the one parse, so they can be pinned together and never diverge. Group handling
/// (<c>{a|b}</c>/<c>{{a|b}}</c>) happens earlier, at enqueue, via <see cref="TagPromptService.Compile"/>.
/// </summary>
/// <param name="ImageModelPrompt">The finalized prompt the image model renders.</param>
/// <param name="TagModelSeed">The seed handed to the tag predictor.</param>
/// <param name="InertKeys">Canonical keys of the '!' inert tags.</param>
/// <param name="GuideKeys">Canonical keys of the '~' guide tags.</param>
/// <param name="Marks">{ canonicalName -&gt; "tag"|"artist" } for the marked, rendered tokens.</param>
public sealed record PromptAnalysis(
    string ImageModelPrompt,
    string TagModelSeed,
    IReadOnlySet<string> InertKeys,
    IReadOnlySet<string> GuideKeys,
    IReadOnlyDictionary<string, string> Marks);

/// <summary>Convenience composition over the tag service for callers and tests that want everything for one resolved
/// prompt at once.</summary>
public static class PromptParse
{
    /// <summary>Everything the two models get for <paramref name="rawPrompt"/>, from the one parse.</summary>
    public static PromptAnalysis Analyze(string? rawPrompt, WorkflowTagging? tagging)
    {
        GeneratedTagGroup g = GeneratedTagGroup.FromResolvedText(rawPrompt ?? string.Empty);
        (string seed, _) = g.ToTagModel(tagging);
        return new PromptAnalysis(g.ToImageModel(tagging), seed,
            TagPromptService.Keys(rawPrompt, TagKind.Inert), TagPromptService.Keys(rawPrompt, TagKind.Guide), g.Marks(tagging));
    }

    /// <summary>The predictor's seed and the keys it must ban for the call, for an already-resolved prompt.</summary>
    public static (string Seed, HashSet<string> SuppressKeys) TagSeed(string? raw, WorkflowTagging tagging) =>
        GeneratedTagGroup.FromResolvedText(raw ?? string.Empty).ToTagModel(tagging);
}
