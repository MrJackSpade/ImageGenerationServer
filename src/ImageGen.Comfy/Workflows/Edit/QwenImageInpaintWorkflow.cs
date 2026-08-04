namespace ImageGen.Comfy;

/// <summary>
/// Masked INPAINT on base Qwen-Image + the InstantX inpainting ControlNet. The region to regenerate arrives as a
/// separate white-on-black mask upload (<c>inputs.MaskImageName</c>, painted in the edit UI), falling back to the
/// source image's alpha when none was supplied — same contract as <see cref="AnimaInpaintWorkflow"/>.
/// </summary>
public sealed class QwenImageInpaintWorkflow : QwenInstantXInpaintBase
{
    public override string Name => "qwen-image-inpaint";

    /// <summary>The ControlNet supplies the fill conditioning, so a full denoise inside the mask is correct — the
    /// surrounding context comes from the control image, not from partially-preserved noise.</summary>
    protected override double DefaultDenoise => 1.0;

    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema = ControlNetSchema.Concat(new ParamSpec[]
    {
        new() { Key = "denoise", Type = ParamType.Double, Min = 0.2, Max = 1.0, Step = 0.01, Label = "Change amount" },
        // 16 = 2σ, same shape as outpaint: with the clamp holding the painted region at a hard 1, grow only places
        // the one-sided ramp's midpoint 16px OUTSIDE the painted region. The painted pixels are always fully
        // replaced; the ramp is the crossfade band over the surrounding original.
        new() { Key = "mask_grow", Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    }).ToArray();

    protected override void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask)
    {
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            wf["11"] = ComfyGraph.Node("LoadImageMask", new { image = inputs.MaskImageName, channel = "red" });
            rawMask = ComfyGraph.Ref("11", 0);
        }
        else rawMask = ComfyGraph.Ref("10", 1);   // source alpha

        // Same pre-fill as outpaint, for the same reason: this app's inpaint masks cover flat WHITE space to be
        // filled — non-scene content under the fill region, grey's twin. Two chained blurs (σ10 each ≈ σ14) pull
        // surrounding colors across the hole's boundary band, and the masked composite swaps that in for the hole
        // content. The hole's deep interior may stay whitish — irrelevant: the mask holds a hard 1 there (never
        // re-injected, never composited) and the ControlNet apply zeroes it out of the control image. What matters
        // is that the ~8-16px boundary band — the latent cells straddling the join, and everything a soft edge can
        // blend — carries scene tone instead of white.
        wf["21"] = ComfyGraph.Node("ImageBlur", new { image = ComfyGraph.Ref("10", 0), blur_radius = 31, sigma = 10.0 });
        wf["22"] = ComfyGraph.Node("ImageBlur", new { image = ComfyGraph.Ref("21", 0), blur_radius = 31, sigma = 10.0 });
        wf["23"] = ComfyGraph.Node("ImageCompositeMasked", new
        {
            destination = ComfyGraph.Ref("10", 0),
            source = ComfyGraph.Ref("22", 0),
            x = 0, y = 0, resize_source = false,
            mask = rawMask,
        });
        image = ComfyGraph.Ref("23", 0);
    }
}
