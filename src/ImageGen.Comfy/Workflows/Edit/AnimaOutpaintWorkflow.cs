using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain.CodeAnalysis;

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
public sealed class AnimaOutpaintWorkflow : EditWorkflow<AnimaOutpaintParams>
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
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema = EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Fill strength" },
        new() { Key = WorkflowParamKeys.PadLeft,        Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,         Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,       Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom,      Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        new() { Key = WorkflowParamKeys.Feather,         Type = ParamType.Int, Min = 0, Max = 256, Label = "Seam feather (px)" },
        new() { Key = WorkflowParamKeys.MaskGrow,       Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
        new() { Key = WorkflowParamKeys.LlliteStrength, Type = ParamType.Double, Min = 0.0, Max = 2.0, Step = 0.01, Label = "Inpaint control strength" },
        new() { Key = WorkflowParamKeys.LlliteStart,    Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Control start %" },
        new() { Key = WorkflowParamKeys.LlliteEnd,      Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Control end %" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
    }).ToArray();

    protected override ComfyWorkflowGraph Build(AnimaOutpaintParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // nodes 4/5/6 + LoadImage "10"

        if (LoaderKindWire.Parse(p.Loader) == LoaderKind.Checkpoint && p.ClipSkip is int clipSkip && clipSkip > 0)
        {
            g[Nodes.ClipSkip] = new CLIPSetLastLayer { Clip = clip0, StopAtClipLayer = -Math.Abs(clipSkip) };
            clip0 = CLIPSetLastLayer.ClipOut(Nodes.ClipSkip);
        }

        // Negative = the config default with the UI negative (inputs.Negative) appended — never replaced.
        string? rp = p.RequiredPrefix;
        string prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        string neg = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative);
        g[Nodes.Positive] = new CLIPTextEncode { Text = prefix + inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // Pad the source on each side — the enlarged canvas (slot 0) + the added-border mask (slot 1). Feathering
        // softens the mask edge so the generated margin blends into the original instead of leaving a hard seam.
        g[Nodes.Pad] = new ImagePadForOutpaint
        {
            Image = LoadImage.ImageOut(EditNodes.Source),
            Left = p.PadLeft,
            Top = p.PadTop,
            Right = p.PadRight,
            Bottom = p.PadBottom,
            Feathering = p.Feather,
        };

        // The fill-conditioning that a base checkpoint lacks: patch the Anima model with the 4-channel inpainting
        // ControlNet-LLLite (kohya-ss Anima-LLLite). It takes the padded RGB + the border MASK (white = fill) and
        // conditions generation on the KNOWN pixels + hole, so the border CONTINUES the existing structure instead of
        // inventing over gray. The node zeroes the RGB inside the mask itself, so the padded canvas (gray border) is
        // fine as the control image. Uses the raw pad mask (not the grown one) so the control keeps every known pixel.
        g[Nodes.LlliteApply] = new AnimaLLLiteApply
        {
            Model = model0,
            LlliteName = req.RequiredControlNet(),
            Image = ImagePadForOutpaint.ImageOut(Nodes.Pad),
            Mask = ImagePadForOutpaint.MaskOut(Nodes.Pad),
            Strength = p.LlliteStrength,
            StartPercent = p.LlliteStart,
            EndPercent = p.LlliteEnd,
            PreserveWrapper = true,
        };
        Output<Slot.Model> ksModel = AnimaLLLiteApply.Out(Nodes.LlliteApply);

        // Encode the padded canvas; confine denoising to the padded (masked) border so the original region is kept.
        // GrowMask expands the border mask slightly into the original (mirrors AnimaInpaintWorkflow) so the seam blends.
        g[Nodes.Encode] = new VAEEncode { Pixels = ImagePadForOutpaint.ImageOut(Nodes.Pad), Vae = vae0 };
        Output<Slot.Mask> maskSrc = ImagePadForOutpaint.MaskOut(Nodes.Pad);
        int grow = p.MaskGrow;   // bound enforced by the DTO's [Range] at the ParamsCodec boundary
        if (grow > 0)
        {
            g[Nodes.GrowMaskNode] = new GrowMask { Mask = maskSrc, Expand = grow, TaperedCorners = true };
            maskSrc = GrowMask.Out(Nodes.GrowMaskNode);
        }
        g[Nodes.NoiseMask] = new SetLatentNoiseMask { Samples = VAEEncode.Out(Nodes.Encode), Mask = maskSrc };

        double dn = p.Denoise;
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = dn,
            Model = ksModel,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = SetLatentNoiseMask.Out(Nodes.NoiseMask),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflow.Nodes).</summary>
file static class Nodes
{
    public const string ClipSkip = "19";
    public const string Positive = "13";
    public const string Negative = "14";
    public const string Pad = "20";
    public const string LlliteApply = "40";
    public const string Encode = "12";
    public const string GrowMaskNode = "30";
    public const string NoiseMask = "31";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}

/// <summary>Anima LLLite-outpaint parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings + fill <c>denoise</c>, the required seam
/// <c>feather</c> + <c>mask_grow</c>, the LLLite strength/start/end (all <c>required</c>), the optional prefix/negative
/// (nullable strings) + Has-guarded <c>clip_skip</c>, and the Has-guarded per-side <c>pad_*</c> ints. <c>seed</c> is the
/// app's single-sourced seed (defaulted).</summary>
public sealed record AnimaOutpaintParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]         public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]    public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]       public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]  public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]      public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]        public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]      public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.0, 1.0)]                                    public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadLeft)]
    [Range(0, 4096)]                                     public int PadLeft { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTop)]
    [Range(0, 4096)]                                     public int PadTop { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRight)]
    [Range(0, 4096)]                                     public int PadRight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottom)]
    [Range(0, 4096)]                                     public int PadBottom { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Feather)]
    [Range(0, 256)]                                      public required int Feather { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)]                                       public required int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LlliteStrength)]
    [Range(0.0, 2.0)]                                    public required double LlliteStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LlliteStart)]
    [Range(0.0, 1.0)]                                    public required double LlliteStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LlliteEnd)]
    [Range(0.0, 1.0)]                                    public required double LlliteEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]       public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)]
    [AllowNullable("null = the config didn't set clip skip; the CLIPSetLastLayer node is emitted only when set, distinct from a real 0")] public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]           public long Seed { get; init; }
}
