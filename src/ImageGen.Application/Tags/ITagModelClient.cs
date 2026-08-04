namespace ImageGen.Application.Tags;

/// <summary>
/// Client for the external Python tag-suggestion model: ranks candidate tags by P(tag | current prompt) and
/// generates whole random tag-set prompts. A transport/HTTP failure throws (no silent fallback); only the genuine
/// non-failure cases (disabled, or an empty result) return null. Implemented by an adapter; the application depends
/// only on this port.
/// </summary>
public interface ITagModelClient
{
    /// <summary>Whether a model URL is configured (false = disabled; callers fall back to count ranking).</summary>
    bool Enabled { get; }

    /// <summary>Model-ranked suggestions for fragment <paramref name="fragment"/> given comma-separated context tags
    /// <paramref name="context"/>. Null when disabled or no match; throws on transport/HTTP failure.</summary>
    Task<IReadOnlyList<TagSuggestion>?> QueryAsync(string context, string fragment, int limit, CancellationToken ct);

    /// <summary>Generate a random tag-set prompt seeded by <paramref name="seed"/> (comma-separated; empty = fully
    /// random). <paramref name="temperature"/> is the sampling temperature (null = model default); <paramref name="banned"/>
    /// tags are suppressed during sampling. <paramref name="allowedTypes"/> is the generation mask — the tag types the
    /// model may emit (see <see cref="GenerationTagTypes"/>), stated on every call so the model's own default can never
    /// silently stand in for the user's choice. Null only on an empty generation; throws on transport/HTTP failure.</summary>
    Task<IReadOnlyList<string>?> GenerateAsync(string? seed, double? temperature, IReadOnlyCollection<string>? banned,
                                               IReadOnlyList<string> allowedTypes, CancellationToken ct);
}
