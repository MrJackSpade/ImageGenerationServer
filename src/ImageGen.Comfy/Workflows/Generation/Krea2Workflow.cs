using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Krea 2's shared parameters: the standard txt2img knobs plus the per-layer conditioning rebalance (the
/// "uncensor" knob) — a global multiplier and the 12 per-layer gains for Krea 2's tapped Qwen3-VL layers. The
/// single-pass <see cref="Krea2Workflow"/> and the two-stage <see cref="Krea2RefineWorkflow"/> both read these.</summary>
public record Krea2Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.RebalanceMultiplier)] public required double Multiplier { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PerLayerWeights)]     public required string PerLayerWeights { get; init; }
}

/// <summary>
/// Shared Krea 2 generation base: the plain txt2img topology with Krea 2's per-layer conditioning rebalance (see
/// <see cref="Krea2Rebalance"/>) spliced in at the base's reserved post-encode node. The rebalance reweights the 12
/// hidden Qwen3-VL layers Krea 2 taps, drowning out the safety / quality-dilution alignment carried there; it operates
/// on the conditioning tensor only, so it is independent of which Krea 2 weight is loaded (RAW or Turbo).
///
/// Both the single-pass <see cref="Krea2Workflow"/> and the two-stage <see cref="Krea2RefineWorkflow"/> (its own
/// <c>Build</c>) derive from this so the rebalance hook is shared.
/// </summary>
public abstract class Krea2Base<TParams> : Txt2ImgWorkflow<TParams> where TParams : Krea2Params
{
    /// <summary>Splice the per-layer conditioning rebalance in at the base's reserved node id "13".</summary>
    protected override Output<Slot.Conditioning> PostEncodePositive(ComfyWorkflowGraph g, Output<Slot.Conditioning> positive, TParams p)
        => Krea2Rebalance.Apply(g, positive, p.Multiplier, p.PerLayerWeights, Nodes.PostEncode);
}

/// <summary>
/// Krea 2 (RAW base) text-to-image. Aesthetic-first open model on the Qwen-Image VAE (Wan 2.1 latent format) with a
/// Qwen3-VL 4B text encoder. fp8 because the bf16 weights are ~26 GB — they don't fit a 24 GB card alongside the
/// encoder. Pure reuse of the txt2img topology (UNETLoader + CLIPLoader type "krea2" + EmptyLatentImage + KSampler);
/// ComfyUI's Krea2 model class bakes the flow shift (1.15) in at load, so no ModelSampling node — its configuration
/// just leaves "auraflow" unset.
///
/// Adds one Krea-2-specific capability: the per-layer conditioning rebalance (see <see cref="Krea2Rebalance"/>),
/// which works for both the RAW base and the Turbo variant (both bind this workflow).
/// </summary>
public sealed class Krea2Workflow : Krea2Base<Krea2Params>
{
    public override string Name => "krea2";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = Txt2ImgWorkflowBase.SharedSchema.Concat(Krea2Rebalance.Schema).ToArray();
}
