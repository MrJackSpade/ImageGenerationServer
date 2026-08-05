using ImageGen.Domain;

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
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema = SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.5, Max = 1.0, Label = "Fill strength" },
        new() { Key = WorkflowParamKeys.PadLeft,        Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,         Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,       Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom,      Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        new() { Key = WorkflowParamKeys.Feather,         Type = ParamType.Int, Min = 0, Max = 256, Label = "Seam feather (px)" },
        new() { Key = WorkflowParamKeys.MaskGrow,       Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
        new() { Key = WorkflowParamKeys.LlliteStrength, Type = ParamType.Double, Min = 0.0, Max = 2.0, Label = "Inpaint control strength" },
        new() { Key = WorkflowParamKeys.LlliteStart,    Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Control start %" },
        new() { Key = WorkflowParamKeys.LlliteEnd,      Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Control end %" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
    }).ToArray();

    /// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflowBase.Nodes).</summary>
    private const string ClipSkip = "19";
    private const string Positive = "13";
    private const string Negative = "14";
    private const string Pad = "20";
    private const string LlliteApply = "40";
    private const string Encode = "12";
    private const string GrowMaskNode = "30";
    private const string NoiseMask = "31";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"

        if (p.Loader() == LoaderKind.Checkpoint && p.Has(WorkflowParamKeys.ClipSkip) && p.IntReq(WorkflowParamKeys.ClipSkip) is int clipSkip && clipSkip > 0)
        {
            wf[ClipSkip] = ComfyGraph.Node(ComfyNodeTypes.CLIPSetLastLayer, new { clip = clip0, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clip0 = ComfyGraph.Ref(ClipSkip, 0);
        }

        // Negative = the config default with the UI negative (inputs.Negative) appended — never replaced.
        var rp = p.Str(WorkflowParamKeys.RequiredPrefix);
        var prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        var neg = ComfyGraph.ComposeNegative(p.Str(WorkflowParamKeys.Negative), inputs.Negative);
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = prefix + inputs.Positive, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = neg, clip = clip0 });

        // Pad the source on each side — the enlarged canvas (slot 0) + the added-border mask (slot 1). Feathering
        // softens the mask edge so the generated margin blends into the original instead of leaving a hard seam.
        int PadPx(string k) => p.Has(k) ? Ensure.NotNegative(p.IntReq(k), k) : 0;   // per-side extend px, absent = 0 (no pad on that side)
        int feather = Ensure.NotNegative(p.IntReq(WorkflowParamKeys.Feather), WorkflowParamKeys.Feather);
        wf[Pad] = ComfyGraph.Node(ComfyNodeTypes.ImagePadForOutpaint, new
        {
            image = ComfyGraph.Ref(Nodes.Source, 0),
            left = PadPx(WorkflowParamKeys.PadLeft),
            top = PadPx(WorkflowParamKeys.PadTop),
            right = PadPx(WorkflowParamKeys.PadRight),
            bottom = PadPx(WorkflowParamKeys.PadBottom),
            feathering = feather,
        });

        // The fill-conditioning that a base checkpoint lacks: patch the Anima model with the 4-channel inpainting
        // ControlNet-LLLite (kohya-ss Anima-LLLite). It takes the padded RGB + the border MASK (white = fill) and
        // conditions generation on the KNOWN pixels + hole, so the border CONTINUES the existing structure instead of
        // inventing over gray. The node zeroes the RGB inside the mask itself, so the padded canvas (gray border) is
        // fine as the control image. Uses the raw pad mask (not the grown one) so the control keeps every known pixel.
        wf[LlliteApply] = ComfyGraph.Node(ComfyNodeTypes.AnimaLLLiteApply, new
        {
            model = model0,
            lllite_name = req.RequiredControlNet(),
            image = ComfyGraph.Ref(Pad, 0),
            mask = ComfyGraph.Ref(Pad, 1),
            strength = p.DblReq(WorkflowParamKeys.LlliteStrength),
            start_percent = p.DblReq(WorkflowParamKeys.LlliteStart),
            end_percent = p.DblReq(WorkflowParamKeys.LlliteEnd),
            preserve_wrapper = true,
        });
        var ksModel = ComfyGraph.Ref(LlliteApply, 0);

        // Encode the padded canvas; confine denoising to the padded (masked) border so the original region is kept.
        // GrowMask expands the border mask slightly into the original (mirrors AnimaInpaintWorkflow) so the seam blends.
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(Pad, 0), vae = vae0 });
        object maskSrc = ComfyGraph.Ref(Pad, 1);
        int grow = p.IntReq(WorkflowParamKeys.MaskGrow);
        if (grow > 0)
        {
            wf[GrowMaskNode] = ComfyGraph.Node(ComfyNodeTypes.GrowMask, new { mask = maskSrc, expand = grow, tapered_corners = true });
            maskSrc = ComfyGraph.Ref(GrowMaskNode, 0);
        }
        wf[NoiseMask] = ComfyGraph.Node(ComfyNodeTypes.SetLatentNoiseMask, new { samples = ComfyGraph.Ref(Encode, 0), mask = maskSrc });

        double dn = p.DblReq(WorkflowParamKeys.Denoise);
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = dn,
            model = ksModel,
            positive = ComfyGraph.Ref(Positive, 0),
            negative = ComfyGraph.Ref(Negative, 0),
            latent_image = ComfyGraph.Ref(NoiseMask, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
