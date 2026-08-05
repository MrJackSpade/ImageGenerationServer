using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// OUTPAINT on base Qwen-Image + the InstantX inpainting ControlNet. <c>ImagePadForOutpaint</c> extends the canvas by
/// the caller's per-side pads (source-native px) and returns the enlarged image plus a mask marking the new border;
/// the ControlNet then conditions the fill on the known pixels so the border continues the existing structure.
/// Pads are the only override the outpaint UI sends (see <c>edit.js</c>).
/// </summary>
public sealed class QwenImageOutpaintWorkflow : QwenInstantXInpaintBase
{
    public override string Name => "qwen-image-outpaint";

    /// <summary>Full denoise. A lower denoise (even 0.9, "locking tone to the pre-fill scaffold") FAILS the other
    /// way: under the AuraFlow-shifted schedule even a 0.1 denoise reduction weights the init so heavily that the pad
    /// comes back as the blur scaffold nearly verbatim (stretched-railing smear and all). At 1.0 the panels are
    /// fully generated and the scaffold still does its real jobs — every soft edge blends scene tone, never grey,
    /// and the boundary latent cells encode scene colors.</summary>
    protected override double DefaultDenoise => 1.0;

    /// <summary>The canvas the ceiling applies to is the PADDED one — outpainting is what actually grows the frame
    /// past the model's comfortable range, so measuring the unpadded source would let the real canvas sail past it.</summary>
    protected override (int W, int H) CanvasSize(ParamValues p, WorkflowInputs inputs)
    {
        Ensure.GreaterThanZero(inputs.SourceWidth);
        Ensure.GreaterThanZero(inputs.SourceHeight);
        return (inputs.SourceWidth + Ensure.NotNegative(p.Int(WorkflowParamKeys.PadLeft), WorkflowParamKeys.PadLeft) + Ensure.NotNegative(p.Int(WorkflowParamKeys.PadRight), WorkflowParamKeys.PadRight),
                inputs.SourceHeight + Ensure.NotNegative(p.Int(WorkflowParamKeys.PadTop), WorkflowParamKeys.PadTop) + Ensure.NotNegative(p.Int(WorkflowParamKeys.PadBottom), WorkflowParamKeys.PadBottom));
    }

    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema = ControlNetSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise,    Type = ParamType.Double, Min = 0.5, Max = 1.0, Step = 0.01, Label = "Fill strength" },
        new() { Key = WorkflowParamKeys.PadLeft,   Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,    Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,  Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        // With the clamp holding the pad at 1, grow only places the ramp's midpoint: 16 = 2σ puts the 50% blend
        // 16px inside the original and has the descent begin right at the boundary — the shape the seam-free
        // hand-ramp measurement used. The crossfade band sits over ground where the ControlNet saw real adjacent
        // pixels.
        new() { Key = WorkflowParamKeys.MaskGrow, Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
        // NOTE: no "feather" knob. ImagePadForOutpaint's own feathering stays 0 (see ResolveCanvas) — the template
        // sets it to 0 too — so seam softening happens in exactly one place: mask_grow + mask_blur.
    }).ToArray();

    /// <summary>This workflow's own node ids, atop the inherited edit head and QwenInstantXInpaintBase's nodes.</summary>
    private const string Pad = "20";
    private const string StretchScale = "21";
    private const string PrefillBlur = "22";
    private const string PrefillComposite = "23";

    protected override void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask)
    {
        int pl = Ensure.NotNegative(p.Int(WorkflowParamKeys.PadLeft), WorkflowParamKeys.PadLeft), pt = Ensure.NotNegative(p.Int(WorkflowParamKeys.PadTop), WorkflowParamKeys.PadTop);
        int pr = Ensure.NotNegative(p.Int(WorkflowParamKeys.PadRight), WorkflowParamKeys.PadRight), pb = Ensure.NotNegative(p.Int(WorkflowParamKeys.PadBottom), WorkflowParamKeys.PadBottom);

        wf[Pad] = ComfyGraph.Node(ComfyNodeTypes.ImagePadForOutpaint, new
        {
            image = ComfyGraph.Ref(Nodes.Source, 0),
            left = pl, top = pt, right = pr, bottom = pb,
            // feathering=0 ON PURPOSE. The node's feathering ramps the mask INWARD from the pad boundary, which would
            // stack with the shared mask_grow/mask_blur softening and give a doubly-wide band of PARTIAL denoise over
            // the original pixels — a mushy seam. Softening happens once, in SoftenMask.
            feathering = 0,
        });
        rawMask = ComfyGraph.Ref(Pad, 1);

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
        var canvas = CanvasSize(p, inputs);
        wf[StretchScale] = ComfyGraph.Node(ComfyNodeTypes.ImageScale, new
        {
            image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos",
            width = canvas.W, height = canvas.H, crop = "disabled",
        });
        // sigma 10.0 is ImageBlur's node maximum.
        wf[PrefillBlur] = ComfyGraph.Node(ComfyNodeTypes.ImageBlur, new { image = ComfyGraph.Ref(StretchScale, 0), blur_radius = 31, sigma = 10.0 });
        wf[PrefillComposite] = ComfyGraph.Node(ComfyNodeTypes.ImageCompositeMasked, new
        {
            destination = ComfyGraph.Ref(PrefillBlur, 0),
            source = ComfyGraph.Ref(Nodes.Source, 0),
            x = pl, y = pt, resize_source = false,
        });
        image = ComfyGraph.Ref(PrefillComposite, 0);
    }
}
