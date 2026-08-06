using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.QwenImageInpaint;

/// <summary>
/// Masked INPAINT on base Qwen-Image + the InstantX inpainting ControlNet. The region to regenerate arrives as a
/// separate white-on-black mask upload (<c>inputs.MaskImageName</c>, painted in the edit UI), falling back to the
/// source image's alpha when none was supplied — same contract as <see cref="AnimaInpaintWorkflow"/>.
/// </summary>
public sealed class QwenImageInpaintWorkflow : QwenInstantXInpaintBase<QwenImageInpaintParams>
{
    public override string Name => "qwen-image-inpaint";

    /// <summary>The ControlNet supplies the fill conditioning, so a full denoise inside the mask is correct — the
    /// surrounding context comes from the control image, not from partially-preserved noise.</summary>
    protected override double DefaultDenoise => 1.0;

    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema = ControlNetSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise, Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Change amount" },
        // 16 = 2σ, same shape as outpaint: with the clamp holding the painted region at a hard 1, grow only places
        // the one-sided ramp's midpoint 16px OUTSIDE the painted region. The painted pixels are always fully
        // replaced; the ramp is the crossfade band over the surrounding original.
        new() { Key = WorkflowParamKeys.MaskGrow, Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    }).ToArray();

    protected override void ResolveCanvas(ComfyWorkflowGraph g, QwenInpaintParams p, WorkflowInputs inputs,
        out Output<Slot.Image> image, out Output<Slot.Mask> rawMask)
    {
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            g[Nodes.MaskLoad] = new LoadImageMask { Image = inputs.MaskImageName, Channel = ComfyWidgets.MaskChannel.Red };
            rawMask = LoadImageMask.Out(Nodes.MaskLoad);
        }
        else rawMask = LoadImage.MaskOut(EditNodes.Source);   // source alpha

        // Same pre-fill as outpaint, for the same reason: this app's inpaint masks cover flat WHITE space to be
        // filled — non-scene content under the fill region, grey's twin. Two chained blurs (σ10 each ≈ σ14) pull
        // surrounding colors across the hole's boundary band, and the masked composite swaps that in for the hole
        // content. The hole's deep interior may stay whitish — irrelevant: the mask holds a hard 1 there (never
        // re-injected, never composited) and the ControlNet apply zeroes it out of the control image. What matters
        // is that the ~8-16px boundary band — the latent cells straddling the join, and everything a soft edge can
        // blend — carries scene tone instead of white.
        g[Nodes.PrefillBlur1] = new ImageBlur { Image = LoadImage.ImageOut(EditNodes.Source), BlurRadius = 31, Sigma = 10.0 };
        g[Nodes.PrefillBlur2] = new ImageBlur { Image = ImageBlur.Out(Nodes.PrefillBlur1), BlurRadius = 31, Sigma = 10.0 };
        g[Nodes.PrefillComposite] = new ImageCompositeMasked
        {
            Destination = LoadImage.ImageOut(EditNodes.Source),
            Source = ImageBlur.Out(Nodes.PrefillBlur2),
            X = 0,
            Y = 0,
            ResizeSource = false,
            Mask = rawMask,
        };
        image = ImageCompositeMasked.Out(Nodes.PrefillComposite);
    }
}
