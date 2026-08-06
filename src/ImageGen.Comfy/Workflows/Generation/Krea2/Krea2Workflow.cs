namespace ImageGen.Comfy.Generation.Krea2;

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
    private static readonly IReadOnlyList<ParamSpec> _schema = [.. Txt2ImgWorkflowBase.SharedSchema, .. Krea2Rebalance.Schema];
}