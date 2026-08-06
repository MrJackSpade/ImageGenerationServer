namespace ImageGen.Comfy;

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
