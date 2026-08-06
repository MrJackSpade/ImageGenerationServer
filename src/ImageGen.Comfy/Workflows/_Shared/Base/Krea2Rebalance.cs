using System.Globalization;

namespace ImageGen.Comfy;

/// <summary>
/// Krea 2's per-layer conditioning rebalance (the "uncensor" knob), shared by every graph that samples a Krea 2
/// weight: the txt2img base (<see cref="Krea2Workflow"/>), the two-stage <see cref="Krea2RefineWorkflow"/>, and the
/// Turbo edit redraw (<see cref="Krea2RedrawWorkflow"/>).
///
/// Krea 2 taps 12 hidden layers of its Qwen3-VL encoder and concatenates them into the conditioning; reweighting
/// those layers (and globally scaling) drowns out the safety / quality-dilution alignment carried in the deep
/// semantic layers. Implemented by splicing the ConditioningKrea2Rebalance node
/// (nova452/ComfyUI-Conditioning-Rebalance) between the positive text-encode and the sampler. It operates on the
/// conditioning tensor only, so it is independent of the quant, of which Krea 2 weight is loaded (RAW or Turbo), and
/// of the graph topology around it — hence a free function over the graph rather than a base-class hook.
///
/// Neutral knobs (multiplier 1.0 + all-ones weights) skip the node entirely, leaving the graph byte-identical to
/// plain Krea 2.
/// </summary>
public static class Krea2Rebalance
{
    /// <summary>The two knobs, concatenated into every Krea 2 workflow's schema.</summary>
    public static readonly IReadOnlyList<ParamSpec> Schema =
    [
        new() { Key = WorkflowParamKeys.RebalanceMultiplier, Type = ParamType.Double, Min = 1.0, Max = 8.0,
                Label = "Uncensor strength",
                Help = "Global conditioning multiplier on Krea 2's per-layer rebalance. 1.0 = off. ~2–4 progressively "
                     + "bypasses the model's safety / quality-dilution alignment; higher is stronger but can destabilize the image." },
        new() { Key = WorkflowParamKeys.PerLayerWeights, Type = ParamType.String,
                Label = "Per-layer weights",
                Help = "12 comma-separated gains for Krea 2's tapped Qwen3-VL layers. All 1.0 = neutral. Uncensor preset: " + Krea2RebalanceWeights.UncensorWeights },
    ];

    /// <summary>Splice the rebalance node at <paramref name="nodeId"/> between the positive conditioning and the
    /// sampler when either knob is non-neutral; otherwise return <paramref name="positive"/> untouched (no node
    /// emitted). The caller owns <paramref name="nodeId"/> because the surrounding topologies differ: the txt2img
    /// base reserves "13", while the edit graphs already use "13"/"14" for their text-encodes.</summary>
    public static Output<Slot.Conditioning> Apply(ComfyWorkflowGraph g, Output<Slot.Conditioning> positive,
        double multiplier, string perLayerWeights, string nodeId)
    {
        if (!IsActive(multiplier, perLayerWeights))
        {
            return positive;
        }

        g[nodeId] = new ConditioningKrea2Rebalance { Conditioning = positive, Multiplier = multiplier, PerLayerWeights = perLayerWeights };
        return ConditioningKrea2Rebalance.Out(nodeId);
    }

    /// <summary>Live when the user has moved either knob off neutral: multiplier ≠ 1.0, or any per-layer weight ≠ 1.0.
    /// Both neutral (the schema defaults) keeps the emitted graph byte-identical to plain Krea 2.</summary>
    public static bool IsActive(double multiplier, string perLayerWeights)
    {
        if (Math.Abs(multiplier - 1.0) > 1e-6)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(perLayerWeights))
        {
            foreach (string part in perLayerWeights.Split(','))
            {
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && Math.Abs(d - 1.0) > 1e-6)
                {
                    return true;
                }
            }
        }

        return false;
    }
}