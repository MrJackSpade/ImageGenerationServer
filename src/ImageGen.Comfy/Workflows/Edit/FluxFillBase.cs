namespace ImageGen.Comfy;

/// <summary>
/// INPAINT / OUTPAINT on <b>FLUX.1 Fill [dev]</b> — a model TRAINED for filling, not a txt2img base with a fill
/// adapter strapped on.
///
/// <para><b>Why this exists.</b> The Qwen + InstantX ControlNet pair (<see cref="QwenInstantXInpaintBase"/>) puts the
/// mask outside the model: a ControlNet injects residuals and everything about the join — how the fill meets the
/// original, how its exposure is anchored, what sits under the hole — has to be arranged by hand in the graph. That
/// hand-arrangement is where the seam/halo failures live. Fill takes the mask as a NATIVE input: the masked image
/// and the mask are extra channels of the model's own conditioning, seen in training, so continuing the surrounding
/// content is the model's job rather than the graph's.</para>
///
/// <para><b>What <c>InpaintModelConditioning</c> does</b> (ComfyUI core; read it before changing anything here):
/// it blanks the masked region to 0.5 grey and VAE-encodes THAT as <c>concat_latent_image</c>, passes the mask
/// itself as <c>concat_mask</c> (the blanking uses <c>round()</c>, but the model sees the SOFT values), and returns
/// the original latent for the sampler. The grey is the model's TRAINED "fill me" signal and lives only in the
/// conditioning — it is never a plate that gets alpha-blended into the output, which is why pre-filling the hole
/// (needed on the Qwen path) would be actively WRONG here.</para>
///
/// <para><b>The latent noise mask is load-bearing — measured, do not remove it.</b> Fill generates the fill region
/// a few levels darker/warmer than the surroundings (ground-truth sky fill: −6 luminance uniformly, RGB
/// −4.5/−6.0/−7.5; the same drift every Fill frontend fights, e.g. SwarmUI's "Recomposite Color Correct"), and the
/// bit-true paste-back turns that into a visible seam (step −7.5 vs ±1.5 texture noise). The obvious cure — sample
/// the WHOLE frame from the conditioning like diffusers' <c>FluxFillPipeline</c> (<c>noise_mask=false</c>) so the
/// outside pixels witness the drift and a fit can invert it — measures strictly WORSE: the per-step latent pinning
/// is what anchors the fill's CONTENT to the surroundings, and without it Fill freewheels (an empty-prompt sky fill
/// hallucinates a giant moon, −27 luminance; a region prompt goes −89). With pinning, the outside of the sampled
/// latent is the original encode, so its decode is only a VAE round-trip (0.46 levels of signal) and no outside-fit
/// can see the −6 fill drift (a Linear2 fit recovers just −6.15 → −5.02). The drift therefore has to be attacked at
/// its source; the fp8_e4m3fn load-cast is the prime suspect (the bf16 reference pipeline does not show this
/// magnitude) — test via a <c>weight_dtype</c> override before touching the graph.</para>
///
/// <para><b><c>DifferentialDiffusion</c> is the seam mechanism.</b> It consumes the latent noise mask and turns the
/// mask's grey values into a PER-PIXEL DENOISE SCHEDULE: a pixel at mask 0.3 starts denoising 30% of the way
/// through, so the transition band is progressively harmonized BY THE MODEL across steps instead of being
/// cross-faded from two finished images. It needs a SOFT mask edge to do anything, which <see cref="SoftenMask"/>
/// supplies.</para>
///
/// <para>The paste-back runs through <c>ImageCompositeMaskedColorCorrected</c> (our fork's node, adapted from
/// SwarmUI's, MIT): it fits per-channel linear corrections on HSV V and S·V over the fully-outside pixels and
/// applies them to the decode before compositing. Under the noise-mask pinning that fit only sees the VAE
/// round-trip, so it corrects the small decode tint (~1 level) — kept because it is measured-per-image, strictly
/// non-degrading, and switchable (<c>color_correct</c>).</para>
///
/// <para>Guidance 30 and CFG 1 are Fill's own values (guidance-distilled model, ComfyUI's shipped blueprint) — they
/// are not the usual Flux 3.5. Steps 20, euler/normal.</para>
/// </summary>
public abstract class FluxFillBase : EditWorkflowBase
{
    /// <summary>Only the masked region changes, and the composite enforces it.</summary>
    public override bool PreservesComposition => true;

    protected static readonly IReadOnlyList<ParamSpec> FillSchema = SharedSchema.Where(s => s.Key != "denoise").Concat(new ParamSpec[]
    {
        // Fill is guidance-distilled: real CFG stays 1 and the strength knob is FluxGuidance. 30 is the value BFL
        // ship for Fill (ComfyUI's blueprint uses it too) — an order of magnitude above Flux txt2img's 3.5, because
        // the guidance embedding is what carries "obey the mask conditioning".
        new() { Key = "guidance",  Type = ParamType.Double, Min = 1.0, Max = 60.0, Step = 0.5, Label = "Fill guidance" },
        // The soft edge that DifferentialDiffusion converts into a per-pixel denoise schedule. Wide enough to be a
        // real schedule ramp rather than a 1px step; the grow keeps it off the region being filled.
        new() { Key = "mask_blur", Type = ParamType.Int, Min = 0, Max = 31, Label = "Mask edge blur (px)" },
        new() { Key = "diffdiff",  Type = ParamType.Bool, Label = "Differential blending" },
        // Fit-and-invert the decode's tint on the outside-mask pixels before pasting back
        // (ImageCompositeMaskedColorCorrected "Linear2"). Off = plain composite.
        new() { Key = "color_correct", Type = ParamType.Bool, Label = "Seam color match" },
        // Long-edge CEILING (not a target): a canvas already under it is passed through untouched and never upscaled.
        new() { Key = "max_dimension", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Max long edge (px)" },
    }).ToArray();

    /// <summary>Produce the canvas to fill and the raw region to fill in it.</summary>
    protected abstract void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask);

    /// <summary>Pixel size of the canvas <see cref="ResolveCanvas"/> produces; (0,0) when unknown (then no scaling
    /// is emitted — we never guess at a resolution change).</summary>
    protected virtual (int W, int H) CanvasSize(ParamValues p, WorkflowInputs inputs)
        => (inputs.SourceWidth, inputs.SourceHeight);

    /// <summary>Gaussian sigma of the mask edge — the width of the crossfade band the composite blends over (and of
    /// the soft mask the model is conditioned on), so it wants to be several latent cells wide.</summary>
    private const double MaskBlurSigma = 8.0;

    /// <summary>
    /// <c>GrowMask → MaskToImage → ImageBlur → ImageToMask → MaskComposite(add)</c>.
    ///
    /// <para>The IMAGE round-trip is not incidental — no MASK-space node blurs a mask's own boundary, and
    /// <c>FeatherMask</c> is not a substitute (it ramps in from the CANVAS EDGES, which on an outpaint is exactly
    /// where the fill must be strongest).</para>
    ///
    /// <para>The trailing <c>MaskComposite "add"</c> (+ its 0..1 clamp) restores a hard 1 over the raw region, making
    /// the ramp ONE-SIDED: full strength across everything being filled, descending only outward over real source
    /// pixels. A symmetric ramp would dip below 1 inside the fill region, so the composite would blend the region's
    /// existing content (grey pad / white hole) back into the fill.</para>
    /// </summary>
    private static object SoftenMask(Dictionary<string, object> wf, ParamValues p, object rawMask)
    {
        object m = rawMask;
        int grow = p.Has("mask_grow") ? Math.Max(0, p.IntReq("mask_grow")) : 0;
        if (grow > 0)
        {
            wf["30"] = ComfyGraph.Node("GrowMask", new { mask = m, expand = grow, tapered_corners = true });
            m = ComfyGraph.Ref("30", 0);
        }

        int blur = p.IntReq("mask_blur");
        if (blur == 0) return m;

        wf["32"] = ComfyGraph.Node("MaskToImage", new { mask = m });
        wf["33"] = ComfyGraph.Node("ImageBlur", new { image = ComfyGraph.Ref("32", 0), blur_radius = blur, sigma = MaskBlurSigma });
        wf["34"] = ComfyGraph.Node("ImageToMask", new { image = ComfyGraph.Ref("33", 0), channel = "red" });
        wf["35"] = ComfyGraph.Node("MaskComposite", new
        {
            destination = ComfyGraph.Ref("34", 0), source = rawMask, x = 0, y = 0, operation = "add",
        });
        return ComfyGraph.Ref("35", 0);
    }

    /// <summary>Emit the ceiling scale for canvas AND mask, or leave both untouched. Both must travel together:
    /// the composite does not resize a mismatched mask.</summary>
    private static void ApplyCeiling(Dictionary<string, object> wf, ParamValues p, (int W, int H) canvas,
        ref object image, ref object rawMask)
    {
        int cap = p.IntReq("max_dimension");
        int longEdge = Math.Max(canvas.W, canvas.H);
        if (cap <= 0 || canvas.W <= 0 || canvas.H <= 0 || longEdge <= cap) return;

        double f = (double)cap / longEdge;
        int w = Math.Max(16, (int)(canvas.W * f) / 16 * 16);
        int h = Math.Max(16, (int)(canvas.H * f) / 16 * 16);

        wf["172"] = ComfyGraph.Node("ImageScale", new { image, upscale_method = "lanczos", width = w, height = h, crop = "disabled" });
        wf["173"] = ComfyGraph.Node("MaskToImage", new { mask = rawMask });
        // nearest-exact keeps the mask binary; bilinear would ramp its edge and stack with SoftenMask.
        wf["174"] = ComfyGraph.Node("ImageScale", new { image = ComfyGraph.Ref("173", 0), upscale_method = "nearest-exact", width = w, height = h, crop = "disabled" });
        wf["175"] = ComfyGraph.Node("ImageToMask", new { image = ComfyGraph.Ref("174", 0), channel = "red" });

        image = ComfyGraph.Ref("172", 0);
        rawMask = ComfyGraph.Ref("175", 0);
    }

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // 4/5/6 + LoadImage "10"

        ResolveCanvas(wf, p, inputs, out var image, out var rawMask);
        ApplyCeiling(wf, p, CanvasSize(p, inputs), ref image, ref rawMask);
        var softMask = SoftenMask(wf, p, rawMask);

        // Flux is a single-conditioning model: the "negative" is the positive zeroed out, and real CFG stays 1.
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["14"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("13", 0), guidance = p.DblReq("guidance") });
        wf["16"] = ComfyGraph.Node("ConditioningZeroOut", new { conditioning = ComfyGraph.Ref("13", 0) });

        // Differential blending: the soft mask becomes a per-pixel denoise SCHEDULE, so the model harmonizes the
        // transition band across steps instead of us cross-fading two finished images. See the class doc.
        object samplerModel = model0;
        if (p.Bool("diffdiff"))
        {
            wf["7"] = ComfyGraph.Node("DifferentialDiffusion", new { model = model0 });
            samplerModel = ComfyGraph.Ref("7", 0);
        }

        // The native fill conditioning — mask and masked-image are the MODEL's inputs here, not a ControlNet's.
        // noise_mask=true is load-bearing: the per-step latent pinning is what anchors the fill's CONTENT to the
        // surroundings. Sampling the full frame instead (noise_mask=false, the diffusers-reference shape) measures
        // strictly worse — Fill freewheels without the anchor (a moon hallucinates into an empty-prompt sky fill;
        // −27/−89 luminance vs −6 pinned). See the class doc before touching this.
        wf["38"] = ComfyGraph.Node("InpaintModelConditioning", new
        {
            positive = ComfyGraph.Ref("14", 0),
            negative = ComfyGraph.Ref("16", 0),
            vae = vae0,
            pixels = image,
            mask = softMask,
            noise_mask = true,
        });

        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = samplerModel,
            positive = ComfyGraph.Ref("38", 0),
            negative = ComfyGraph.Ref("38", 1),
            latent_image = ComfyGraph.Ref("38", 2),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });

        // Paste-back so everything outside the region is bit-identical to the source rather than a VAE round-trip of
        // it, with the decode's tint fitted on the outside pixels and inverted first (a small correction under the
        // noise-mask pinning — see the class doc). The same soft mask crossfades the band DifferentialDiffusion
        // already harmonized.
        wf["126"] = ComfyGraph.Node("ImageCompositeMaskedColorCorrected", new
        {
            destination = image,
            source = ComfyGraph.Ref("8", 0),
            x = 0,
            y = 0,
            mask = softMask,
            correction_method = p.Bool("color_correct") ? "Linear2" : "None",
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("126", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
