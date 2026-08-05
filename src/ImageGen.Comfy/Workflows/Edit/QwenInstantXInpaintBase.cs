using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain;

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
/// <see cref="AnimaOutpaintWorkflow"/>, where a plain checkpoint produces borders that are only stylistically
/// similar rather than a continuation).</item>
/// <item>Into <c>SetLatentNoiseMask</c>, confining denoising to the region. This is also the <b>exposure anchor</b>:
/// re-injecting the noised ORIGINAL latents outside the mask at every step lets attention harmonize the fill's tone
/// with the real image. ControlNet residuals alone do NOT anchor exposure — without this node the side panels
/// come out measurably brighter than the frame they extend (the "color balance" halo).</item>
/// <item>Into a final <c>ImageCompositeMasked</c> paste-back, so pixels outside the mask are byte-for-byte the
/// source. The VAE round-trip perturbs the whole frame otherwise, redrawing outside the mask and softening the
/// picture.</item>
/// </list>
///
/// <para>ONE mask feeds all three consumers — the ControlNet apply, <c>SetLatentNoiseMask</c> and the composite —
/// as the reference template wires it. Do not split it. Giving the ControlNet the RAW mask and the other two the
/// grown/blurred one (as <see cref="AnimaOutpaintWorkflow"/> does, a different ControlNet with different semantics)
/// makes the conditioning and the denoise disagree over a <c>mask_grow</c>-wide ring and dirties the seam. Splitting
/// the COMPOSITE off instead (raw pad mask there, softened elsewhere) puts a hard switch exactly on the ring the
/// ControlNet is blind to — the extension fails to line up.</para>
///
/// <para><b>Where we deviate from the template.</b> Two coupled changes, both on the outpaint path. (1) The grey
/// pad never reaches the sampler: <c>ImagePadForOutpaint</c> is kept only for its mask, and the actual canvas is
/// pre-filled with a blurred stretch of the source so every pixel under the fill region is scene-toned — grey is
/// the substance the halos here are made of, and removing it beats quarantining it with mask
/// geometry (see <c>QwenImageOutpaintWorkflow.ResolveCanvas</c>). (2) The template's outpaint branch drops
/// <c>SetLatentNoiseMask</c> (VAEEncode straight into KSampler) and accepts unanchored fill exposure; we keep the
/// latent mask in BOTH directions and defeat its known failure mode — a binary mask blends latents across ONE 8px
/// cell and decodes as a hard 1px line — with a ramp wide enough to span several latent cells
/// (<see cref="MaskBlurSigma"/>), held at a hard 1 over the fill region itself (<c>SoftenMask</c>).</para>
/// </summary>
public abstract class QwenInstantXInpaintBase<TParams> : EditWorkflow<TParams> where TParams : QwenInpaintParams
{
    /// <summary>Prompt describes the whole resulting picture (a generation-style prompt), not an edit instruction —
    /// base Qwen-Image is a txt2img model with a plain CLIPTextEncode, not an instruction editor.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;
    /// <summary>Only the masked region changes (and the composite guarantees it) — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;


    /// <summary>Knobs common to both directions. The shared <c>denoise</c> label ("Denoise (source ↔ motion)") is
    /// wrong here, so it is dropped and re-added per subclass.</summary>
    protected static readonly IReadOnlyList<ParamSpec> ControlNetSchema = EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Negative,       Type = ParamType.String },
        // Shift for ModelSamplingAuraFlow. Qwen-Image's trained value; the template ships 3.1.
        new() { Key = WorkflowParamKeys.Auraflow,       Type = ParamType.Double, Min = 0.0, Max = 10.0 },
        new() { Key = WorkflowParamKeys.CnStrength,    Type = ParamType.Double, Min = 0.0, Max = 2.0,  Step = 0.05, Label = "Fill control strength" },
        new() { Key = WorkflowParamKeys.CnStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0,  Step = 0.01, Label = "Control start %" },
        new() { Key = WorkflowParamKeys.CnEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0,  Step = 0.01, Label = "Control end %" },
        // mask_grow is declared PER DIRECTION (0 inpaint / 24 outpaint) — see each subclass's schema; they are not
        // interchangeable.
        //
        // Gaussian blur applied to the mask through the IMAGE round-trip in SoftenMask, matching the reference
        // template's "Grow and Blur Mask" subgraph: ImageBlur[radius 31, sigma MaskBlurSigma]. This knob is only the
        // kernel WINDOW; the ramp width is set by the per-direction sigma (see MaskBlurSigma — narrow for inpaint,
        // latent-cell-wide for outpaint, always paired with a grow that keeps the ramp off the 0.5-grey pad fill).
        // Do not "simplify" this to FeatherMask — that node ramps in from the CANVAS EDGES, not from the mask's own
        // boundary.
        new() { Key = WorkflowParamKeys.MaskBlur,      Type = ParamType.Int,    Min = 0,   Max = 31,   Label = "Mask edge blur (px)" },
        // Long-edge ceiling for the sampled canvas. 0 = no ceiling (run native). This is a CEILING, not a target: an
        // image already under it is passed through untouched and is never upscaled to meet it. Comfy's template uses
        // ImageScaleToMaxDimension, which forces the long edge to EXACTLY largest_size and so scales small sources UP;
        // that wastes VRAM on a 20B model + a 4.2GB ControlNet and silently changes the user's resolution, so the
        // decision is made here in C# (the source dims are known at submit) and a scale node is emitted only when the
        // canvas genuinely exceeds the ceiling.
        new() { Key = WorkflowParamKeys.MaxDimension,  Type = ParamType.Int,    Min = 0,  Max = 4096, Label = "Max long edge (px)" },
    }).ToArray();

    /// <summary>Produce the canvas to fill and the region to fill in it. Inpaint uses the source as-is plus the
    /// painted mask; outpaint pads the source and uses the added border as the mask.</summary>
    protected abstract void ResolveCanvas(ComfyWorkflowGraph g, QwenInpaintParams p, WorkflowInputs inputs,
        out Output<Slot.Image> image, out Output<Slot.Mask> rawMask);

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
    /// <c>max_dimension</c> ceiling is exceeded. The source is a still, so its dimensions are always measured — a
    /// zero is refused rather than silently skipping the ceiling.</summary>
    protected virtual (int W, int H) CanvasSize(QwenInpaintParams p, WorkflowInputs inputs)
    {
        Ensure.GreaterThanZero(inputs.SourceWidth);
        Ensure.GreaterThanZero(inputs.SourceHeight);
        return (inputs.SourceWidth, inputs.SourceHeight);
    }

    /// <summary>
    /// The reference template's "Grow and Blur Mask" subgraph, node for node:
    /// <c>GrowMask → MaskToImage → ImageBlur[radius, σ] → ImageToMask</c> (σ = <see cref="MaskBlurSigma"/>).
    ///
    /// <para>The IMAGE round-trip is not incidental — no MASK-space node blurs a mask's own boundary.
    /// <c>FeatherMask</c> is NOT a substitute: it ramps the mask in from the CANVAS EDGES, so on an outpaint (whose
    /// fill region touches those edges) it drove the mask toward 0 exactly where the fill had to be strongest, and
    /// <c>ImagePadForOutpaint</c>'s 0.5-grey fill showed through as a grey frame.</para>
    /// </summary>
    private Output<Slot.Mask> SoftenMask(ComfyWorkflowGraph g, QwenInpaintParams p, Output<Slot.Mask> rawMask)
    {
        Output<Slot.Mask> m = rawMask;
        int grow = p.MaskGrow;   // 0 = no grow; range enforced by the DTO's [Range] at the ParamsCodec boundary
        if (grow > 0)
        {
            g[GrowMaskNode] = new GrowMask { Mask = m, Expand = grow, TaperedCorners = true };
            m = GrowMask.Out(GrowMaskNode);
        }

        int blur = p.MaskBlur;
        if (blur == 0) return m;

        g[SoftenMaskImage] = new MaskToImage { Mask = m };
        g[SoftenBlur] = new ImageBlur { Image = MaskToImage.Out(SoftenMaskImage), BlurRadius = blur, Sigma = MaskBlurSigma };
        g[SoftenMaskBack] = new ImageToMask { Image = ImageBlur.Out(SoftenBlur), Channel = ComfyWidgets.MaskChannel.Red };

        // "add" + the node's final 0..1 clamp = max() against the raw fill mask: the ramp survives only where the
        // raw mask is 0 (over the original), and every fill pixel is restored to a hard 1. Any mask deficit over
        // the fill region mixes its flat content (grey pad / white hole) into the result through both the latent
        // re-injection and the composite, and the leak is visible far below full amplitude — even a gaussian value
        // just under 1.0 at the pad boundary leaves a visible seam, versus seam-free with a ramp held at exactly
        // 1.0 there. The ramp is therefore ONE-SIDED: hard over the fill, descending only outward across real
        // source pixels.
        g[SoftenComposite] = new MaskComposite
        {
            Destination = ImageToMask.Out(SoftenMaskBack),
            Source = rawMask,
            X = 0,
            Y = 0,
            Operation = ComfyWidgets.MaskOperation.Add,
        };
        return MaskComposite.Out(SoftenComposite);
    }

    /// <summary>Emit the ceiling scale for the canvas AND its mask, or return them untouched. Both must be resized
    /// together: the ControlNet apply and the sampler's noise mask resize a mismatched mask internally, but
    /// <c>ImageCompositeMasked</c> does not, so a mask left at the original size would break the paste-back.</summary>
    private static void ApplyCeiling(ComfyWorkflowGraph g, QwenInpaintParams p, WorkflowInputs inputs,
        (int W, int H) canvas, ref Output<Slot.Image> image, ref Output<Slot.Mask> rawMask)
    {
        int cap = p.MaxDimension;   // 0 = off (no ceiling); range enforced by the DTO's [Range]
        int longEdge = Math.Max(canvas.W, canvas.H);
        if (cap == 0 || longEdge <= cap) return;   // native: ceiling off, or under it (CanvasSize guarantees real dims)

        // Preserve aspect, then snap DOWN to a multiple of 16 (Qwen: VAE /8 + patch /2) so the latent grid is exact.
        double f = (double)cap / longEdge;
        int w = Math.Max(16, (int)(canvas.W * f) / 16 * 16);
        int h = Math.Max(16, (int)(canvas.H * f) / 16 * 16);

        g[CeilingImageScale] = new ImageScale
        {
            Image = image,
            UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
            Width = w,
            Height = h,
            Crop = ComfyWidgets.Crop.Disabled,
        };
        // The mask has to make the same trip; MASK has no scale node, so round-trip it through IMAGE.
        g[CeilingMaskImage] = new MaskToImage { Mask = rawMask };
        // nearest-exact, NOT bilinear: the mask must stay binary. Bilinear resampling turns its edge into a ramp,
        // which the composite then cross-fades across — reintroducing the seam fade through the back door on any
        // canvas that trips the ceiling.
        g[CeilingMaskScale] = new ImageScale
        {
            Image = MaskToImage.Out(CeilingMaskImage),
            UpscaleMethod = ComfyWidgets.Upscale.NearestExact,
            Width = w,
            Height = h,
            Crop = ComfyWidgets.Crop.Disabled,
        };
        g[CeilingMaskBack] = new ImageToMask { Image = ImageScale.Out(CeilingMaskScale), Channel = ComfyWidgets.MaskChannel.Red };

        image = ImageScale.Out(CeilingImageScale);
        rawMask = ImageToMask.Out(CeilingMaskBack);
    }

    /// <summary>This base's own node ids (role-named), on top of the inherited edit head
    /// (Nodes.Model/Clip/Vae/Source). Values preserved exactly so the emitted graph stays byte-identical.</summary>
    protected const string GrowMaskNode = "30";
    protected const string SoftenMaskImage = "32";
    protected const string SoftenBlur = "33";
    protected const string SoftenMaskBack = "34";
    protected const string SoftenComposite = "35";
    protected const string CeilingImageScale = "172";
    protected const string CeilingMaskImage = "173";
    protected const string CeilingMaskScale = "174";
    protected const string CeilingMaskBack = "175";
    protected const string Positive = "13";
    protected const string Negative = "14";
    protected const string ControlNet = "84";
    protected const string ControlNetApply = "108";
    protected const string Encode = "12";
    protected const string LatentNoiseMask = "31";
    protected const string ModelSampling = "66";
    protected const string Sampler = "3";
    protected const string Decode = "8";
    protected const string Composite = "126";
    protected const string Save = "9";

    protected override ComfyWorkflowGraph Build(TParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // nodes 4/5/6 + LoadImage "10"

        ResolveCanvas(g, p, inputs, out Output<Slot.Image> image, out Output<Slot.Mask> rawMask);
        ApplyCeiling(g, p, inputs, CanvasSize(p, inputs), ref image, ref rawMask);

        Output<Slot.Mask> softMask = SoftenMask(g, p, rawMask);

        // Base Qwen-Image is txt2img: a plain CLIPTextEncode, NOT TextEncodeQwenImageEdit(Plus).
        // The negative runs at CFG ~2.5 and must not be empty — Comfy's own template ships a single space.
        string neg = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative);
        if (string.IsNullOrWhiteSpace(neg)) neg = " ";
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // The fill conditioning. It takes the SAME softened mask as the sampler and the composite — all three
        // consumers must agree, exactly as the reference template wires them (its Grow-and-Blur output feeds
        // ControlNetInpaintingAliMamaApply, SetLatentNoiseMask and ImageCompositeMasked alike).
        //
        // Handing this node the RAW mask instead dirties the seam: the apply zeroes the control image inside
        // the mask and concatenates that mask as conditioning, so a raw mask tells the ControlNet "known pixels right
        // up to the boundary, preserve them" while SetLatentNoiseMask has the sampler regenerating `mask_grow` px
        // INSIDE that boundary. Across that ring the model is conditioned on pixels it is simultaneously being told
        // to replace, and the contradiction lands precisely on the join.
        g[ControlNet] = new ControlNetLoader { ControlNetName = req.RequiredControlNet() };
        g[ControlNetApply] = new ControlNetInpaintingAliMamaApply
        {
            Positive = CLIPTextEncode.Out(Positive),
            Negative = CLIPTextEncode.Out(Negative),
            ControlNet = ControlNetLoader.Out(ControlNet),
            Vae = vae0,
            Image = image,
            Mask = softMask,
            Strength = p.CnStrength,
            StartPercent = p.CnStart,
            EndPercent = p.CnEnd,
        };

        g[Encode] = new VAEEncode { Pixels = image, Vae = vae0 };

        // BOTH directions sample through SetLatentNoiseMask — a deliberate deviation from the reference template,
        // whose outpaint branch wires VAEEncode straight in. Without it the fill's only tie to the original is
        // ControlNet residuals, which anchor structure but not exposure: the side panels then come out measurably
        // brighter/warmer than the frame they extend (the "color balance changed" halo). Re-injecting the noised
        // original latents every step anchors the fill's tone. The latent-space seam this node is known for (a
        // binary mask blends across ONE 8px latent cell and decodes as a hard 1px line) is defeated by the mask's
        // ramp instead: MaskBlurSigma makes the outpaint ramp span several latent cells, over the original side only.
        g[LatentNoiseMask] = new SetLatentNoiseMask { Samples = VAEEncode.Out(Encode), Mask = softMask };
        Output<Slot.Latent> latent = SetLatentNoiseMask.Out(LatentNoiseMask);

        g[ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = p.Auraflow };

        double dn = p.Denoise;
        g[Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = dn,
            Model = ModelSamplingAuraFlow.Out(ModelSampling),
            Positive = ControlNetInpaintingAliMamaApply.PositiveOut(ControlNetApply),
            Negative = ControlNetInpaintingAliMamaApply.NegativeOut(ControlNetApply),
            LatentImage = latent,
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };

        // Paste the generated region back over the ORIGINAL canvas, so the untouched area keeps the source pixels
        // rather than a VAE round-trip of them.
        //
        // The SAME softened mask as the other two consumers — see the class doc: do not split it. Compositing with
        // the raw pad mask instead would put a hard switch on pixels the ControlNet is blind to (the apply zeroes the
        // control image inside the mask, mask_grow deep into the original) and the extension would fail to line
        // up with the source. The blur ramp is grown inward, over real source pixels only — nowhere near the
        // 0.5-grey pad fill — so the crossfade blends generated-vs-original and can never blend in grey.
        g[Composite] = new ImageCompositeMasked
        {
            Destination = image,
            Source = VAEDecode.Out(Decode),
            X = 0,
            Y = 0,
            ResizeSource = false,
            Mask = softMask,
        };
        g[Save] = new SaveImage { Images = ImageCompositeMasked.Out(Composite), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Base Qwen-Image + InstantX-ControlNet inpaint/outpaint parameters, shared by the inpaint and outpaint
/// subclasses — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed
/// <c>LoadModel</c>), the sampler settings + <c>denoise</c>, the AuraFlow shift, the ControlNet
/// strength/start/end, the mask-softening/ceiling knobs, and (outpaint only) the per-side pads. The <c>*Req</c> reads
/// are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c>/<c>negative</c> are nullable strings; <c>mask_grow</c> is
/// a Has-guarded nullable int; the <c>pad_*</c> reads are plain <c>p.Int</c> (absent = 0); <c>seed</c> is the app's
/// single-sourced seed (defaulted).</summary>
public abstract record QwenInpaintParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    /// <summary>Change amount / fill strength — declared here without a range because the valid floor differs per
    /// direction (inpaint 0.2, outpaint 0.5); each concrete record overrides it with its own <c>[Range]</c>.</summary>
    [JsonPropertyName(WorkflowParamKeys.Denoise)]      public virtual required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]     public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Auraflow)]
    [Range(0.0, 10.0)]                                 public required double Auraflow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CnStrength)]
    [Range(0.0, 2.0)]                                  public required double CnStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CnStart)]
    [Range(0.0, 1.0)]                                  public required double CnStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CnEnd)]
    [Range(0.0, 1.0)]                                  public required double CnEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskBlur)]
    [Range(0, 31)]                                     public required int MaskBlur { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaxDimension)]
    [Range(0, 4096)]                                   public required int MaxDimension { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)]                                     public int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadLeft)]
    [Range(0, 4096)]                                   public int PadLeft { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTop)]
    [Range(0, 4096)]                                   public int PadTop { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRight)]
    [Range(0, 4096)]                                   public int PadRight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottom)]
    [Range(0, 4096)]                                   public int PadBottom { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
