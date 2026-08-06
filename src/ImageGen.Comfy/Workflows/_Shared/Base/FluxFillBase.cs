using ImageGen.Domain;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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
public abstract class FluxFillBase : EditWorkflow<FluxFillParams>
{
    /// <summary>Only the masked region changes, and the composite enforces it.</summary>
    public override bool PreservesComposition => true;

    protected static readonly IReadOnlyList<ParamSpec> FillSchema =
    [
        .. EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise),
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double, Min = 1.0, Max = 60.0, Step = 0.5, Label = "Fill guidance" },
        new() { Key = WorkflowParamKeys.MaskBlur, Type = ParamType.Int, Min = 0, Max = 31, Label = "Mask edge blur (px)" },
        new() { Key = WorkflowParamKeys.Diffdiff,  Type = ParamType.Bool, Label = "Differential blending" },
        new() { Key = WorkflowParamKeys.ColorCorrect, Type = ParamType.Bool, Label = "Seam color match" },
        new() { Key = WorkflowParamKeys.MaxDimension, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Max long edge (px)" },
    ];

    /// <summary>Produce the canvas to fill and the raw region to fill in it.</summary>
    protected abstract void ResolveCanvas(ComfyWorkflowGraph g, FluxFillParams p, WorkflowInputs inputs,
        out Output<Slot.Image> image, out Output<Slot.Mask> rawMask);

    /// <summary>Pixel size of the canvas <see cref="ResolveCanvas"/> produces. The source is a still, so its
    /// dimensions are always measured — a zero is a broken source, refused rather than silently skipping the ceiling.</summary>
    protected virtual (int W, int H) CanvasSize(FluxFillParams p, WorkflowInputs inputs)
    {
        _ = Ensure.GreaterThanZero(inputs.SourceWidth);
        _ = Ensure.GreaterThanZero(inputs.SourceHeight);
        return (inputs.SourceWidth, inputs.SourceHeight);
    }

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
    private static Output<Slot.Mask> SoftenMask(ComfyWorkflowGraph g, FluxFillParams p, Output<Slot.Mask> rawMask)
    {
        Output<Slot.Mask> m = rawMask;
        int grow = p.MaskGrow;   // 0 = no grow; range enforced by the DTO's [Range] at the ParamsCodec boundary
        if (grow > 0)
        {
            g[Nodes.Grow] = new GrowMask { Mask = m, Expand = grow, TaperedCorners = true };
            m = GrowMask.Out(Nodes.Grow);
        }

        int blur = p.MaskBlur;
        if (blur == 0)
        {
            return m;
        }

        g[Nodes.MaskAsImage] = new MaskToImage { Mask = m };
        g[Nodes.BlurredMaskImage] = new ImageBlur { Image = MaskToImage.Out(Nodes.MaskAsImage), BlurRadius = blur, Sigma = MaskBlurSigma };
        g[Nodes.BlurredMask] = new ImageToMask { Image = ImageBlur.Out(Nodes.BlurredMaskImage), Channel = ComfyWidgets.MaskChannel.Red };
        g[Nodes.SoftMask] = new MaskComposite
        {
            Destination = ImageToMask.Out(Nodes.BlurredMask),
            Source = rawMask,
            X = 0,
            Y = 0,
            Operation = ComfyWidgets.MaskOperation.Add,
        };
        return MaskComposite.Out(Nodes.SoftMask);
    }

    /// <summary>Emit the ceiling scale for canvas AND mask, or leave both untouched. Both must travel together:
    /// the composite does not resize a mismatched mask.</summary>
    private static void ApplyCeiling(ComfyWorkflowGraph g, FluxFillParams p, (int W, int H) canvas,
        ref Output<Slot.Image> image, ref Output<Slot.Mask> rawMask)
    {
        int cap = p.MaxDimension;   // 0 = off (no ceiling); range enforced by the DTO's [Range]
        int longEdge = Math.Max(canvas.W, canvas.H);
        if (cap == 0 || longEdge <= cap)
        {
            return;   // ceiling off, or already under it (CanvasSize guarantees real dims)
        }

        double f = (double)cap / longEdge;
        int w = Math.Max(16, (int)(canvas.W * f) / 16 * 16);
        int h = Math.Max(16, (int)(canvas.H * f) / 16 * 16);

        g[Nodes.CeilingImage] = new ImageScale { Image = image, UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Width = w, Height = h, Crop = ComfyWidgets.Crop.Disabled };
        g[Nodes.CeilingMaskAsImage] = new MaskToImage { Mask = rawMask };
        // nearest-exact keeps the mask binary; bilinear would ramp its edge and stack with SoftenMask.
        g[Nodes.CeilingMaskImage] = new ImageScale { Image = MaskToImage.Out(Nodes.CeilingMaskAsImage), UpscaleMethod = ComfyWidgets.Upscale.NearestExact, Width = w, Height = h, Crop = ComfyWidgets.Crop.Disabled };
        g[Nodes.CeilingMask] = new ImageToMask { Image = ImageScale.Out(Nodes.CeilingMaskImage), Channel = ComfyWidgets.MaskChannel.Red };

        image = ImageScale.Out(Nodes.CeilingImage);
        rawMask = ImageToMask.Out(Nodes.CeilingMask);
    }

    protected override ComfyWorkflowGraph Build(FluxFillParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // 4/5/6 + LoadImage "10"

        ResolveCanvas(g, p, inputs, out Output<Slot.Image> image, out Output<Slot.Mask> rawMask);
        ApplyCeiling(g, p, CanvasSize(p, inputs), ref image, ref rawMask);
        Output<Slot.Mask> softMask = SoftenMask(g, p, rawMask);

        // Flux is a single-conditioning model: the "negative" is the positive zeroed out, and real CFG stays 1.
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = p.Guidance };
        g[Nodes.Negative] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Nodes.Positive) };

        // Differential blending: the soft mask becomes a per-pixel denoise SCHEDULE, so the model harmonizes the
        // transition band across steps instead of us cross-fading two finished images. See the class doc.
        Output<Slot.Model> samplerModel = model0;
        if (p.Diffdiff)
        {
            g[Nodes.DiffDiff] = new DifferentialDiffusion { Model = model0 };
            samplerModel = DifferentialDiffusion.Out(Nodes.DiffDiff);
        }

        // The native fill conditioning — mask and masked-image are the MODEL's inputs here, not a ControlNet's.
        // noise_mask=true is load-bearing: the per-step latent pinning is what anchors the fill's CONTENT to the
        // surroundings. Sampling the full frame instead (noise_mask=false, the diffusers-reference shape) measures
        // strictly worse — Fill freewheels without the anchor (a moon hallucinates into an empty-prompt sky fill;
        // −27/−89 luminance vs −6 pinned). See the class doc before touching this.
        g[Nodes.InpaintConditioning] = new InpaintModelConditioning
        {
            Positive = FluxGuidance.Out(Nodes.Guidance),
            Negative = ConditioningZeroOut.Out(Nodes.Negative),
            Vae = vae0,
            Pixels = image,
            Mask = softMask,
            NoiseMask = true,
        };

        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = samplerModel,
            Positive = InpaintModelConditioning.PositiveOut(Nodes.InpaintConditioning),
            Negative = InpaintModelConditioning.NegativeOut(Nodes.InpaintConditioning),
            LatentImage = InpaintModelConditioning.LatentOut(Nodes.InpaintConditioning),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };

        // Paste-back so everything outside the region is bit-identical to the source rather than a VAE round-trip of
        // it, with the decode's tint fitted on the outside pixels and inverted first (a small correction under the
        // noise-mask pinning — see the class doc). The same soft mask crossfades the band DifferentialDiffusion
        // already harmonized.
        g[Nodes.Composite] = new ImageCompositeMaskedColorCorrected
        {
            Destination = image,
            Source = VAEDecode.Out(Nodes.Decode),
            X = 0,
            Y = 0,
            Mask = softMask,
            CorrectionMethod = p.ColorCorrect ? "Linear2" : "None",
        };
        g[Nodes.Save] = new SaveImage { Images = ImageCompositeMaskedColorCorrected.Out(Nodes.Composite), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>FluxFillBase's own node ids, named by role; values are the graph-local keys, preserved exactly so the
/// emitted graph stays byte-identical.</summary>
file static class Nodes
{
    public const string Grow = "30";
    public const string MaskAsImage = "32";
    public const string BlurredMaskImage = "33";
    public const string BlurredMask = "34";
    public const string SoftMask = "35";
    public const string CeilingImage = "172";
    public const string CeilingMaskAsImage = "173";
    public const string CeilingMaskImage = "174";
    public const string CeilingMask = "175";
    public const string Positive = "13";
    public const string Guidance = "14";
    public const string Negative = "16";
    public const string DiffDiff = "7";
    public const string InpaintConditioning = "38";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Composite = "126";
    public const string Save = "9";
}

/// <summary>FLUX.1 Fill parameters, shared by the inpaint and outpaint subclasses — the shared loader head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the Fill
/// guidance/mask-softening/ceiling knobs, and (outpaint only) the per-side pads. The <c>*Req</c> reads are
/// <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings; <c>diffdiff</c>/<c>color_correct</c> are
/// booleans (absent = false); <c>mask_grow</c> is a Has-guarded nullable int; the <c>pad_*</c> reads are plain
/// <c>p.Int</c> (absent = 0); <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record FluxFillParams
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
    [JsonPropertyName(WorkflowParamKeys.Guidance)]
    [Range(1.0, 60.0)] public required double Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskBlur)]
    [Range(0, 31)] public required int MaskBlur { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Diffdiff)] public bool Diffdiff { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ColorCorrect)] public bool ColorCorrect { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaxDimension)]
    [Range(0, 4096)] public required int MaxDimension { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)] public int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadLeft)]
    [Range(0, 4096)] public int PadLeft { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTop)]
    [Range(0, 4096)] public int PadTop { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRight)]
    [Range(0, 4096)] public int PadRight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottom)]
    [Range(0, 4096)] public int PadBottom { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}