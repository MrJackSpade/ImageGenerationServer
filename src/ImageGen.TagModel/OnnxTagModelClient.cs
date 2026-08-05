using ImageGen.Application.Rendering;
using ImageGen.Application.Tags;

namespace ImageGen.TagModel;

/// <summary>
/// <see cref="ITagModelClient"/> served in-process by ONNX Runtime: the tag model reached by a method call rather
/// than an HTTP round-trip to a separate service. It runs the same exported graph either way.
///
/// <para>Inference is serialised behind a lock. The ONNX session is thread-safe, but this is CPU inference on a box
/// whose GPU is busy rendering: letting several full-vocabulary forward passes run at once would compete with the
/// render for cores rather than finish sooner. Requests queue instead, which is what the 110 ms autocomplete debounce
/// already assumes.</para>
/// </summary>
public sealed class OnnxTagModelClient : ITagModelClient, IDisposable
{
    private readonly TagModelBundle _bundle;
    private readonly SuggestEngine _suggest;
    private readonly GenerateEngine _generate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The UI slider's temperature range: 0 (greedy) to 5 (wildest). A value outside it is refused, not clamped.</summary>
    private const double TempMin = 0, TempMax = 5;

    /// <summary>Wrap an already-loaded bundle.</summary>
    public OnnxTagModelClient(TagModelBundle bundle)
    {
        _bundle = bundle;
        _suggest = new SuggestEngine(bundle);
        _generate = new GenerateEngine(bundle);
    }

    /// <summary>
    /// Always true. There is no URL to configure and nothing to be unreachable — if the artifacts were missing the
    /// app would not have started.
    /// </summary>
    public bool Enabled => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagSuggestion>?> QueryAsync(
        string context, string fragment, int limit, CancellationToken ct)
    {
        var contextTags = context.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await _gate.WaitAsync(ct);
        try
        {
            // A limit below 1 is refused, not floored to 1 — an empty ask is the caller's mistake to see, not to have
            // silently turned into a one-result response (the /tags endpoint already rejects it before we get here).
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1.");
            var result = _suggest.Query(contextTags, fragment, limit);
            if (result.Results.Count == 0)
                return null;   // "no match" is a non-failure, and the port says null for it

            return result.Results
                .Select(r => new TagSuggestion(r.Tag, r.P, r.Lift))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="seed"/> is the CONDITIONING tag set, not an RNG seed — the tags the user typed, which the model
    /// grows a prompt around. There is no reproducible draw here: the same request may produce a different prompt each
    /// time, and nothing upstream depends on repeatability.
    /// </remarks>
    public async Task<IReadOnlyList<string>?> GenerateAsync(
        string? seed, double? temperature, IReadOnlyCollection<string>? banned,
        IReadOnlyList<string> allowedTypes, CancellationToken ct)
    {
        var seedTags = NormalizeTags(seed);
        var bannedTags = banned is null ? [] : NormalizeTags(string.Join(',', banned));

        // The caller's list names the types that stay ALLOWED, so it is passed straight through -- including the ones
        // it offers no switch for. Translating or defaulting it here is exactly the drift that would collapse
        // generation to [highres, original].
        var typeMask = TypeMask.FromAllowedNames(allowedTypes);

        // A present temperature outside the slider's [0, 5] is REFUSED, not clamped — a quietly clamped value would
        // render at a temperature the user did not choose. Null legitimately means "unspecified": the model's natural 1.0.
        double temp;
        if (temperature is null)
            temp = 1.0;
        else if (temperature.Value is < TempMin or > TempMax)
            throw new RenderValidationException($"temperature must be between {TempMin} and {TempMax}, but was {temperature.Value}.");
        else
            temp = temperature.Value;

        await _gate.WaitAsync(ct);
        try
        {
            // The seedless path (no user tags — a from-scratch prompt) reaches wider into the tail so the set is more
            // varied; a seeded set stays pinned near the user's context at the default floor.
            var minP = seedTags.Length == 0 ? GenerateEngine.SeedlessMinP : GenerateEngine.DefaultMinP;
            var result = _generate.Generate(
                seedTags, Random.Shared.Next(), temp, bannedTags, typeMask, minP);

            return result.Tags.Count == 0 ? null : result.Tags;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The wire spelling of a tag list, canonicalised to the vocabulary's spelling.
    ///
    /// <para>Each step earns its place: callers send prompt tokens, so <c>@</c>/<c>#</c> markers are still attached,
    /// the user may have typed 'Long Hair', and the vocabulary holds lowercase underscored names. Skip any one of
    /// these and the tag simply fails to resolve — silently, since an unresolvable tag is indistinguishable from one
    /// the model does not know.</para>
    /// </summary>
    private static string[] NormalizeTags(string? csv) =>
        (csv ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.ToLowerInvariant().TrimStart('@', '#').Replace(' ', '_'))
        .Where(t => t.Length > 0)
        .ToArray();

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
