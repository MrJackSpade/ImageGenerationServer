//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>
/// INPAINT / OUTPAINT on base <b>Qwen-Image</b> using the InstantX inpainting ControlNet
/// (<c>Qwen-Image-InstantX-ControlNet-Inpainting.safetensors</c>, natively supported since ComfyUI 0.3.59).
///
/// <para><b>Why base Qwen-Image and not Qwen-Image-Edit.</b> The ControlNet was trained against
/// <c>Qwen/Qwen-Image</c> — the txt2img base. It is shape-compatible with the Edit fine-tune (same MMDiT, same VAE)
/// so it loads and samples without erroring, but Edit feeds the reference image through Qwen2.5-VL AND concatenates
/// its VAE latent into the sequence; the ControlNet's residuals then land on a distribution it never saw, and the
/// reported result is a plausible masked region with the rest of the frame degraded. InstantX has never shipped an
/// Edit variant. So these two workflows bind to the base model, and the Qwen EDIT editors stay as they are.</para>
///
/// <para><b>The topology</b> (lifted from Comfy's own <c>image_qwen_image_instantx_inpainting_controlnet</c>
/// template). The ControlNet apply node is <c>ControlNetInpaintingAliMamaApply</c> — Comfy reuses AliMama's node
/// because its signature (positive, negative, control_net, vae, image, mask) is exactly what this ControlNet needs;
/// there is no Qwen-specific apply node. The mask does three jobs:</para>
/// <list type="number">
/// <item>Into the ControlNet apply — this is the real <b>fill conditioning</b>. The node itself inverts the mask and
/// zeroes the RGB inside it, handing the model the KNOWN pixels plus the hole, so the fill CONTINUES the surrounding
/// structure. This is the piece a bare masked-denoise lacks (the same lesson as
/// <see cref="AnimaOutpaintWorkflow"/>, where a plain checkpoint produced borders that were only stylistically
/// similar rather than a continuation).</item>
/// <item>Into <c>SetLatentNoiseMask</c>, confining denoising to the region. This is also the <b>exposure anchor</b>:
/// re-injecting the noised ORIGINAL latents outside the mask at every step lets attention harmonize the fill's tone
/// with the real image. ControlNet residuals alone do NOT anchor exposure — an outpaint cut without this node
/// produced side panels a measured ~15 RGB brighter than the frame they extended (the "color balance" halo).</item>
/// <item>Into a final <c>ImageCompositeMasked</c> paste-back, so pixels outside the mask are byte-for-byte the
/// source. The VAE round-trip perturbs the whole frame otherwise — the exact "it also redraws outside the mask and
/// softens the picture" complaint this ControlNet drew on release.</item>
/// </list>
///
/// <para>ONE mask feeds all three consumers — the ControlNet apply, <c>SetLatentNoiseMask</c> and the composite —
/// as the reference template wires it. Do not split it. An earlier cut gave the ControlNet the RAW mask and the
/// other two the grown/blurred one (copied from <see cref="AnimaOutpaintWorkflow"/>, a different ControlNet with
/// different semantics); that made the conditioning and the denoise disagree over a <c>mask_grow</c>-wide ring and
/// dirtied the seam. A later cut split the COMPOSITE off instead (raw pad mask there, softened elsewhere) and put a
/// hard switch exactly on the ring the ControlNet was blind to — the extension visibly failed to line up.</para>
///
/// <para><b>Where we deviate from the template.</b> Two coupled changes, both on the outpaint path. (1) The grey
/// pad never reaches the sampler: <c>ImagePadForOutpaint</c> is kept only for its mask, and the actual canvas is
/// pre-filled with a blurred stretch of the source so every pixel under the fill region is scene-toned — grey is
/// the substance every "halo" ever measured here was made of, and removing it beats quarantining it with mask
/// geometry (see <c>QwenImageOutpaintWorkflow.ResolveCanvas</c>). (2) The template's outpaint branch drops
/// <c>SetLatentNoiseMask</c> (VAEEncode straight into KSampler) and accepts unanchored fill exposure; we keep the
/// latent mask in BOTH directions and defeat its known failure mode — a binary mask blends latents across ONE 8px
/// cell and decodes as a hard 1px line (measured: a lone ~63 gradient column) — with a ramp wide enough to span
/// several latent cells (<see cref="MaskBlurSigma"/>), held at a hard 1 over the fill region itself
/// (<see cref="HoldFillRegionAtFull"/>).</para>
/// </summary>
public abstract class QwenInstantXInpaintBase : EditWorkflowBase
{
    /// <summary>Prompt describes the whole resulting picture (a generation-style prompt), not an edit instruction —
    /// base Qwen-Image is a txt2img model with a plain CLIPTextEncode, not an instruction editor.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;
    /// <summary>Only the masked region changes (and the composite guarantees it) — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;


    /// <summary>Knobs common to both directions. The shared <c>denoise</c> label ("Denoise (source ↔ motion)") is
    /// wrong here, so it is dropped and re-added per subclass.</summary>
    protected static readonly IReadOnlyList<ParamSpec> ControlNetSchema = SharedSchema.Where(s => s.Key != "denoise").Concat(new ParamSpec[]
    {
        new() { Key = "negative",       Type = ParamType.String },
        // Shift for ModelSamplingAuraFlow. Qwen-Image's trained value; the template ships 3.1.
        new() { Key = "auraflow",       Type = ParamType.Double, Default = 3.1, Min = 0.0, Max = 10.0 },
        new() { Key = "cn_strength",    Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 2.0,  Step = 0.05, Label = "Fill control strength" },
        new() { Key = "cn_start",       Type = ParamType.Double, Default = 0.0, Min = 0.0, Max = 1.0,  Step = 0.01, Label = "Control start %" },
        new() { Key = "cn_end",         Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0,  Step = 0.01, Label = "Control end %" },
        // mask_grow is declared PER DIRECTION (0 inpaint / 24 outpaint) — see each subclass's schema; they are not
        // interchangeable.
        //
        // Gaussian blur applied to the mask through the IMAGE round-trip in SoftenMask, matching the reference
        // template's "Grow and Blur Mask" subgraph: ImageBlur[radius 31, sigma MaskBlurSigma]. This knob is only the
        // kernel WINDOW; the ramp width is set by the per-direction sigma (see MaskBlurSigma — narrow for inpaint,
        // latent-cell-wide for outpaint, always paired with a grow that keeps the ramp off the 0.5-grey pad fill).
        // Do not "simplify" this to FeatherMask — that node ramps in from the CANVAS EDGES, not from the mask's own
        // boundary.
        new() { Key = "mask_blur",      Type = ParamType.Int,    Default = 31,  Min = 0,   Max = 31,   Label = "Mask edge blur (px)" },
        // Long-edge ceiling for the sampled canvas. 0 = no ceiling (run native). This is a CEILING, not a target: an
        // image already under it is passed through untouched and is never upscaled to meet it. Comfy's template uses
        // ImageScaleToMaxDimension, which forces the long edge to EXACTLY largest_size and so scales small sources UP;
        // that wastes VRAM on a 20B model + a 4.2GB ControlNet and silently changes the user's resolution, so the
        // decision is made here in C# (the source dims are known at submit) and a scale node is emitted only when the
        // canvas genuinely exceeds the ceiling.
        new() { Key = "max_dimension",  Type = ParamType.Int,    Default = 1536, Min = 0,  Max = 4096, Label = "Max long edge (px)" },
    }).ToArray();

    /// <summary>Produce the canvas to fill and the region to fill in it. Inpaint uses the source as-is plus the
    /// painted mask; outpaint pads the source and uses the added border as the mask.</summary>
    protected abstract void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask);

    /// <summary>Direction-specific denoise default (inpaint preserves what's under the mask; outpaint has nothing
    /// under the border to preserve).</summary>
    protected abstract double DefaultDenoise { get; }

    /// <summary>Gaussian sigma for the mask-edge blur, in pixels of the FINAL canvas — deliberately wider than the
    /// template's 1.0 in BOTH directions: the ramp must span several 8px latent cells or
    /// <c>SetLatentNoiseMask</c>'s blend lands inside one cell and decodes as a hard 1px line along the join.
    /// Shared because the fill regions are alike: an outpaint pad is flat grey and this app's inpaint masks cover
    /// flat WHITE space to be filled — the same "non-scene content under the fill" problem in a different color.</summary>
    private const double MaskBlurSigma = 8.0;

    /// <summary>Pixel size of the canvas <see cref="ResolveCanvas"/> produces, used to decide whether the
    /// <c>max_dimension</c> ceiling is exceeded. (0,0) when the source dims are unknown, in which case no scaling is
    /// emitted — we never guess at a resolution change.</summary>
    protected virtual (int W, int H) CanvasSize(ParamValues p, WorkflowInputs inputs)
        => (inputs.SourceWidth, inputs.SourceHeight);

    /// <summary>
    /// The reference template's "Grow and Blur Mask" subgraph, node for node:
    /// <c>GrowMask → MaskToImage → ImageBlur[radius, σ] → ImageToMask</c> (σ = <see cref="MaskBlurSigma"/>).
    ///
    /// <para>The IMAGE round-trip is not incidental — no MASK-space node blurs a mask's own boundary.
    /// <c>FeatherMask</c> is NOT a substitute: it ramps the mask in from the CANVAS EDGES, so on an outpaint (whose
    /// fill region touches those edges) it drove the mask toward 0 exactly where the fill had to be strongest, and
    /// <c>ImagePadForOutpaint</c>'s 0.5-grey fill showed through as a grey frame.</para>
    /// </summary>
    private object SoftenMask(Dictionary<string, object> wf, ParamValues p, object rawMask)
    {
        object m = rawMask;
        int grow = Math.Max(0, p.Int("mask_grow"));
        if (grow > 0)
        {
            wf["30"] = ComfyGraph.Node("GrowMask", new { mask = m, expand = grow, tapered_corners = true });
            m = ComfyGraph.Ref("30", 0);
        }

        int blur = Math.Clamp(p.Int("mask_blur", 31), 0, 31);
        if (blur == 0) return m;

        wf["32"] = ComfyGraph.Node("MaskToImage", new { mask = m });
        wf["33"] = ComfyGraph.Node("ImageBlur", new { image = ComfyGraph.Ref("32", 0), blur_radius = blur, sigma = MaskBlurSigma });
        wf["34"] = ComfyGraph.Node("ImageToMask", new { image = ComfyGraph.Ref("33", 0), channel = "red" });

        // "add" + the node's final 0..1 clamp = max() against the raw fill mask: the ramp survives only where the
        // raw mask is 0 (over the original), and every fill pixel is restored to a hard 1. Any mask deficit over
        // the fill region mixes its flat content (grey pad / white hole) into the result through both the latent
        // re-injection and the composite, and the leak is visible far below full amplitude — measured seam columns
        // of 51/34/10 as the gaussian's value at the pad boundary went 0.933/0.977/0.9987, versus seam-free with a
        // ramp held at exactly 1.0 there. The ramp is therefore ONE-SIDED: hard over the fill, descending only
        // outward across real source pixels.
        wf["35"] = ComfyGraph.Node("MaskComposite", new
        {
            destination = ComfyGraph.Ref("34", 0), source = rawMask, x = 0, y = 0, operation = "add",
        });
        return ComfyGraph.Ref("35", 0);
    }

    /// <summary>Emit the ceiling scale for the canvas AND its mask, or return them untouched. Both must be resized
    /// together: the ControlNet apply and the sampler's noise mask resize a mismatched mask internally, but
    /// <c>ImageCompositeMasked</c> does not, so a mask left at the original size would break the paste-back.</summary>
    private static void ApplyCeiling(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        (int W, int H) canvas, ref object image, ref object rawMask)
    {
        int cap = p.Int("max_dimension", 1536);
        int longEdge = Math.Max(canvas.W, canvas.H);
        if (cap <= 0 || canvas.W <= 0 || canvas.H <= 0 || longEdge <= cap) return;   // native: under the ceiling, or unknown

        // Preserve aspect, then snap DOWN to a multiple of 16 (Qwen: VAE /8 + patch /2) so the latent grid is exact.
        double f = (double)cap / longEdge;
        int w = Math.Max(16, (int)(canvas.W * f) / 16 * 16);
        int h = Math.Max(16, (int)(canvas.H * f) / 16 * 16);

        wf["172"] = ComfyGraph.Node("ImageScale", new
        {
            image, upscale_method = "lanczos", width = w, height = h, crop = "disabled",
        });
        // The mask has to make the same trip; MASK has no scale node, so round-trip it through IMAGE.
        wf["173"] = ComfyGraph.Node("MaskToImage", new { mask = rawMask });
        // nearest-exact, NOT bilinear: the mask must stay binary. Bilinear resampling turns its edge into a ramp,
        // which the composite then cross-fades across — the same fade the softened mask used to cause, reintroduced
        // through the back door on any canvas that trips the ceiling.
        wf["174"] = ComfyGraph.Node("ImageScale", new
        {
            image = ComfyGraph.Ref("173", 0), upscale_method = "nearest-exact", width = w, height = h, crop = "disabled",
        });
        wf["175"] = ComfyGraph.Node("ImageToMask", new { image = ComfyGraph.Ref("174", 0), channel = "red" });

        image = ComfyGraph.Ref("172", 0);
        rawMask = ComfyGraph.Ref("175", 0);
    }

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"

        ResolveCanvas(wf, p, inputs, out var image, out var rawMask);
        ApplyCeiling(wf, p, inputs, CanvasSize(p, inputs), ref image, ref rawMask);

        var softMask = SoftenMask(wf, p, rawMask);

        // Base Qwen-Image is txt2img: a plain CLIPTextEncode, NOT TextEncodeQwenImageEdit(Plus).
        // The negative runs at CFG ~2.5 and must not be empty — Comfy's own template ships a single space.
        var neg = ComfyGraph.ComposeNegative(p.Str("negative"), inputs.Negative);
        if (string.IsNullOrWhiteSpace(neg)) neg = " ";
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["14"] = ComfyGraph.Node("CLIPTextEncode", new { text = neg, clip = clip0 });

        // The fill conditioning. It takes the SAME softened mask as the sampler and the composite — all three
        // consumers must agree, exactly as the reference template wires them (its Grow-and-Blur output feeds
        // ControlNetInpaintingAliMamaApply, SetLatentNoiseMask and ImageCompositeMasked alike).
        //
        // Handing this node the RAW mask instead is what dirtied the seam: the apply zeroes the control image inside
        // the mask and concatenates that mask as conditioning, so a raw mask tells the ControlNet "known pixels right
        // up to the boundary, preserve them" while SetLatentNoiseMask has the sampler regenerating `mask_grow` px
        // INSIDE that boundary. Across that ring the model is conditioned on pixels it is simultaneously being told
        // to replace, and the contradiction lands precisely on the join.
        wf["84"] = ComfyGraph.Node("ControlNetLoader", new { control_net_name = req.ControlNet ?? "" });
        wf["108"] = ComfyGraph.Node("ControlNetInpaintingAliMamaApply", new
        {
            positive = ComfyGraph.Ref("13", 0),
            negative = ComfyGraph.Ref("14", 0),
            control_net = ComfyGraph.Ref("84", 0),
            vae = vae0,
            image,
            mask = softMask,
            strength = p.Dbl("cn_strength", 1.0),
            start_percent = p.Dbl("cn_start", 0.0),
            end_percent = p.Dbl("cn_end", 1.0),
        });

        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = image, vae = vae0 });

        // BOTH directions sample through SetLatentNoiseMask — a deliberate deviation from the reference template,
        // whose outpaint branch wires VAEEncode straight in. Without it the fill's only tie to the original is
        // ControlNet residuals, which anchor structure but not exposure: measured on a real outpaint, the side
        // panels came out ~15 RGB brighter/warmer than the frame they extend (the "color balance changed" halo).
        // Re-injecting the noised original latents every step anchors the fill's tone. The latent-space seam this
        // node is known for (a binary mask blends across ONE 8px latent cell and decodes as a hard 1px line,
        // measured ~63 gradient against ~3 neighbours) is defeated by the mask's ramp instead: MaskBlurSigma makes
        // the outpaint ramp span several latent cells, over the original side only.
        wf["31"] = ComfyGraph.Node("SetLatentNoiseMask", new { samples = ComfyGraph.Ref("12", 0), mask = softMask });
        object latent = ComfyGraph.Ref("31", 0);

        wf["66"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = model0, shift = p.Dbl("auraflow", 3.1) });

        double dn = p.Dbl("denoise", DefaultDenoise);
        if (dn <= 0 || dn > 1) dn = DefaultDenoise;
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 20),
            cfg = p.Dbl("cfg", 2.5),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = dn,
            model = ComfyGraph.Ref("66", 0),
            positive = ComfyGraph.Ref("108", 0),
            negative = ComfyGraph.Ref("108", 1),
            latent_image = latent,
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });

        // Paste the generated region back over the ORIGINAL canvas, so the untouched area keeps the source pixels
        // rather than a VAE round-trip of them.
        //
        // The SAME softened mask as the other two consumers — see the class doc: do not split it. Compositing with
        // the raw pad mask instead put a hard switch on pixels the ControlNet was blind to (the apply zeroes the
        // control image inside the mask, mask_grow deep into the original) and the extension visibly failed to line
        // up with the source. The blur ramp is grown inward, over real source pixels only — nowhere near the
        // 0.5-grey pad fill — so the crossfade blends generated-vs-original and can never blend in grey.
        wf["126"] = ComfyGraph.Node("ImageCompositeMasked", new
        {
            destination = image,
            source = ComfyGraph.Ref("8", 0),
            x = 0,
            y = 0,
            resize_source = false,
            mask = softMask,
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("126", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
