using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.AnimaInpaint;

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
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;
    public override string Name => "anima-inpaint";
    public override WorkflowKind Kind => WorkflowKind.Inpaint;

    /// <summary>inputs.Positive carries the user's FULL prompt for the picture, not an edit instruction.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;
    /// <summary>Local masked edit — exempt from the whole-image no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>
    /// SharedSchema already declares the loader/clip knobs + `denoise`; drop the shared denoise (its chat label
    /// "Denoise (source ↔ motion)" is wrong here) and re-add it as "Change amount", plus the inpaint-specific knobs.
    /// </summary>
    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema =
    [
        .. EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise),
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Change amount" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.MaskGrow,       Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    ];

    protected override (int Width, int Height) EtaRenderSize(
        AnimaInpaintParams p,
        ResolvedRequirements req,
        int sourceWidth,
        int sourceHeight,
        double? editMegapixels) =>
        EditWorkingResolution.Resolve(sourceWidth, sourceHeight, editMegapixels ?? EditWorkingResolution.NativeMegapixels);

    protected override ComfyWorkflowGraph Build(AnimaInpaintParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // nodes 4/5/6 + LoadImage "10"

        // clip-skip applies only to a checkpoint's baked CLIP (Anima loads split → no-op there; kept for parity).
        if (LoaderKindWire.Parse(p.Loader) == LoaderKind.Checkpoint && p.ClipSkip is int clipSkip && clipSkip > 0)
        {
            g[Nodes.ClipSkip] = new CLIPSetLastLayer { Clip = clip0, StopAtClipLayer = -Math.Abs(clipSkip) };
            clip0 = CLIPSetLastLayer.ClipOut(Nodes.ClipSkip);
        }

        // Positive = quality prefix + the user's full prompt; negative = the config default with the UI negative
        // (inputs.Negative) appended — never replaced (see ComfyGraph.ComposeNegative).
        string? rp = p.RequiredPrefix;
        string prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        string neg = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative);
        g[Nodes.Positive] = new CLIPTextEncode { Text = prefix + inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // Mask: a SEPARATE white-on-black image via LoadImageMask (red channel). Fallback to the source alpha only if
        // no mask image was supplied. SetLatentNoiseMask confines denoising to the masked (white) region.
        Output<Slot.Mask> maskSrc;
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            g[Nodes.MaskImage] = new LoadImageMask { Image = inputs.MaskImageName, Channel = ComfyWidgets.MaskChannel.Red };
            maskSrc = LoadImageMask.Out(Nodes.MaskImage);
        }
        else
        {
            maskSrc = LoadImage.MaskOut(EditNodes.Source);
        }

        (int Width, int Height) current = (
            Ensure.GreaterThanZero(inputs.SourceWidth),
            Ensure.GreaterThanZero(inputs.SourceHeight));
        (int Width, int Height) target = EditWorkingResolution.Resolve(current.Width, current.Height,
            inputs.EditMegapixels ?? EditWorkingResolution.NativeMegapixels);
        Output<Slot.Image> image = LoadImage.ImageOut(EditNodes.Source);
        EditWorkingResolution.ScalePair(
            g,
            Nodes.WorkingImage,
            Nodes.WorkingMaskAsImage,
            Nodes.WorkingMaskImage,
            Nodes.WorkingMask,
            current,
            target,
            ref image,
            ref maskSrc);

        // Source RGB and mask are normalized to the same native working grid before encoding/denoising. The masked
        // region still starts from the real pixels, preserving identity while avoiding a tiny VAE latent grid.
        g[Nodes.Encode] = new VAEEncode { Pixels = image, Vae = vae0 };

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
            Model = model0,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = SetLatentNoiseMask.Out(Nodes.NoiseMask),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
