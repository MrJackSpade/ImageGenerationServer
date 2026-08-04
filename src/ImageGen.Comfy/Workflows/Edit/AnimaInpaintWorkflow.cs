namespace ImageGen.Comfy;

/// <summary>
/// Masked img2img INPAINT using a standard generation checkpoint (Anima). Reuses the edit rails: the source image is
/// uploaded with the region-to-regenerate painted into its ALPHA channel, so ComfyUI's <c>LoadImage</c> (node "10",
/// emitted by <see cref="EditWorkflowBase.LoadModel"/>) yields BOTH the RGB pixels (IMAGE, slot 0) and the mask
/// (MASK, slot 1) from one upload — no separate mask file or request field. Only the masked region is denoised
/// (<c>SetLatentNoiseMask</c>) at a PARTIAL denoise, so the character's identity/structure is preserved while the
/// prompt drives the change (the target use: same character, new facial expression).
///
/// The edit submit path carries the positive (= the instruction) and an optional UI negative, applying no prefix, so
/// this workflow adds the prefix itself: <c>inputs.Positive</c> carries the user's FULL booru-tag prompt, the quality
/// prefix comes from <c>required_prefix</c>, and the negative is the config default (<c>negative</c>) with the UI
/// negative (<c>inputs.Negative</c>) appended — never replaced (see <see cref="ComfyGraph.ComposeNegative"/>).
/// </summary>
public sealed class AnimaInpaintWorkflow : EditWorkflowBase
{
    public override string Name => "anima-inpaint";

    /// <summary>inputs.Positive carries the user's FULL prompt for the picture, not an edit instruction.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;
    /// <summary>Local masked edit — exempt from the whole-image no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>
    /// SharedSchema already declares the loader/clip knobs + `denoise`; drop the shared denoise (its chat label
    /// "Denoise (source ↔ motion)" is wrong here) and re-add it as "Change amount", plus the inpaint-specific knobs.
    /// </summary>
    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema = SharedSchema.Where(s => s.Key != "denoise").Concat(new ParamSpec[]
    {
        // Step 0.01, not the UI's 0.1 default for doubles: how far the masked region drifts is the knob you tune most
        // finely here, and 0.1 is too coarse to land between (e.g.) 0.55 and 0.65.
        new() { Key = "denoise",         Type = ParamType.Double, Min = 0.2, Max = 1.0, Step = 0.01, Label = "Change amount" },
        new() { Key = "required_prefix", Type = ParamType.String },
        new() { Key = "negative",        Type = ParamType.String },
        new() { Key = "clip_skip",       Type = ParamType.Int },
        new() { Key = "mask_grow",       Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"

        // clip-skip applies only to a checkpoint's baked CLIP (Anima loads split → no-op there; kept for parity).
        if (p.StrReq("loader") == "checkpoint" && p.Has("clip_skip") && p.IntReq("clip_skip") is int clipSkip && clipSkip > 0)
        {
            wf["19"] = ComfyGraph.Node("CLIPSetLastLayer", new { clip = clip0, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clip0 = ComfyGraph.Ref("19", 0);
        }

        // Positive = quality prefix + the user's full prompt; negative = the config default with the UI negative
        // (inputs.Negative) appended — never replaced (see ComfyGraph.ComposeNegative).
        var rp = p.Str("required_prefix");
        var prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        var neg = ComfyGraph.ComposeNegative(p.Str("negative"), inputs.Negative);
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = prefix + inputs.Positive, clip = clip0 });
        wf["14"] = ComfyGraph.Node("CLIPTextEncode", new { text = neg, clip = clip0 });

        // Source RGB (LoadImage IMAGE, node "10") stays PRISTINE → latent, so the region outside the mask is preserved
        // and the masked region has the real pixels to partially-denoise from (identity kept, expression changed).
        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("10", 0), vae = vae0 });
        // Mask: a SEPARATE white-on-black image via LoadImageMask (red channel). Fallback to the source alpha only if
        // no mask image was supplied. SetLatentNoiseMask confines denoising to the masked (white) region.
        object maskSrc;
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            wf["11"] = ComfyGraph.Node("LoadImageMask", new { image = inputs.MaskImageName, channel = "red" });
            maskSrc = ComfyGraph.Ref("11", 0);
        }
        else maskSrc = ComfyGraph.Ref("10", 1);
        int grow = p.IntReq("mask_grow");
        if (grow > 0)
        {
            wf["30"] = ComfyGraph.Node("GrowMask", new { mask = maskSrc, expand = grow, tapered_corners = true });
            maskSrc = ComfyGraph.Ref("30", 0);
        }
        wf["31"] = ComfyGraph.Node("SetLatentNoiseMask", new { samples = ComfyGraph.Ref("12", 0), mask = maskSrc });

        double dn = p.DblReq("denoise");
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = dn,
            model = model0,
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("14", 0),
            latent_image = ComfyGraph.Ref("31", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
