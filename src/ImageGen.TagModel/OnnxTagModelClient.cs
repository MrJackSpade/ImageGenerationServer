using ImageGen.Application.Tags;

namespace ImageGen.TagModel;

/// <summary>
/// <see cref="ITagModelClient"/> served in-process by ONNX Runtime, replacing the HTTP client that talked to a
/// separate Python service on port 8000.
///
/// <para>That service is gone: with it goes a 815 MB virtual environment, a scheduled task that had to be restarted
/// with elevation to pick up a model change, a lazily-built ONNX cache that went stale after a checkpoint swap, and a
/// second process holding both PyTorch and ONNX Runtime resident (~2 GB, against ~900 MB now). What remains is the
/// same model — literally the same exported graph — reached by a method call.</para>
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
        var contextTags = (context ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await _gate.WaitAsync(ct);
        try
        {
            var result = _suggest.Query(contextTags, fragment ?? "", Math.Max(1, limit));
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
    /// grows a prompt around. There is no reproducible draw here and never was: the Python server sampled from the
    /// process-wide RNG, so the same request produced a different prompt every time. Nothing upstream depends on
    /// repeatability, so the behaviour is preserved as-is rather than invented.
    /// </remarks>
    public async Task<IReadOnlyList<string>?> GenerateAsync(
        string? seed, double? temperature, IReadOnlyCollection<string>? banned,
        IReadOnlyList<string> allowedTypes, CancellationToken ct)
    {
        var seedTags = NormalizeTags(seed);
        var bannedTags = banned is null ? [] : NormalizeTags(string.Join(',', banned));

        // The caller's list names the types that stay ALLOWED, so it is passed straight through -- including the ones
        // it offers no switch for. Translating or defaulting it here is exactly the drift that once collapsed
        // generation to [highres, original].
        var typeMask = TypeMask.FromAllowedNames(allowedTypes);

        // Clamped as the HTTP client used to clamp it, so the slider's range means the same thing it always did.
        var temp = temperature is null ? 1.0 : Math.Clamp(temperature.Value, 0, 5);

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
    /// <para>Character-for-character what the Python server's <c>_norm</c> did, and each step earns its place: callers
    /// send prompt tokens, so <c>@</c>/<c>#</c> markers are still attached, the user may have typed 'Long Hair', and
    /// the vocabulary holds lowercase underscored names. Skip any one of these and the tag simply fails to resolve —
    /// silently, since an unresolvable tag is indistinguishable from one the model does not know.</para>
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
