using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// Qwen-Image-Edit (<c>TextEncodeQwenImageEditPlus</c>). Two models run this topology — the standard split model
/// and the all-in-one (AIO) rapid checkpoint — so they are two separate workflow classes over this shared base.
/// The only difference is the AIO bakes its own sampling, so the standard path inserts ModelSamplingAuraFlow+CFGNorm
/// and the AIO does not (<see cref="Aio"/>).
/// </summary>
public abstract class QwenEditBase : EditWorkflow<QwenEditParams>
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
    public override IReadOnlyList<ParamSpec> Schema => EditWorkflowBase.SharedSchema.Concat(new ParamSpec[]
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

    /// <summary>The TextEncodeQwenImageEditPlus node's variable input-field names carried in the encode's overflow bag.
    /// The fixed <c>clip</c>/<c>image1</c>/<c>prompt</c> are typed properties on the node; the per-reference image slots
    /// come from the <c>reference_inputs</c> param. <c>vae</c> is added only when at least one reference is present.</summary>
    private static class Inputs
    {
        public const string Vae = "vae";
    }

    protected override ComfyWorkflowGraph Build(QwenEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        string instruction = inputs.Positive;
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;

        // Default resolution normalisation (FluxKontextImageScale snaps to a Qwen-trained bucket) + the danamir blur
        // fix. The text-encode image and the VAEEncode both come from that scaled image, and we build the ref latent
        // ourselves (VAE off the text-encode so it can't force-rescale) -> ref latent matches sample latent, no
        // per-turn resample -> no compounding blur over a multi-turn conversation.
        g[KontextScale] = new FluxKontextImageScale { Image = LoadImage.ImageOut(Nodes.Source) };

        string[] qInputs = p.ReferenceInputs ?? Array.Empty<string>();
        // Capacity is the smaller of the model's reference_max and the graph's available image slots — both hard
        // structural limits. More references than that is REFUSED, not silently truncated to fit.
        int refCapacity = Math.Min(p.ReferenceMax ?? 0, qInputs.Length);
        if (refNames.Count > refCapacity)
            throw new RenderValidationException($"This configuration accepts at most {refCapacity} reference image(s); got {refNames.Count}.");
        int qn = refNames.Count;
        Dictionary<string, object> encRefs = new Dictionary<string, object>();
        for (int i = 0; i < qn; i++)                          // each reference: load + scale into image2/image3
        {
            string load = $"{40 + i * 2}", scale = $"{41 + i * 2}";
            g[load] = new LoadImage { Image = refNames[i] };
            g[scale] = new FluxKontextImageScale { Image = LoadImage.ImageOut(load) };
            encRefs[qInputs[i]] = FluxKontextImageScale.Out(scale);
        }
        g[SourceEncode] = new VAEEncode { Pixels = FluxKontextImageScale.Out(KontextScale), Vae = vae0 };
        Output<Slot.Conditioning> cond;
        if (qn > 0)
        {
            encRefs[Inputs.Vae] = vae0;
            g[Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = FluxKontextImageScale.Out(KontextScale), Prompt = instruction, Extra = encRefs };
            g[MultiRefLatent] = new FluxKontextMultiReferenceLatentMethod { Conditioning = TextEncodeQwenImageEditPlus.Out(Encode), ReferenceLatentsMethod = "index_timestep_zero" };
            cond = FluxKontextMultiReferenceLatentMethod.Out(MultiRefLatent);
        }
        else
        {
            g[Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = FluxKontextImageScale.Out(KontextScale), Prompt = instruction };
            g[RefLatent] = new ReferenceLatent { Conditioning = TextEncodeQwenImageEditPlus.Out(Encode), Latent = VAEEncode.Out(SourceEncode) };
            cond = ReferenceLatent.Out(RefLatent);
        }
        g[ZeroNegative] = new ConditioningZeroOut { Conditioning = cond };
        Output<Slot.Model> ksModel = model0;
        if (!Aio)                                             // standard 2511 needs ModelSamplingAuraFlow + CFGNorm
        {
            g[ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = 3.1 };
            g[CfgNorm] = new CFGNorm { Model = ModelSamplingAuraFlow.Out(ModelSampling), Strength = 1.0 };
            ksModel = CFGNorm.Out(CfgNorm);
        }
        // Optional canvas mask, implemented as a REFRAME (see Schema). Sample on a latent shaped like the drawing
        // rectangle instead of the full canvas, then paste the decoded result back onto a white canvas at the
        // rectangle's offset. The model's fill-the-frame bias then works FOR us: given a 66%-tall frame it draws a
        // crouch at native scale. The conditioning is untouched — node 13 still encodes the FULL source and node 30's
        // reference latent is still the full-frame latent — so identity and the character's true scale are preserved.
        int Pct(int? v) => v ?? 0;   // a canvas-mask side %, absent = 0 (no mask on that side)
        // A Qwen edit's source is a still, so its dimensions are ALWAYS measured — a zero is a broken source, not a
        // valid state. Refuse it rather than silently drop a requested canvas mask. (MaskGeom returns null when no
        // mask side is set, so an unmasked edit still no-ops.)
        Ensure.GreaterThanZero(inputs.SourceWidth);
        Ensure.GreaterThanZero(inputs.SourceHeight);
        (int X, int Y, int W, int H)? rect = MaskGeom(Pct(p.MaskLeftPct), Pct(p.MaskRightPct), Pct(p.MaskTopPct), Pct(p.MaskBottomPct),
                            inputs.SourceWidth, inputs.SourceHeight);

        Output<Slot.Latent> sampleLatent = VAEEncode.Out(SourceEncode);
        if (rect is (int rx, int ry, int rw, int rh))
        {
            // Sample at the rectangle, aligned down to the VAE/patch stride; a blank white canvas is the starting
            // latent because denoise is 1.0, so only its SHAPE matters, not its content.
            g[RectCanvas] = new EmptyImageLiteral { Width = AlignDown(rw), Height = AlignDown(rh), BatchSize = 1, Color = CanvasMaskConstants.BlockedFillRgb };
            g[RectEncode] = new VAEEncode { Pixels = EmptyImageLiteral.Out(RectCanvas), Vae = vae0 };
            sampleLatent = VAEEncode.Out(RectEncode);
        }

        g[Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = ksModel,
            Positive = cond,
            Negative = ConditioningZeroOut.Out(ZeroNegative),
            LatentImage = sampleLatent,
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };

        Output<Slot.Image> output = VAEDecode.Out(Decode);
        if (rect is (int px, int py, int pw, int ph))
        {
            // Undo the stride rounding, paste onto a white canvas at the rectangle's offset (both in source pixels),
            // then match the unmasked path's output dimensions exactly — GetImageSize reads the Kontext bucket node 11
            // chose, so a masked and an unmasked pose of the same portrait land on identical canvases and keep a
            // consistent sprite scale. When the source is already a bucket size this final scale is an identity.
            g[RectResize] = new ImageScale { Image = VAEDecode.Out(Decode), UpscaleMethod = "lanczos", Width = pw, Height = ph, Crop = "disabled" };
            g[PasteCanvas] = new EmptyImageLiteral { Width = inputs.SourceWidth, Height = inputs.SourceHeight, BatchSize = 1, Color = CanvasMaskConstants.BlockedFillRgb };
            g[Composite] = new ImageCompositePaste { Destination = EmptyImageLiteral.Out(PasteCanvas), Source = ImageScale.Out(RectResize), X = px, Y = py, ResizeSource = false };
            g[OutputSize] = new GetImageSize { Image = FluxKontextImageScale.Out(KontextScale) };
            g[OutputScale] = new ImageScaleFromSize { Image = ImageCompositePaste.Out(Composite), UpscaleMethod = "lanczos", Width = GetImageSize.WidthOut(OutputSize), Height = GetImageSize.HeightOut(OutputSize), Crop = "disabled" };
            output = ImageScaleFromSize.Out(OutputScale);
        }
        g[Save] = new SaveImage { Images = output, FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>Qwen-Image-Edit parameters, shared by the standard and AIO subclasses — the shared loader head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the
/// optional reference cap + encode-node slot names, and the four canvas-mask side percentages. The <c>*Req</c> reads
/// are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings, <c>reference_max</c> and each
/// <c>mask_*_pct</c> are Has-guarded nullable ints, <c>reference_inputs</c> is a nullable string array (treated as
/// empty when absent); <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record QwenEditParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]          public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]     public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]        public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]           public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]             public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]         public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]       public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]    public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceInputs)] public string[]? ReferenceInputs { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskLeftPct)]     public int? MaskLeftPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskRightPct)]    public int? MaskRightPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskTopPct)]      public int? MaskTopPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskBottomPct)]   public int? MaskBottomPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]            public long Seed { get; init; }
}
