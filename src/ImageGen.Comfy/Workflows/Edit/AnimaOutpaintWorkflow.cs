namespace ImageGen.Comfy;

/// <summary>
/// OUTPAINT with Anima + the 4-channel inpainting <b>ControlNet-LLLite</b> (kohya-ss Anima-LLLite). The source is
/// loaded (node "10") and padded on each side by the caller's <c>pad_left/top/right/bottom</c> (source-native pixels)
/// via ComfyUI's built-in <c>ImagePadForOutpaint</c>, which returns the enlarged canvas (IMAGE) + a mask (MASK) marking
/// the new border. Two things then cooperate:
/// <list type="number">
/// <item><c>AnimaLLLiteApply</c> patches the model with the inpaint LLLite, fed the padded RGB + the border mask
/// (white = fill). This is the trained fill-conditioning a plain checkpoint lacks — it tells the model the KNOWN pixels
/// and the hole, so the border <b>continues the existing structure</b> instead of inventing new content over gray
/// (verified empirically: without it, the border is only stylistically similar, not a continuation).</item>
/// <item><c>VAEEncode</c> → <c>GrowMask</c> → <c>SetLatentNoiseMask</c> confine denoising to the border so the original
/// pixels are preserved natively (no composite), feathered into the seam — the same masked op as
/// <see cref="AnimaInpaintWorkflow"/>.</item>
/// </list>
/// Prefix/negative come from config params like <see cref="AnimaInpaintWorkflow"/>. Requires the LLLite weight
/// (<c>controlnet</c> requirement) + the <c>ComfyUI-Anima-LLLite</c> custom node.
/// </summary>
public sealed class AnimaOutpaintWorkflow : EditWorkflowBase
{
    public override string Name => "anima-outpaint";

    /// <summary>The prompt describes the whole extended picture, not a change to make to the existing pixels.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;
    /// <summary>Only the added border changes; the original region is untouched — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>Drop the shared <c>denoise</c> and re-add it (the gray border has nothing to preserve, so it defaults
    /// to a full regenerate), plus the per-side pad amounts, the seam feather, the mask grow (mirrors inpaint), and the
    /// Anima prefix/negative/clip-skip knobs.</summary>
    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema = SharedSchema.Where(s => s.Key != "denoise").Concat(new ParamSpec[]
    {
        new() { Key = "denoise",         Type = ParamType.Double, Default = 1.0, Min = 0.5, Max = 1.0, Label = "Fill strength" },
        new() { Key = "pad_left",        Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = "pad_top",         Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = "pad_right",       Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = "pad_bottom",      Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        new() { Key = "feather",         Type = ParamType.Int, Default = 24, Min = 0, Max = 256, Label = "Seam feather (px)" },
        new() { Key = "mask_grow",       Type = ParamType.Int, Default = 8, Min = 0, Max = 64, Label = "Mask grow (px)" },
        new() { Key = "lllite_strength", Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 2.0, Label = "Inpaint control strength" },
        new() { Key = "lllite_start",    Type = ParamType.Double, Default = 0.0, Min = 0.0, Max = 1.0, Label = "Control start %" },
        new() { Key = "lllite_end",      Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0, Label = "Control end %" },
        new() { Key = "required_prefix", Type = ParamType.String },
        new() { Key = "negative",        Type = ParamType.String },
        new() { Key = "clip_skip",       Type = ParamType.Int },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"

        int clipSkip = p.Int("clip_skip");
        if (clipSkip > 0 && (p.Str("loader") ?? "checkpoint") == "checkpoint")
        {
            wf["19"] = ComfyGraph.Node("CLIPSetLastLayer", new { clip = clip0, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clip0 = ComfyGraph.Ref("19", 0);
        }

        // Negative = the config default with the UI negative (inputs.Negative) appended — never replaced.
        var rp = p.Str("required_prefix");
        var prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp!.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        var neg = ComfyGraph.ComposeNegative(p.Str("negative"), inputs.Negative);
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = prefix + inputs.Positive, clip = clip0 });
        wf["14"] = ComfyGraph.Node("CLIPTextEncode", new { text = neg, clip = clip0 });

        // Pad the source on each side — the enlarged canvas (slot 0) + the added-border mask (slot 1). Feathering
        // softens the mask edge so the generated margin blends into the original instead of leaving a hard seam.
        int feather = Math.Max(0, p.Int("feather", 24));
        wf["20"] = ComfyGraph.Node("ImagePadForOutpaint", new
        {
            image = ComfyGraph.Ref("10", 0),
            left = Math.Max(0, p.Int("pad_left")),
            top = Math.Max(0, p.Int("pad_top")),
            right = Math.Max(0, p.Int("pad_right")),
            bottom = Math.Max(0, p.Int("pad_bottom")),
            feathering = feather,
        });

        // The fill-conditioning that a base checkpoint lacks: patch the Anima model with the 4-channel inpainting
        // ControlNet-LLLite (kohya-ss Anima-LLLite). It takes the padded RGB + the border MASK (white = fill) and
        // conditions generation on the KNOWN pixels + hole, so the border CONTINUES the existing structure instead of
        // inventing over gray. The node zeroes the RGB inside the mask itself, so the padded canvas (gray border) is
        // fine as the control image. Uses the raw pad mask (not the grown one) so the control keeps every known pixel.
        wf["40"] = ComfyGraph.Node("AnimaLLLiteApply", new
        {
            model = model0,
            lllite_name = req.ControlNet ?? "",
            image = ComfyGraph.Ref("20", 0),
            mask = ComfyGraph.Ref("20", 1),
            strength = p.Dbl("lllite_strength", 1.0),
            start_percent = p.Dbl("lllite_start", 0.0),
            end_percent = p.Dbl("lllite_end", 1.0),
            preserve_wrapper = true,
        });
        var ksModel = ComfyGraph.Ref("40", 0);

        // Encode the padded canvas; confine denoising to the padded (masked) border so the original region is kept.
        // GrowMask expands the border mask slightly into the original (mirrors AnimaInpaintWorkflow) so the seam blends.
        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("20", 0), vae = vae0 });
        object maskSrc = ComfyGraph.Ref("20", 1);
        int grow = p.Int("mask_grow", 8);
        if (grow > 0)
        {
            wf["30"] = ComfyGraph.Node("GrowMask", new { mask = maskSrc, expand = grow, tapered_corners = true });
            maskSrc = ComfyGraph.Ref("30", 0);
        }
        wf["31"] = ComfyGraph.Node("SetLatentNoiseMask", new { samples = ComfyGraph.Ref("12", 0), mask = maskSrc });

        double dn = p.Dbl("denoise", 1.0);
        if (dn <= 0 || dn > 1) dn = 1.0;
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 40),
            cfg = p.Dbl("cfg", 4.5),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = dn,
            model = ksModel,
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("14", 0),
            latent_image = ComfyGraph.Ref("31", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
