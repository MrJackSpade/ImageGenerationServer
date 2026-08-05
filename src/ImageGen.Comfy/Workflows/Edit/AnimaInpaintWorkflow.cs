using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// Masked img2img INPAINT using a standard generation checkpoint (Anima). Reuses the edit rails: the source image is
/// uploaded with the region-to-regenerate painted into its ALPHA channel, so ComfyUI's <c>LoadImage</c> (node "10",
/// emitted by <see cref="EditWorkflow{TParams}.LoadModel"/>) yields BOTH the RGB pixels (IMAGE, slot 0) and the mask
/// (MASK, slot 1) from one upload — no separate mask file or request field. Only the masked region is denoised
/// (<c>SetLatentNoiseMask</c>) at a PARTIAL denoise, so the character's identity/structure is preserved while the
/// prompt drives the change (the target use: same character, new facial expression).
///
/// The edit submit path carries the positive (= the instruction) and an optional UI negative, applying no prefix, so
/// this workflow adds the prefix itself: <c>inputs.Positive</c> carries the user's FULL booru-tag prompt, the quality
/// prefix comes from <c>required_prefix</c>, and the negative is the config default (<c>negative</c>) with the UI
/// negative (<c>inputs.Negative</c>) appended — never replaced (see <see cref="ComfyGraph.ComposeNegative"/>).
/// </summary>
public sealed class AnimaInpaintWorkflow : EditWorkflow<AnimaInpaintParams>
{
    public override string Name => "anima-inpaint";

    /// <summary>inputs.Positive carries the user's FULL prompt for the picture, not an edit instruction.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;
    /// <summary>Local masked edit — exempt from the whole-image no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>
    /// SharedSchema already declares the loader/clip knobs + `denoise`; drop the shared denoise (its chat label
    /// "Denoise (source ↔ motion)" is wrong here) and re-add it as "Change amount", plus the inpaint-specific knobs.
    /// </summary>
    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema = EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        // Step 0.01, not the UI's 0.1 default for doubles: how far the masked region drifts is the knob you tune most
        // finely here, and 0.1 is too coarse to land between (e.g.) 0.55 and 0.65.
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Change amount" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.MaskGrow,       Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    }).ToArray();

    /// <summary>This workflow's own nodes (the shared head Model/Clip/Vae/Source come from EditWorkflow.Nodes).</summary>
    private const string ClipSkip = "19";
    private const string Positive = "13";
    private const string Negative = "14";
    private const string Encode = "12";
    private const string MaskImage = "11";
    private const string GrowMaskNode = "30";
    private const string NoiseMask = "31";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(AnimaInpaintParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"

        // clip-skip applies only to a checkpoint's baked CLIP (Anima loads split → no-op there; kept for parity).
        if (LoaderKinds.Parse(p.Loader) == LoaderKind.Checkpoint && p.ClipSkip is int clipSkip && clipSkip > 0)
        {
            g[ClipSkip] = new CLIPSetLastLayer { Clip = clip0, StopAtClipLayer = -Math.Abs(clipSkip) };
            clip0 = CLIPSetLastLayer.ClipOut(ClipSkip);
        }

        // Positive = quality prefix + the user's full prompt; negative = the config default with the UI negative
        // (inputs.Negative) appended — never replaced (see ComfyGraph.ComposeNegative).
        string? rp = p.RequiredPrefix;
        string prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        string neg = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative);
        g[Positive] = new CLIPTextEncode { Text = prefix + inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // Source RGB (LoadImage IMAGE, node "10") stays PRISTINE → latent, so the region outside the mask is preserved
        // and the masked region has the real pixels to partially-denoise from (identity kept, expression changed).
        g[Encode] = new VAEEncode { Pixels = LoadImage.ImageOut(Nodes.Source), Vae = vae0 };
        // Mask: a SEPARATE white-on-black image via LoadImageMask (red channel). Fallback to the source alpha only if
        // no mask image was supplied. SetLatentNoiseMask confines denoising to the masked (white) region.
        Output<Slot.Mask> maskSrc;
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            g[MaskImage] = new LoadImageMask { Image = inputs.MaskImageName, Channel = "red" };
            maskSrc = LoadImageMask.Out(MaskImage);
        }
        else maskSrc = LoadImage.MaskOut(Nodes.Source);
        int grow = p.MaskGrow;   // bound enforced by the DTO's [Range] at the ParamsCodec boundary
        if (grow > 0)
        {
            g[GrowMaskNode] = new GrowMask { Mask = maskSrc, Expand = grow, TaperedCorners = true };
            maskSrc = GrowMask.Out(GrowMaskNode);
        }
        g[NoiseMask] = new SetLatentNoiseMask { Samples = VAEEncode.Out(Encode), Mask = maskSrc };

        double dn = p.Denoise;
        g[Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = dn,
            Model = model0,
            Positive = CLIPTextEncode.Out(Positive),
            Negative = CLIPTextEncode.Out(Negative),
            LatentImage = SetLatentNoiseMask.Out(NoiseMask),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>Anima masked-inpaint parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings and the masked-region <c>denoise</c> (all
/// <c>required</c>), the required mask grow, and the optional prefix/negative (nullable strings) + Has-guarded
/// <c>clip_skip</c>. <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record AnimaInpaintParams
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
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]       public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)]       public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)]                                       public required int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]           public long Seed { get; init; }
}
