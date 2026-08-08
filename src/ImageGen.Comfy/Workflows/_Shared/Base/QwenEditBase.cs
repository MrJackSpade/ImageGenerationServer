using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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
    public override IReadOnlyList<ParamSpec> Schema =>
    [
        .. EditWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.MaskLeftPct,   Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask left %",   Help = "Fence the model out of the left N% of the canvas" },
        new() { Key = WorkflowParamKeys.MaskRightPct,  Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask right %",  Help = "Fence the model out of the right N% of the canvas" },
        new() { Key = WorkflowParamKeys.MaskTopPct,    Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask top %",    Help = "Fence the model out of the top N% of the canvas" },
        new() { Key = WorkflowParamKeys.MaskBottomPct, Type = ParamType.Int, Min = CanvasMaskConstants.MinSidePct, Max = CanvasMaskConstants.MaxSidePct, Step = 1, Label = "Mask bottom %", Help = "Fence the model out of the bottom N% of the canvas" },
    ];

    /// <summary>
    /// The largest multiple of <see cref="CanvasMaskConstants.LatentAlignPx"/> that is <c>&lt;= n</c> — the sampled
    /// rectangle must align to the VAE/patch stride, so it is rounded down and scaled back up on the way out.
    /// </summary>
    private static int AlignDown(int n) =>
        Math.Max(CanvasMaskConstants.LatentAlignPx, n - (n % CanvasMaskConstants.LatentAlignPx));

    /// <summary>
    /// The drawing rectangle (X, Y, W, H) in SOURCE pixels left over once each side's blocked percentage is removed.
    /// Null when no side is blocked (the graph is then byte-identical to the unmasked one). Throws when the request is
    /// degenerate — opposing margins that leave no room, or a rectangle too small to survive the latent's 8× downscale
    /// — rather than silently clamping to something the caller didn't ask for.
    /// </summary>
    private static (int X, int Y, int W, int H)? MaskGeom(int pctL, int pctR, int pctT, int pctB, int sw, int sh)
    {
        if (pctL == 0 && pctR == 0 && pctT == 0 && pctB == 0)
        {
            return null;   // no mask
        }

        foreach ((string? name, int pct) in new[] { (WorkflowParamKeys.MaskLeftPct, pctL), (WorkflowParamKeys.MaskRightPct, pctR), (WorkflowParamKeys.MaskTopPct, pctT), (WorkflowParamKeys.MaskBottomPct, pctB) })
        {
            _ = Ensure.Between(pct, CanvasMaskConstants.MinSidePct, CanvasMaskConstants.MaxSidePct, name);
        }

        if (pctL + pctR > 100 - CanvasMaskConstants.MinOpenPctPerAxis)
        {
            throw new ArgumentException($"mask_left_pct + mask_right_pct = {pctL + pctR}% leaves no width for the model to draw in.");
        }

        if (pctT + pctB > 100 - CanvasMaskConstants.MinOpenPctPerAxis)
        {
            throw new ArgumentException($"mask_top_pct + mask_bottom_pct = {pctT + pctB}% leaves no height for the model to draw in.");
        }

        int x = sw * pctL / 100, y = sh * pctT / 100;
        int w = sw - x - (sw * pctR / 100), h = sh - y - (sh * pctB / 100);
        if (w < CanvasMaskConstants.MinRectPx || h < CanvasMaskConstants.MinRectPx)
        {
            throw new ArgumentException($"the masked drawing rectangle is {w}×{h}px, below the {CanvasMaskConstants.MinRectPx}px minimum.");
        }

        return (x, y, w, h);
    }

    /// <summary>The TextEncodeQwenImageEditPlus node's variable input-field names carried in the encode's overflow bag.
    /// The fixed <c>clip</c>/<c>image1</c>/<c>prompt</c> are typed properties on the node; the per-reference image slots
    /// come from the <c>reference_inputs</c> param. <c>vae</c> is added only when at least one reference is present.</summary>
    private static class Inputs
    {
        public const string Vae = "vae";
    }

    protected override ComfyWorkflowGraph Build(QwenEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        string instruction = inputs.Positive;
        IReadOnlyList<string> refNames = inputs.ImageReferences;

        // Default resolution normalisation (FluxKontextImageScale snaps to a Qwen-trained bucket) + the danamir blur
        // fix. The text-encode image and the VAEEncode both come from that scaled image, and we build the ref latent
        // ourselves (VAE off the text-encode so it can't force-rescale) -> ref latent matches sample latent, no
        // per-turn resample -> no compounding blur over a multi-turn conversation.
        g[Nodes.KontextScale] = new FluxKontextImageScale { Image = LoadImage.ImageOut(EditNodes.Source) };

        string[] qInputs = p.ReferenceInputs ?? [];
        // Capacity is the smaller of the model's reference_max and the graph's available image slots — both hard
        // structural limits. More references than that is REFUSED, not silently truncated to fit.
        int refCapacity = Math.Min(p.ReferenceMax ?? 0, qInputs.Length);
        if (refNames.Count > refCapacity)
        {
            throw new RenderValidationException($"This configuration accepts at most {refCapacity} reference image(s); got {refNames.Count}.");
        }

        int qn = refNames.Count;
        Dictionary<string, object> encRefs = [];
        for (int i = 0; i < qn; i++)                          // each reference: load + scale into image2/image3
        {
            string load = $"{40 + (i * 2)}", scale = $"{41 + (i * 2)}";
            g[load] = new LoadImage { Image = refNames[i] };
            g[scale] = new FluxKontextImageScale { Image = LoadImage.ImageOut(load) };
            encRefs[qInputs[i]] = FluxKontextImageScale.Out(scale);
        }

        g[Nodes.SourceEncode] = new VAEEncode { Pixels = FluxKontextImageScale.Out(Nodes.KontextScale), Vae = vae0 };
        Output<Slot.Conditioning> cond;
        if (qn > 0)
        {
            // The stitch method is the CONFIG's declaration of what its model supports: Qwen-Image handles
            // index_timestep_zero, but LongCat (plain-Flux modulation) crashes on it and takes index — see
            // ComfyWidgets.ReferenceLatents. An unknown value is refused here rather than surfacing as a Comfy error.
            string refMethod = p.ReferenceLatentsMethod switch
            {
                ComfyWidgets.ReferenceLatents.Offset or ComfyWidgets.ReferenceLatents.Index or
                ComfyWidgets.ReferenceLatents.UxoUno or ComfyWidgets.ReferenceLatents.IndexTimestepZero => p.ReferenceLatentsMethod,
                _ => throw new ArgumentException($"unknown reference_latents_method '{p.ReferenceLatentsMethod}'."),
            };
            encRefs[Inputs.Vae] = vae0;
            g[Nodes.Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = FluxKontextImageScale.Out(Nodes.KontextScale), Prompt = instruction, Extra = encRefs };
            g[Nodes.MultiRefLatent] = new FluxKontextMultiReferenceLatentMethod { Conditioning = TextEncodeQwenImageEditPlus.Out(Nodes.Encode), ReferenceLatentsMethod = refMethod };
            cond = FluxKontextMultiReferenceLatentMethod.Out(Nodes.MultiRefLatent);
        }
        else
        {
            g[Nodes.Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = FluxKontextImageScale.Out(Nodes.KontextScale), Prompt = instruction };
            g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = TextEncodeQwenImageEditPlus.Out(Nodes.Encode), Latent = VAEEncode.Out(Nodes.SourceEncode) };
            cond = ReferenceLatent.Out(Nodes.RefLatent);
        }

        g[Nodes.ZeroNegative] = new ConditioningZeroOut { Conditioning = cond };
        Output<Slot.Model> ksModel = model0;
        if (!Aio)                                             // standard 2511 needs ModelSamplingAuraFlow + CFGNorm
        {
            g[Nodes.ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = 3.1 };
            g[Nodes.CfgNorm] = new CFGNorm { Model = ModelSamplingAuraFlow.Out(Nodes.ModelSampling), Strength = 1.0 };
            ksModel = CFGNorm.Out(Nodes.CfgNorm);
        }
        // Optional canvas mask, implemented as a REFRAME (see Schema). Sample on a latent shaped like the drawing
        // rectangle instead of the full canvas, then paste the decoded result back onto a white canvas at the
        // rectangle's offset. The model's fill-the-frame bias then works FOR us: given a 66%-tall frame it draws a
        // crouch at native scale. The conditioning is untouched — node 13 still encodes the FULL source and node 30's
        // reference latent is still the full-frame latent — so identity and the character's true scale are preserved.
        static int Pct(int? v)
        {
            return v ?? 0;   // a canvas-mask side %, absent = 0 (no mask on that side)
        }
        // A Qwen edit's source is a still, so its dimensions are ALWAYS measured — a zero is a broken source, not a
        // valid state. Refuse it rather than silently drop a requested canvas mask. (MaskGeom returns null when no
        // mask side is set, so an unmasked edit still no-ops.)
        _ = Ensure.GreaterThanZero(inputs.SourceWidth);
        _ = Ensure.GreaterThanZero(inputs.SourceHeight);
        (int X, int Y, int W, int H)? rect = MaskGeom(Pct(p.MaskLeftPct), Pct(p.MaskRightPct), Pct(p.MaskTopPct), Pct(p.MaskBottomPct),
                            inputs.SourceWidth, inputs.SourceHeight);

        Output<Slot.Latent> sampleLatent = VAEEncode.Out(Nodes.SourceEncode);
        if (rect is (int, int, int rw, int rh))
        {
            // Sample at the rectangle, aligned down to the VAE/patch stride; a blank white canvas is the starting
            // latent because denoise is 1.0, so only its SHAPE matters, not its content.
            g[Nodes.RectCanvas] = new EmptyImageLiteral { Width = AlignDown(rw), Height = AlignDown(rh), BatchSize = 1, Color = CanvasMaskConstants.BlockedFillRgb };
            g[Nodes.RectEncode] = new VAEEncode { Pixels = EmptyImageLiteral.Out(Nodes.RectCanvas), Vae = vae0 };
            sampleLatent = VAEEncode.Out(Nodes.RectEncode);
        }

        g[Nodes.Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = ksModel,
            Positive = cond,
            Negative = ConditioningZeroOut.Out(Nodes.ZeroNegative),
            LatentImage = sampleLatent,
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };

        Output<Slot.Image> output = VAEDecode.Out(Nodes.Decode);
        if (rect is (int px, int py, int pw, int ph))
        {
            // Undo the stride rounding, paste onto a white canvas at the rectangle's offset (both in source pixels),
            // then match the unmasked path's output dimensions exactly — GetImageSize reads the Kontext bucket node 11
            // chose, so a masked and an unmasked pose of the same portrait land on identical canvases and keep a
            // consistent sprite scale. When the source is already a bucket size this final scale is an identity.
            g[Nodes.RectResize] = new ImageScale { Image = VAEDecode.Out(Nodes.Decode), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Width = pw, Height = ph, Crop = ComfyWidgets.Crop.Disabled };
            g[Nodes.PasteCanvas] = new EmptyImageLiteral { Width = inputs.SourceWidth, Height = inputs.SourceHeight, BatchSize = 1, Color = CanvasMaskConstants.BlockedFillRgb };
            g[Nodes.Composite] = new ImageCompositePaste { Destination = EmptyImageLiteral.Out(Nodes.PasteCanvas), Source = ImageScale.Out(Nodes.RectResize), X = px, Y = py, ResizeSource = false };
            g[Nodes.OutputSize] = new GetImageSize { Image = FluxKontextImageScale.Out(Nodes.KontextScale) };
            g[Nodes.OutputScale] = new ImageScaleFromSize { Image = ImageCompositePaste.Out(Nodes.Composite), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Width = GetImageSize.WidthOut(Nodes.OutputSize), Height = GetImageSize.HeightOut(Nodes.OutputSize), Crop = ComfyWidgets.Crop.Disabled };
            output = ImageScaleFromSize.Out(Nodes.OutputScale);
        }

        g[Nodes.Save] = new SaveImage { Images = output, FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>QwenEditBase's own node ids (role-named), on top of the inherited edit head
/// (EditNodes.Model/Clip/Vae/Source). The per-reference load/scale nodes stay computed ($"{40+i*2}"). Values
/// preserved exactly so the emitted graph stays byte-identical.</summary>
file static class Nodes
{
    public const string KontextScale = "11";
    public const string Encode = "13";
    public const string SourceEncode = "14";
    public const string RefLatent = "30";
    public const string MultiRefLatent = "70";
    public const string ZeroNegative = "26";
    public const string ModelSampling = "2";
    public const string CfgNorm = "7";
    public const string RectCanvas = "80";
    public const string RectEncode = "81";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string RectResize = "82";
    public const string PasteCanvas = "83";
    public const string Composite = "84";
    public const string OutputSize = "85";
    public const string OutputScale = "86";
    public const string Save = "9";
}

/// <summary>Qwen-Image-Edit parameters, shared by the standard and AIO subclasses — the shared loader head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the
/// optional reference cap + encode-node slot names, the required per-model reference-latent stitch method, and the
/// four canvas-mask side percentages. The <c>*Req</c> reads
/// are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings, <c>reference_max</c> and each
/// <c>mask_*_pct</c> are Has-guarded nullable ints, <c>reference_inputs</c> is a nullable string array (treated as
/// empty when absent); <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record QwenEditParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]
    [AllowNullable("null = the config declares no reference-image cap; distinct from a real 0 cap")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceInputs)] public string[]? ReferenceInputs { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceLatentsMethod)] public required string ReferenceLatentsMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskLeftPct)]
    [Range(CanvasMaskConstants.MinSidePct, CanvasMaskConstants.MaxSidePct)]
    [AllowNullable("null = the config didn't set this mask/pad percentage; distinct from a real 0%")] public int? MaskLeftPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskRightPct)]
    [Range(CanvasMaskConstants.MinSidePct, CanvasMaskConstants.MaxSidePct)]
    [AllowNullable("null = the config didn't set this mask/pad percentage; distinct from a real 0%")] public int? MaskRightPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskTopPct)]
    [Range(CanvasMaskConstants.MinSidePct, CanvasMaskConstants.MaxSidePct)]
    [AllowNullable("null = the config didn't set this mask/pad percentage; distinct from a real 0%")] public int? MaskTopPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskBottomPct)]
    [Range(CanvasMaskConstants.MinSidePct, CanvasMaskConstants.MaxSidePct)]
    [AllowNullable("null = the config didn't set this mask/pad percentage; distinct from a real 0%")] public int? MaskBottomPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}