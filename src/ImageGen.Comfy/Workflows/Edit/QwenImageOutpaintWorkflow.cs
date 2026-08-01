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

    /// <summary>Full denoise. 0.9 was tried to "lock tone to the pre-fill scaffold" and FAILED the other way:
    /// under the AuraFlow-shifted schedule even a 0.1 denoise reduction weights the init so heavily that the pad
    /// came back as the blur scaffold nearly verbatim (stretched-railing smear and all). At 1.0 the panels are
    /// fully generated and the scaffold still does its real jobs — every soft edge blends scene tone, never grey,
    /// and the boundary latent cells encode scene colors.</summary>
    protected override double DefaultDenoise => 1.0;

    /// <summary>The canvas the ceiling applies to is the PADDED one — outpainting is what actually grows the frame
    /// past the model's comfortable range, so measuring the unpadded source would let the real canvas sail past it.</summary>
    protected override (int W, int H) CanvasSize(ParamValues p, WorkflowInputs inputs)
    {
        if (inputs.SourceWidth <= 0 || inputs.SourceHeight <= 0) return (0, 0);
        return (inputs.SourceWidth + Math.Max(0, p.Int("pad_left")) + Math.Max(0, p.Int("pad_right")),
                inputs.SourceHeight + Math.Max(0, p.Int("pad_top")) + Math.Max(0, p.Int("pad_bottom")));
    }

    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema = ControlNetSchema.Concat(new ParamSpec[]
    {
        new() { Key = "denoise",    Type = ParamType.Double, Default = 1.0, Min = 0.5, Max = 1.0, Step = 0.01, Label = "Fill strength" },
        new() { Key = "pad_left",   Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = "pad_top",    Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = "pad_right",  Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = "pad_bottom", Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        // With the clamp holding the pad at 1, grow only places the ramp's midpoint: 16 = 2σ puts the 50% blend
        // 16px inside the original and has the descent begin right at the boundary — the shape the seam-free
        // hand-ramp measurement used. The crossfade band sits over ground where the ControlNet saw real adjacent
        // pixels.
        new() { Key = "mask_grow", Type = ParamType.Int, Default = 16, Min = 0, Max = 64, Label = "Mask grow (px)" },
        // NOTE: no "feather" knob. ImagePadForOutpaint's own feathering stays 0 (see ResolveCanvas) — the template
        // sets it to 0 too — so seam softening happens in exactly one place: mask_grow + mask_blur.
    }).ToArray();

    protected override void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask)
    {
        int pl = Math.Max(0, p.Int("pad_left")), pt = Math.Max(0, p.Int("pad_top"));
        int pr = Math.Max(0, p.Int("pad_right")), pb = Math.Max(0, p.Int("pad_bottom"));

        wf["20"] = ComfyGraph.Node("ImagePadForOutpaint", new
        {
            image = ComfyGraph.Ref("10", 0),
            left = pl, top = pt, right = pr, bottom = pb,
            // feathering=0 ON PURPOSE. The node's feathering ramps the mask INWARD from the pad boundary, which would
            // stack with the shared mask_grow/mask_blur softening and give a doubly-wide band of PARTIAL denoise over
            // the original pixels — a mushy seam. Softening happens once, in SoftenMask.
            feathering = 0,
        });
        rawMask = ComfyGraph.Ref("20", 1);

        // GREY MUST NOT EXIST. ImagePadForOutpaint fills the new area with flat 0.5 grey, and that grey is the whole
        // halo family: any mask softness anywhere — the blur ramp, the latent blend, the composite crossfade — mixes
        // whatever is under the fill region into the picture, and if that is grey, the seam blends grey. Every
        // attempt to fix the halo with mask geometry was quarantining the grey instead of removing it. So the grey
        // canvas is used ONLY for its mask; the canvas the sampler actually sees is PRE-FILLED with scene-toned
        // content: the source stretched to the padded size, heavily blurred (a low-frequency tone scaffold — local
        // colors near each edge come from that edge's own content), with the original pasted back on top at its
        // offset. Blending can then only ever blend scene colors: the mask ramp, the latent blend and the composite
        // all cross-fade into scene tone, and the latent cells straddling the boundary encode scene colors instead
        // of a grey|content step. (Denoise stays 1.0 — see DefaultDenoise for why partial denoise over the scaffold
        // does not work here.)
        var canvas = CanvasSize(p, inputs);
        if (canvas.W > 0 && canvas.H > 0)
        {
            wf["21"] = ComfyGraph.Node("ImageScale", new
            {
                image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos",
                width = canvas.W, height = canvas.H, crop = "disabled",
            });
            // sigma 10.0 is ImageBlur's node maximum.
            wf["22"] = ComfyGraph.Node("ImageBlur", new { image = ComfyGraph.Ref("21", 0), blur_radius = 31, sigma = 10.0 });
            wf["23"] = ComfyGraph.Node("ImageCompositeMasked", new
            {
                destination = ComfyGraph.Ref("22", 0),
                source = ComfyGraph.Ref("10", 0),
                x = pl, y = pt, resize_source = false,
            });
            image = ComfyGraph.Ref("23", 0);
        }
        else image = ComfyGraph.Ref("20", 0);   // source dims unknown: no stretch target, grey canvas as a last resort
    }
}
