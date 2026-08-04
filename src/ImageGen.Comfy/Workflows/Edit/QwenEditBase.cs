//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>
/// Qwen-Image-Edit (<c>TextEncodeQwenImageEditPlus</c>). Two models run this topology — the standard split model
/// and the all-in-one (AIO) rapid checkpoint — so they are two separate workflow classes over this shared base.
/// The only difference is the AIO bakes its own sampling, so the standard path inserts ModelSamplingAuraFlow+CFGNorm
/// and the AIO does not (<see cref="Aio"/>). Exact lift of the old <c>qwen_image</c>/<c>qwen_image_aio</c> branch.
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
        new() { Key = "mask_left_pct",   Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Default = 0, Label = "Mask left %",   Help = "Fence the model out of the left N% of the canvas" },
        new() { Key = "mask_right_pct",  Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Default = 0, Label = "Mask right %",  Help = "Fence the model out of the right N% of the canvas" },
        new() { Key = "mask_top_pct",    Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Default = 0, Label = "Mask top %",    Help = "Fence the model out of the top N% of the canvas" },
        new() { Key = "mask_bottom_pct", Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Default = 0, Label = "Mask bottom %", Help = "Fence the model out of the bottom N% of the canvas" },
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
        foreach (var (name, pct) in new[] { ("mask_left_pct", pctL), ("mask_right_pct", pctR), ("mask_top_pct", pctT), ("mask_bottom_pct", pctB) })
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

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        var instruction = inputs.Positive;
        var refNames = inputs.ReferenceImageNames;

        // Default resolution normalisation (FluxKontextImageScale snaps to a Qwen-trained bucket) + the danamir blur
        // fix. The text-encode image and the VAEEncode both come from that scaled image, and we build the ref latent
        // ourselves (VAE off the text-encode so it can't force-rescale) -> ref latent matches sample latent, no
        // per-turn resample -> no compounding blur over a multi-turn conversation.
        wf["11"] = ComfyGraph.Node("FluxKontextImageScale", new { image = ComfyGraph.Ref("10", 0) });
        var enc = new Dictionary<string, object> { ["clip"] = clip0, ["image1"] = ComfyGraph.Ref("11", 0), ["prompt"] = instruction };
        var qInputs = p.StrArray("reference_inputs");
        int qn = Math.Min(refNames.Count, Math.Min(p.Has("reference_max") ? p.IntReq("reference_max") : 0, qInputs.Length));
        for (int i = 0; i < qn; i++)                          // each reference: load + scale into image2/image3
        {
            string load = $"{40 + i * 2}", scale = $"{41 + i * 2}";
            wf[load] = ComfyGraph.Node("LoadImage", new { image = refNames[i] });
            wf[scale] = ComfyGraph.Node("FluxKontextImageScale", new { image = ComfyGraph.Ref(load, 0) });
            enc[qInputs[i]] = ComfyGraph.Ref(scale, 0);
        }
        wf["14"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("11", 0), vae = vae0 });
        object cond;
        if (qn > 0)
        {
            enc["vae"] = vae0;
            wf["13"] = ComfyGraph.Node("TextEncodeQwenImageEditPlus", enc);
            wf["70"] = ComfyGraph.Node("FluxKontextMultiReferenceLatentMethod", new { conditioning = ComfyGraph.Ref("13", 0), reference_latents_method = "index_timestep_zero" });
            cond = ComfyGraph.Ref("70", 0);
        }
        else
        {
            wf["13"] = ComfyGraph.Node("TextEncodeQwenImageEditPlus", enc);
            wf["30"] = ComfyGraph.Node("ReferenceLatent", new { conditioning = ComfyGraph.Ref("13", 0), latent = ComfyGraph.Ref("14", 0) });
            cond = ComfyGraph.Ref("30", 0);
        }
        wf["26"] = ComfyGraph.Node("ConditioningZeroOut", new { conditioning = cond });
        object ksModel = model0;
        if (!Aio)                                             // standard 2511 needs ModelSamplingAuraFlow + CFGNorm
        {
            wf["2"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = model0, shift = 3.1 });
            wf["7"] = ComfyGraph.Node("CFGNorm", new { model = ComfyGraph.Ref("2", 0), strength = 1.0 });
            ksModel = ComfyGraph.Ref("7", 0);
        }
        // Optional canvas mask, implemented as a REFRAME (see Schema). Sample on a latent shaped like the drawing
        // rectangle instead of the full canvas, then paste the decoded result back onto a white canvas at the
        // rectangle's offset. The model's fill-the-frame bias then works FOR us: given a 66%-tall frame it draws a
        // crouch at native scale. The conditioning is untouched — node 13 still encodes the FULL source and node 30's
        // reference latent is still the full-frame latent — so identity and the character's true scale are preserved.
        int Pct(string k) => p.Has(k) ? p.IntReq(k) : 0;   // a canvas-mask side %, absent = 0 (no mask on that side)
        var rect = inputs.SourceWidth > 0 && inputs.SourceHeight > 0
            ? MaskGeom(Pct("mask_left_pct"), Pct("mask_right_pct"), Pct("mask_top_pct"), Pct("mask_bottom_pct"),
                       inputs.SourceWidth, inputs.SourceHeight)
            : null;

        object sampleLatent = ComfyGraph.Ref("14", 0);
        if (rect is (int rx, int ry, int rw, int rh))
        {
            // Sample at the rectangle, aligned down to the VAE/patch stride; a blank white canvas is the starting
            // latent because denoise is 1.0, so only its SHAPE matters, not its content.
            wf["80"] = ComfyGraph.Node("EmptyImage", new { width = AlignDown(rw), height = AlignDown(rh), batch_size = 1, color = CanvasMaskConstants.BlockedFillRgb });
            wf["81"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("80", 0), vae = vae0 });
            sampleLatent = ComfyGraph.Ref("81", 0);
        }

        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed,
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = ksModel,
            positive = cond,
            negative = ComfyGraph.Ref("26", 0),
            latent_image = sampleLatent,
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });

        object output = ComfyGraph.Ref("8", 0);
        if (rect is (int px, int py, int pw, int ph))
        {
            // Undo the stride rounding, paste onto a white canvas at the rectangle's offset (both in source pixels),
            // then match the unmasked path's output dimensions exactly — GetImageSize reads the Kontext bucket node 11
            // chose, so a masked and an unmasked pose of the same portrait land on identical canvases and keep a
            // consistent sprite scale. When the source is already a bucket size this final scale is an identity.
            wf["82"] = ComfyGraph.Node("ImageScale", new { image = ComfyGraph.Ref("8", 0), upscale_method = "lanczos", width = pw, height = ph, crop = "disabled" });
            wf["83"] = ComfyGraph.Node("EmptyImage", new { width = inputs.SourceWidth, height = inputs.SourceHeight, batch_size = 1, color = CanvasMaskConstants.BlockedFillRgb });
            wf["84"] = ComfyGraph.Node("ImageCompositeMasked", new { destination = ComfyGraph.Ref("83", 0), source = ComfyGraph.Ref("82", 0), x = px, y = py, resize_source = false });
            wf["85"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
            wf["86"] = ComfyGraph.Node("ImageScale", new { image = ComfyGraph.Ref("84", 0), upscale_method = "lanczos", width = ComfyGraph.Ref("85", 0), height = ComfyGraph.Ref("85", 1), crop = "disabled" });
            output = ComfyGraph.Ref("86", 0);
        }
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = output, filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
