using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.QwenImageOutpaint;

/// <summary>
/// OUTPAINT on base Qwen-Image + the InstantX inpainting ControlNet. <c>ImagePadForOutpaint</c> extends the canvas by
/// the caller's per-side pads (source-native px) and returns the enlarged image plus a mask marking the new border;
/// the ControlNet then conditions the fill on the known pixels so the border continues the existing structure.
/// Pads are the only override the outpaint UI sends (see <c>edit.js</c>).
/// </summary>
public sealed class QwenImageOutpaintWorkflow : QwenInstantXInpaintBase<QwenImageOutpaintParams>
{
    public override string Name => "qwen-image-outpaint";
    public override WorkflowKind Kind => WorkflowKind.Outpaint;

    /// <summary>Full denoise. A lower denoise (even 0.9, "locking tone to the pre-fill scaffold") FAILS the other
    /// way: under the AuraFlow-shifted schedule even a 0.1 denoise reduction weights the init so heavily that the pad
    /// comes back as the blur scaffold nearly verbatim (stretched-railing smear and all). At 1.0 the panels are
    /// fully generated and the scaffold still does its real jobs — every soft edge blends scene tone, never grey,
    /// and the boundary latent cells encode scene colors.</summary>
    protected override double DefaultDenoise => 1.0;

    /// <summary>The canvas the ceiling applies to is the PADDED one — outpainting is what actually grows the frame
    /// past the model's comfortable range, so measuring the unpadded source would let the real canvas sail past it.</summary>
    protected override (int W, int H) CanvasSize(QwenInpaintParams p, WorkflowInputs inputs)
    {
        _ = Ensure.GreaterThanZero(inputs.SourceWidth);
        _ = Ensure.GreaterThanZero(inputs.SourceHeight);
        return (inputs.SourceWidth + p.PadLeft + p.PadRight,
                inputs.SourceHeight + p.PadTop + p.PadBottom);
    }

    /// <summary>ETA follows the padded canvas after it is normalized to the same native MP budget as the graph.</summary>
    protected override (int Width, int Height) EtaRenderSize(
        QwenImageOutpaintParams p,
        ResolvedRequirements req,
        int sourceWidth,
        int sourceHeight) =>
        EditWorkingResolution.Resolve(
            sourceWidth + p.PadLeft + p.PadRight,
            sourceHeight + p.PadTop + p.PadBottom,
            maxDimension: p.MaxDimension);

    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema =
    [
        .. ControlNetSchema,
        new() { Key = WorkflowParamKeys.Denoise,    Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Fill strength" },
        new() { Key = WorkflowParamKeys.PadLeft,   Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,    Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,  Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        new() { Key = WorkflowParamKeys.MaskGrow, Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    ];

    protected override void ResolveCanvas(ComfyWorkflowGraph g, QwenInpaintParams p, WorkflowInputs inputs,
        out Output<Slot.Image> image, out Output<Slot.Mask> rawMask)
    {
        int pl = p.PadLeft, pt = p.PadTop;
        int pr = p.PadRight, pb = p.PadBottom;

        g[Nodes.Pad] = new ImagePadForOutpaint
        {
            Image = LoadImage.ImageOut(EditNodes.Source),
            Left = pl,
            Top = pt,
            Right = pr,
            Bottom = pb,
            // feathering=0 ON PURPOSE. The node's feathering ramps the mask INWARD from the pad boundary, which would
            // stack with the shared mask_grow/mask_blur softening and give a doubly-wide band of PARTIAL denoise over
            // the original pixels — a mushy seam. Softening happens once, in SoftenMask.
            Feathering = 0,
        };
        rawMask = ImagePadForOutpaint.MaskOut(Nodes.Pad);

        // GREY MUST NOT EXIST. ImagePadForOutpaint fills the new area with flat 0.5 grey, and that grey is the whole
        // halo family: any mask softness anywhere — the blur ramp, the latent blend, the composite crossfade — mixes
        // whatever is under the fill region into the picture, and if that is grey, the seam blends grey. Fixing the
        // halo with mask geometry only quarantines the grey instead of removing it. So the grey canvas is used ONLY
        // for its mask; the canvas the sampler actually sees is PRE-FILLED with scene-toned
        // content: the source stretched to the padded size, heavily blurred (a low-frequency tone scaffold — local
        // colors near each edge come from that edge's own content), with the original pasted back on top at its
        // offset. Blending can then only ever blend scene colors: the mask ramp, the latent blend and the composite
        // all cross-fade into scene tone, and the latent cells straddling the boundary encode scene colors instead
        // of a grey|content step. (Denoise stays 1.0 — see DefaultDenoise for why partial denoise over the scaffold
        // does not work here.)
        // CanvasSize refuses a source with unknown dimensions, so the padded canvas is always real here.
        (int W, int H) canvas = CanvasSize(p, inputs);
        g[Nodes.StretchScale] = new ImageScale
        {
            Image = LoadImage.ImageOut(EditNodes.Source),
            UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
            Width = canvas.W,
            Height = canvas.H,
            Crop = ComfyWidgets.Crop.Disabled,
        };
        // sigma 10.0 is ImageBlur's node maximum.
        g[Nodes.PrefillBlur] = new ImageBlur { Image = ImageScale.Out(Nodes.StretchScale), BlurRadius = 31, Sigma = 10.0 };
        g[Nodes.PrefillComposite] = new ImageCompositeMaskedNoMask
        {
            Destination = ImageBlur.Out(Nodes.PrefillBlur),
            Source = LoadImage.ImageOut(EditNodes.Source),
            X = pl,
            Y = pt,
            ResizeSource = false,
        };
        image = ImageCompositeMaskedNoMask.Out(Nodes.PrefillComposite);
    }
}
