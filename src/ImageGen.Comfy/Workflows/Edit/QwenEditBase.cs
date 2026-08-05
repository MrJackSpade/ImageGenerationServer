using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// Qwen-Image-Edit (<c>TextEncodeQwenImageEditPlus</c>). Two models run this topology — the standard split model
/// and the all-in-one (AIO) rapid checkpoint — so they are two separate workflow classes over this shared base.
/// The only difference is the AIO bakes its own sampling, so the standard path inserts ModelSamplingAuraFlow+CFGNorm
/// and the AIO does not (<see cref="Aio"/>).
/// </summary>
public abstract class QwenEditBase : EditWorkflowBase
{
    /// <summary>True for the all-in-one rapid checkpoint (skips the standard 2511 sampling-fix nodes).</summary>
    protected abstract bool Aio { get; }

    /// <summary>
    /// Adds four <c>mask_*_pct</c> params on top of the shared edit schema: the <b>canvas mask</b>, i.e. how much of
    /// each side of the canvas the model is fenced out of, as a percentage of that dimension. What's left is the
    /// drawing rectangle. Unlike the WAN <c>pad_*_pct</c> params (which GROW the canvas so a character can move outside
    /// its bounds), these SHRINK the region the subject may occupy while the output canvas size stays put:
    /// <c>mask_top_pct=34</c> → the subject is drawn in the bottom two-thirds, the top third is plain white.
    ///
    /// <b>Implemented as a reframe, not an inpaint mask.</b> Qwen-Image-Edit reliably scales the subject to FILL its
    /// canvas — that bias is exactly why asking for a crouch on a full canvas yields a crouch blown up to full height,
    /// wrecking sprite scale. Fencing it with a <c>SetLatentNoiseMask</c> does NOT fix that: the conditioning
    /// (<c>image1</c> + the reference latent) still shows a subject filling the canvas, so the model paints a
    /// full-canvas figure and the mask merely erases whatever crosses the line — a decapitated character.
    ///
    /// So instead of fighting the fill-the-frame bias, this uses it: the sampler runs on a latent shaped like the
    /// RECTANGLE, and the decoded result is composited back onto a white canvas at the rectangle's offset. The model
    /// fills the frame it is given; we simply give it a frame of the right shape, so a crouch drawn to fill a 66%-tall
    /// rectangle lands at the character's native scale with her head intact. The reference latent and <c>image1</c>
    /// still carry the FULL source, so identity and native scale are preserved. See <see cref="MaskGeom"/>.
    /// </summary>
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.MaskLeftPct,   Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask left %",   Help = "Fence the model out of the left N% of the canvas" },
        new() { Key = WorkflowParamKeys.MaskRightPct,  Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask right %",  Help = "Fence the model out of the right N% of the canvas" },
        new() { Key = WorkflowParamKeys.MaskTopPct,    Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask top %",    Help = "Fence the model out of the top N% of the canvas" },
        new() { Key = WorkflowParamKeys.MaskBottomPct, Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask bottom %", Help = "Fence the model out of the bottom N% of the canvas" },
    }).ToArray();

    /// <summary>
    /// The largest multiple of <see cref="CanvasMaskConstants.LatentAlignPx"/> that is <c>&lt;= n</c> — the sampled
    /// rectangle must align to the VAE/patch stride, so it is rounded down and scaled back up on the way out.
    /// </summary>
    private static int AlignDown(int n) =>
        Math.Max(CanvasMaskConstants.LatentAlignPx, n - n % CanvasMaskConstants.LatentAlignPx);

    /// <summary>
    /// The drawing rectangle (X, Y, W, H) in SOURCE pixels left over once each side's blocked percentage is removed.
    /// Null when no side is blocked (the graph is then byte-identical to the unmasked one). Throws when the request is
    /// degenerate — opposing margins that leave no room, or a rectangle too small to survive the latent's 8× downscale
    /// — rather than silently clamping to something the caller didn't ask for.
    /// </summary>
    private static (int X, int Y, int W, int H)? MaskGeom(int pctL, int pctR, int pctT, int pctB, int sw, int sh)
    {
        if (pctL == 0 && pctR == 0 && pctT == 0 && pctB == 0) return null;   // no mask
        foreach ((string? name, int pct) in new[] { (WorkflowParamKeys.MaskLeftPct, pctL), (WorkflowParamKeys.MaskRightPct, pctR), (WorkflowParamKeys.MaskTopPct, pctT), (WorkflowParamKeys.MaskBottomPct, pctB) })
            if (pct < CanvasMaskConstants.MinSidePct || pct > CanvasMaskConstants.MaxSidePct)
                throw new ArgumentOutOfRangeException(name, pct,
                    $"must be {CanvasMaskConstants.MinSidePct}–{CanvasMaskConstants.MaxSidePct}");

        if (pctL + pctR > 100 - CanvasMaskConstants.MinOpenPctPerAxis)
            throw new ArgumentException($"mask_left_pct + mask_right_pct = {pctL + pctR}% leaves no width for the model to draw in.");
        if (pctT + pctB > 100 - CanvasMaskConstants.MinOpenPctPerAxis)
            throw new ArgumentException($"mask_top_pct + mask_bottom_pct = {pctT + pctB}% leaves no height for the model to draw in.");

        int x = sw * pctL / 100, y = sh * pctT / 100;
        int w = sw - x - sw * pctR / 100, h = sh - y - sh * pctB / 100;
        if (w < CanvasMaskConstants.MinRectPx || h < CanvasMaskConstants.MinRectPx)
            throw new ArgumentException($"the masked drawing rectangle is {w}×{h}px, below the {CanvasMaskConstants.MinRectPx}px minimum.");
        return (x, y, w, h);
    }

    /// <summary>This base's own node ids (role-named), on top of the inherited edit head
    /// (Nodes.Model/Clip/Vae/Source). The per-reference load/scale nodes stay computed ($"{40+i*2}"). Values
    /// preserved exactly so the emitted graph stays byte-identical.</summary>
    protected const string KontextScale = "11";
    protected const string Encode = "13";
    protected const string SourceEncode = "14";
    protected const string RefLatent = "30";
    protected const string MultiRefLatent = "70";
    protected const string ZeroNegative = "26";
    protected const string ModelSampling = "2";
    protected const string CfgNorm = "7";
    protected const string RectCanvas = "80";
    protected const string RectEncode = "81";
    protected const string Sampler = "3";
    protected const string Decode = "8";
    protected const string RectResize = "82";
    protected const string PasteCanvas = "83";
    protected const string Composite = "84";
    protected const string OutputSize = "85";
    protected const string OutputScale = "86";
    protected const string Save = "9";

    /// <summary>The TextEncodeQwenImageEditPlus node's fixed input-field names (the enc-dict keys). Values are the
    /// ComfyUI input names, preserved exactly; the per-reference image slots come from the reference_inputs param.</summary>
    private static class Inputs
    {
        public const string Clip = "clip";
        public const string Image1 = "image1";
        public const string Prompt = "prompt";
        public const string Vae = "vae";
    }

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);
        long seed = ComfyGraph.Seed(p);
        string instruction = inputs.Positive;
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;

        // Default resolution normalisation (FluxKontextImageScale snaps to a Qwen-trained bucket) + the danamir blur
        // fix. The text-encode image and the VAEEncode both come from that scaled image, and we build the ref latent
        // ourselves (VAE off the text-encode so it can't force-rescale) -> ref latent matches sample latent, no
        // per-turn resample -> no compounding blur over a multi-turn conversation.
        wf[KontextScale] = ComfyGraph.Node(ComfyNodeTypes.FluxKontextImageScale, new { image = ComfyGraph.Ref(Nodes.Source, 0) });
        Dictionary<string, object> enc = new Dictionary<string, object> { [Inputs.Clip] = clip0, [Inputs.Image1] = ComfyGraph.Ref(KontextScale, 0), [Inputs.Prompt] = instruction };
        string[] qInputs = p.StrArray(WorkflowParamKeys.ReferenceInputs);
        int qn = Math.Min(refNames.Count, Math.Min(p.Has(WorkflowParamKeys.ReferenceMax) ? p.IntReq(WorkflowParamKeys.ReferenceMax) : 0, qInputs.Length));
        for (int i = 0; i < qn; i++)                          // each reference: load + scale into image2/image3
        {
            string load = $"{40 + i * 2}", scale = $"{41 + i * 2}";
            wf[load] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = refNames[i] });
            wf[scale] = ComfyGraph.Node(ComfyNodeTypes.FluxKontextImageScale, new { image = ComfyGraph.Ref(load, 0) });
            enc[qInputs[i]] = ComfyGraph.Ref(scale, 0);
        }
        wf[SourceEncode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(KontextScale, 0), vae = vae0 });
        object cond;
        if (qn > 0)
        {
            enc[Inputs.Vae] = vae0;
            wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.TextEncodeQwenImageEditPlus, enc);
            wf[MultiRefLatent] = ComfyGraph.Node(ComfyNodeTypes.FluxKontextMultiReferenceLatentMethod, new { conditioning = ComfyGraph.Ref(Encode, 0), reference_latents_method = "index_timestep_zero" });
            cond = ComfyGraph.Ref(MultiRefLatent, 0);
        }
        else
        {
            wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.TextEncodeQwenImageEditPlus, enc);
            wf[RefLatent] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = ComfyGraph.Ref(Encode, 0), latent = ComfyGraph.Ref(SourceEncode, 0) });
            cond = ComfyGraph.Ref(RefLatent, 0);
        }
        wf[ZeroNegative] = ComfyGraph.Node(ComfyNodeTypes.ConditioningZeroOut, new { conditioning = cond });
        object ksModel = model0;
        if (!Aio)                                             // standard 2511 needs ModelSamplingAuraFlow + CFGNorm
        {
            wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingAuraFlow, new { model = model0, shift = 3.1 });
            wf[CfgNorm] = ComfyGraph.Node(ComfyNodeTypes.CFGNorm, new { model = ComfyGraph.Ref(ModelSampling, 0), strength = 1.0 });
            ksModel = ComfyGraph.Ref(CfgNorm, 0);
        }
        // Optional canvas mask, implemented as a REFRAME (see Schema). Sample on a latent shaped like the drawing
        // rectangle instead of the full canvas, then paste the decoded result back onto a white canvas at the
        // rectangle's offset. The model's fill-the-frame bias then works FOR us: given a 66%-tall frame it draws a
        // crouch at native scale. The conditioning is untouched — node 13 still encodes the FULL source and node 30's
        // reference latent is still the full-frame latent — so identity and the character's true scale are preserved.
        int Pct(string k) => p.Has(k) ? p.IntReq(k) : 0;   // a canvas-mask side %, absent = 0 (no mask on that side)
        // A Qwen edit's source is a still, so its dimensions are ALWAYS measured — a zero is a broken source, not a
        // valid state. Refuse it rather than silently drop a requested canvas mask. (MaskGeom returns null when no
        // mask side is set, so an unmasked edit still no-ops.)
        Ensure.GreaterThanZero(inputs.SourceWidth);
        Ensure.GreaterThanZero(inputs.SourceHeight);
        (int X, int Y, int W, int H)? rect = MaskGeom(Pct(WorkflowParamKeys.MaskLeftPct), Pct(WorkflowParamKeys.MaskRightPct), Pct(WorkflowParamKeys.MaskTopPct), Pct(WorkflowParamKeys.MaskBottomPct),
                            inputs.SourceWidth, inputs.SourceHeight);

        object sampleLatent = ComfyGraph.Ref(SourceEncode, 0);
        if (rect is (int rx, int ry, int rw, int rh))
        {
            // Sample at the rectangle, aligned down to the VAE/patch stride; a blank white canvas is the starting
            // latent because denoise is 1.0, so only its SHAPE matters, not its content.
            wf[RectCanvas] = ComfyGraph.Node(ComfyNodeTypes.EmptyImage, new { width = AlignDown(rw), height = AlignDown(rh), batch_size = 1, color = CanvasMaskConstants.BlockedFillRgb });
            wf[RectEncode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(RectCanvas, 0), vae = vae0 });
            sampleLatent = ComfyGraph.Ref(RectEncode, 0);
        }

        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed,
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = ksModel,
            positive = cond,
            negative = ComfyGraph.Ref(ZeroNegative, 0),
            latent_image = sampleLatent,
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });

        object output = ComfyGraph.Ref(Decode, 0);
        if (rect is (int px, int py, int pw, int ph))
        {
            // Undo the stride rounding, paste onto a white canvas at the rectangle's offset (both in source pixels),
            // then match the unmasked path's output dimensions exactly — GetImageSize reads the Kontext bucket node 11
            // chose, so a masked and an unmasked pose of the same portrait land on identical canvases and keep a
            // consistent sprite scale. When the source is already a bucket size this final scale is an identity.
            wf[RectResize] = ComfyGraph.Node(ComfyNodeTypes.ImageScale, new { image = ComfyGraph.Ref(Decode, 0), upscale_method = "lanczos", width = pw, height = ph, crop = "disabled" });
            wf[PasteCanvas] = ComfyGraph.Node(ComfyNodeTypes.EmptyImage, new { width = inputs.SourceWidth, height = inputs.SourceHeight, batch_size = 1, color = CanvasMaskConstants.BlockedFillRgb });
            wf[Composite] = ComfyGraph.Node(ComfyNodeTypes.ImageCompositeMasked, new { destination = ComfyGraph.Ref(PasteCanvas, 0), source = ComfyGraph.Ref(RectResize, 0), x = px, y = py, resize_source = false });
            wf[OutputSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(KontextScale, 0) });
            wf[OutputScale] = ComfyGraph.Node(ComfyNodeTypes.ImageScale, new { image = ComfyGraph.Ref(Composite, 0), upscale_method = "lanczos", width = ComfyGraph.Ref(OutputSize, 0), height = ComfyGraph.Ref(OutputSize, 1), crop = "disabled" });
            output = ComfyGraph.Ref(OutputScale, 0);
        }
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = output, filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
