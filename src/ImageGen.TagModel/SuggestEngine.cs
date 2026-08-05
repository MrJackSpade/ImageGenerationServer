using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.TagModel;

/// <summary>
/// Context-aware tag autocomplete: given the tags already in the prompt and what the user is typing, rank what
/// probably comes next.
///
/// <para>Ranking is by P(tag | set); the percentage shown beside a suggestion is the calibrated
/// sigmoid(a·logit + b), which is a different number from the softmax and is why the logits are kept rather than
/// normalised away. Scoring runs UNCONDITIONED (all types allowed) on purpose: this path answers what the user is
/// typing, and typing 'sakimi' should still find the artist even though generation never emits one.</para>
///
/// <para>There is no candidate-only fast path (projecting the pooled query against just the candidate decoder rows,
/// skipping the full projection): those raw decoder weights live in the PyTorch checkpoint, not the exported graph,
/// and shipping a second 240 MB tensor to save a matmul the model already performs on every generation step is a poor
/// trade. The full forward yields identical logits.</para>
/// </summary>
public sealed class SuggestEngine(TagModelBundle bundle)
{
    private readonly TagModelBundle _bundle = bundle;

    /// <summary>One ranked suggestion: the tag, its shown probability, and its lift over the tag's base rate.</summary>
    public readonly record struct Suggestion(string Tag, double P, double? Lift);

    /// <summary>How to rank.</summary>
    public enum Mode
    {
        /// <summary>By P(tag | set) — the genuinely most probable completions.</summary>
        Likely = 0,

        /// <summary>By lift, P(tag|set)/P(tag) — what this set makes unusually likely rather than what is common.</summary>
        Distinctive = 1,
    }

    /// <summary>
    /// Rank up to <paramref name="limit"/> completions.
    /// </summary>
    /// <param name="contextTags">Tags already in the prompt. Unknown names are ignored (and reported).</param>
    /// <param name="fragment">What the user is typing; matched as a case-insensitive substring. May be empty.</param>
    /// <param name="limit">Maximum results. Honoured on every path.</param>
    /// <param name="mode">Ranking mode.</param>
    [AllowMagicStrings("exception message")]
    public SuggestResult Query(
        IReadOnlyCollection<string> contextTags, string fragment, int limit, Mode mode = Mode.Likely)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1.");

        var vocab = _bundle.Vocab;
        var query = fragment.Trim().ToLowerInvariant();

        var present = new List<int>();
        var presentSet = new HashSet<int>();
        var unknown = new List<string>();
        foreach (var raw in contextTags)
        {
            var tag = raw.Trim();
            if (tag.Length == 0) continue;
            if (vocab.IdOf(tag) is int id)
            {
                if (presentSet.Add(id)) present.Add(id);
            }
            else unknown.Add(tag);
        }

        float[] conditional;
        float[] display;
        if (present.Count == 0)
        {
            // NO forward pass on an empty context. With nothing to condition on there is nothing for the model to
            // say, so the answer is the corpus base rate -- which also makes the first keystroke in an empty prompt
            // box free instead of a full-vocabulary inference. Ranking here is by raw popularity, which is why '1girl'
            // leads: it is the most common tag in the corpus, not the model's opinion.
            conditional = _bundle.Vocab.Marginal;
            display = _bundle.Vocab.Marginal;
        }
        else
        {
            // Scoring is deliberately unconditioned by TYPE: suggest ranks every category, artists included, because
            // this path answers what the user is typing rather than what generation may emit.
            var (logits, _) = _bundle.Session.Forward(present, TypeMask.AllTypes);
            conditional = Softmax(logits);
            display = Display(logits, conditional);
        }

        // 'distinctive' divides by the base rate, which without a floor promotes tags seen a handful of times in
        // millions of images -- statistically striking, useless as a suggestion.
        var score = new float[conditional.Length];
        if (present.Count > 0 && mode == Mode.Distinctive)
        {
            for (var i = 0; i < score.Length; i++)
                score[i] = vocab.Marginal[i] >= 5e-5f ? conditional[i] / Math.Max(vocab.Marginal[i], 1e-9f) : 0f;
        }
        else
        {
            Array.Copy(conditional, score, score.Length);
        }

        var junk = _bundle.JunkIds.ToHashSet();
        List<int> ordered;
        int total;

        if (query.Length > 0)
        {
            var candidates = new List<int>();
            for (var i = 0; i < vocab.Count; i++)
                if (!presentSet.Contains(i) && !junk.Contains(i) && vocab.Lowercase[i].Contains(query, StringComparison.Ordinal))
                    candidates.Add(i);
            candidates.Sort((x, y) => score[y].CompareTo(score[x]));
            total = candidates.Count;
            ordered = candidates.Take(limit).ToList();
        }
        else
        {
            // Excluded rather than filtered afterwards, so a full page of results still comes back when the top of the
            // distribution is tags the prompt already has.
            var ranking = new float[score.Length];
            Array.Copy(score, ranking, score.Length);
            foreach (var id in presentSet) ranking[id] = -1f;
            foreach (var id in junk) ranking[id] = -1f;

            ordered = TopK(ranking, limit);
            total = ordered.Count;
        }

        var results = new List<Suggestion>(ordered.Count);
        foreach (var id in ordered)
        {
            // Marginal is eps-floored positive by construction (TagVocab clamps it to [eps, 1-eps]), so the base rate
            // is never zero here — no divide guard, which would only paper over a corrupt bundle.
            var baseRate = vocab.Marginal[id];
            results.Add(new Suggestion(
                vocab.Tags[id],
                display[id],
                conditional[id] / baseRate));
        }

        return new SuggestResult(results, total, unknown);
    }

    /// <summary>The ranked suggestions, how many candidates matched in total, and any context tags not in the vocabulary.</summary>
    public sealed record SuggestResult(
        IReadOnlyList<Suggestion> Results, int Total, IReadOnlyList<string> UnknownContextTags);

    /// <summary>
    /// The percentage to SHOW, which is not the ranking score. The calibrated sigmoid turns a raw ranking logit into
    /// something that behaves like a real probability; without a fit, the softmax stands in.
    /// </summary>
    private float[] Display(float[] logits, float[] conditional)
    {
        if (_bundle.Calibration is null)
            return conditional;

        var (a, b) = (_bundle.Calibration.A, _bundle.Calibration.B);
        var display = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++)
        {
            // -inf marks an unemittable tag; it has no calibrated probability, and exp() of it would be a NaN factory.
            display[i] = float.IsNegativeInfinity(logits[i])
                ? 0f
                : (float)(1.0 / (1.0 + Math.Exp(-(a * logits[i] + b))));
        }
        return display;
    }

    /// <summary>Softmax over the vocabulary, shifted by the max so the exponentials cannot overflow.</summary>
    internal static float[] Softmax(float[] logits)
    {
        var max = float.NegativeInfinity;
        foreach (var v in logits)
            if (v > max) max = v;

        var result = new float[logits.Length];
        if (float.IsNegativeInfinity(max))
            return result;   // every tag unemittable: a uniform zero, not a division by zero

        double sum = 0;
        for (var i = 0; i < logits.Length; i++)
        {
            var e = float.IsNegativeInfinity(logits[i]) ? 0.0 : Math.Exp(logits[i] - max);
            result[i] = (float)e;
            sum += e;
        }
        // sum >= 1 here: `max` is finite (the all-unemittable case returned above), so the max element contributes
        // exp(0) = 1 — there is no divide-by-zero to guard against.
        for (var i = 0; i < result.Length; i++)
            result[i] = (float)(result[i] / sum);
        return result;
    }

    /// <summary>Indices of the <paramref name="k"/> largest values, descending. A partial selection, not a full sort.</summary>
    private static List<int> TopK(float[] values, int k)
    {
        k = Math.Min(k, values.Length);
        var best = new List<int>(k);
        var taken = new bool[values.Length];
        for (var n = 0; n < k; n++)
        {
            var bestIndex = -1;
            var bestValue = float.NegativeInfinity;
            for (var i = 0; i < values.Length; i++)
            {
                if (taken[i] || values[i] <= bestValue) continue;
                bestValue = values[i];
                bestIndex = i;
            }
            if (bestIndex < 0) break;
            taken[bestIndex] = true;
            best.Add(bestIndex);
        }
        return best;
    }
}
