namespace ImageGen.TagModel;

/// <summary>
/// Grows a whole prompt one sampled tag at a time, conditioned on the set so far, stopping when the model's
/// completeness head says the set is finished. Ported from <c>_generate_set</c> / <c>/api/random_prompt</c>.
///
/// <para>There is no imposed length: <see cref="MaxSteps"/> is a runaway cap, not a target, and a set that hits it is
/// reported as truncated rather than complete. That distinction matters — a capped set and a finished set used to be
/// indistinguishable in the response, so a truncated prompt read as one the model considered done.</para>
/// </summary>
public sealed class GenerateEngine(TagModelBundle bundle)
{
    /// <summary>P(complete) above which the stop head ends generation.</summary>
    public const double StopThreshold = 0.5;

    /// <summary>Runaway cap. One definition, used both as the limit and as the number reported alongside a truncation.</summary>
    public const int MaxSteps = 60;

    /// <summary>Default tail-reach for sampling. Matches the Python server's <c>min_p</c> default.</summary>
    public const double DefaultMinP = 0.01;

    private readonly TagModelBundle _bundle = bundle;

    /// <summary>Why generation ended.</summary>
    public enum StopReason
    {
        /// <summary>The completeness head fired — the set looks finished. The normal outcome.</summary>
        Complete = 0,

        /// <summary>Nothing was left to sample: masking emptied the distribution.</summary>
        Exhausted = 1,

        /// <summary>The safety cap truncated the set. The result is NOT a complete thought.</summary>
        MaxSteps = 2,
    }

    /// <summary>The tags generated (excluding the seed), and why it stopped.</summary>
    public sealed record Result(IReadOnlyList<string> Tags, StopReason Reason);

    /// <summary>
    /// Generate a set.
    /// </summary>
    /// <param name="seedTags">The user's own tags, conditioned on but never echoed back.</param>
    /// <param name="seed">RNG seed, for reproducibility within this implementation.</param>
    /// <param name="temperature">0 = greedy. Above 0, redistributes preference within the pinned support.</param>
    /// <param name="bannedTags">Tags to suppress during sampling, so the set completes around them.</param>
    /// <param name="typeMask">Which categories may be emitted; see <see cref="TypeMask"/>.</param>
    /// <param name="minP">Support floor as a fraction of the peak, applied before tempering.</param>
    public Result Generate(
        IReadOnlyCollection<string> seedTags,
        int seed,
        double temperature = 1.0,
        IReadOnlyCollection<string>? bannedTags = null,
        int typeMask = TypeMask.NoArtist,
        double minP = DefaultMinP)
    {
        var vocab = _bundle.Vocab;
        var rng = new Random(seed);

        var current = new List<int>();
        var seen = new HashSet<int>();
        foreach (var raw in seedTags)
        {
            var id = vocab.IdOf(raw.Trim());
            if (id >= 0 && seen.Add(id)) current.Add(id);
        }
        var seedIds = new HashSet<int>(current);

        var banned = new HashSet<int>();
        if (bannedTags is not null)
            foreach (var raw in bannedTags)
            {
                var id = vocab.IdOf(raw.Trim());
                if (id >= 0) banned.Add(id);
            }

        // Categories the caller switched off are zeroed every step. The mask ALSO conditions the model (above), and
        // both are needed: conditioning makes the stop head judge completeness by the right standard, zeroing enforces
        // the exclusion exactly. With only the zeroing, the set comes back one tag short instead of completing to a
        // real alternative.
        var suppressed = new List<int>();
        for (var id = 0; id < vocab.Count; id++)
            if (!TypeMask.Allows(typeMask, vocab.Types[id]))
                suppressed.Add(id);

        var reason = StopReason.MaxSteps;

        for (var step = 0; step < MaxSteps; step++)
        {
            float[] probabilities;
            if (current.Count > 0)
            {
                var (logits, completenessLogit) = _bundle.Session.Forward(current, typeMask);
                var stopP = 1.0 / (1.0 + Math.Exp(-completenessLogit));
                if (stopP > StopThreshold)
                {
                    reason = StopReason.Complete;
                    break;
                }
                probabilities = SuggestEngine.Softmax(logits);
            }
            else
            {
                // Empty seed: start from corpus base rates rather than an unconditioned forward pass.
                probabilities = (float[])vocab.Marginal.Clone();
            }

            var p = probabilities;
            foreach (var id in current) p[id] = 0f;
            foreach (var id in _bundle.JunkIds) p[id] = 0f;
            foreach (var id in suppressed) p[id] = 0f;
            foreach (var id in banned) p[id] = 0f;

            if (temperature <= 0)
            {
                // Greedy. Also avoids the 1/temperature division, and is what temperature 0 means on the UI slider.
                var (bestId, bestP) = ArgMax(p);
                if (bestP <= 0)
                {
                    reason = StopReason.Exhausted;
                    break;
                }
                current.Add(bestId);
                continue;
            }

            // SUPPORT-PINNED SAMPLING. min_p truncates the temperature-1 distribution BEFORE tempering, so which tags
            // MAY be sampled does not change with temperature. Rarity and incompatibility both read as small p, and
            // truncating after tempering erases that distinction: high temperature then admits contradictory tags, the
            // stop head correctly refuses to call those sets complete, and length runs away. Pinned, temperature only
            // redistributes preference among tags the model already finds plausible.
            if (minP > 0)
            {
                var (_, peak) = ArgMax(p);
                var floor = (float)(minP * peak);
                for (var i = 0; i < p.Length; i++)
                    if (p[i] < floor) p[i] = 0f;
            }

            if (Math.Abs(temperature - 1.0) > double.Epsilon)
            {
                var exponent = 1.0 / temperature;
                for (var i = 0; i < p.Length; i++)
                    if (p[i] > 0) p[i] = (float)Math.Pow(p[i], exponent);
            }

            var sampled = SampleIndex(p, rng);
            if (sampled < 0)
            {
                reason = StopReason.Exhausted;
                break;
            }
            current.Add(sampled);
        }

        // Only what was generated: the caller keeps its own prompt verbatim and appends to it.
        var tags = current.Where(id => !seedIds.Contains(id)).Select(id => vocab.Tags[id]).ToList();
        return new Result(tags, reason);
    }

    private static (int Index, float Value) ArgMax(float[] values)
    {
        var index = -1;
        var best = 0f;
        for (var i = 0; i < values.Length; i++)
            if (values[i] > best) { best = values[i]; index = i; }
        return (index, best);
    }

    /// <summary>
    /// One draw from unnormalised weights, by inverse CDF.
    ///
    /// <para>Accumulating in double and comparing against a scaled target avoids the failure mode of normalising
    /// first: over ~639k float weights the normalised sum drifts from 1, and a target drawn from [0,1) can then fall
    /// past the last bucket and select nothing. Returns -1 only when the weights really are all zero.</para>
    /// </summary>
    private static int SampleIndex(float[] weights, Random rng)
    {
        double total = 0;
        foreach (var w in weights)
            if (w > 0) total += w;
        if (total <= 0) return -1;

        var target = rng.NextDouble() * total;
        double running = 0;
        var lastPositive = -1;
        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] <= 0) continue;
            lastPositive = i;
            running += weights[i];
            if (running >= target) return i;
        }
        return lastPositive;   // floating-point shortfall: the final bucket is the honest answer
    }
}
