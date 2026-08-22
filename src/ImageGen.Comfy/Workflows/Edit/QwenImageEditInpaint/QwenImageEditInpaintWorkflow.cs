using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.QwenImageEditInpaint;

/// <summary>
/// Masked INPAINT on <b>Qwen-Image-Edit</b> (the instruction fine-tune) that ALSO takes reference images — the
/// combination the split editors couldn't reach on their own. <see cref="QwenImageEditWorkflow"/> edits with
/// references but regenerates the whole canvas; <see cref="QwenImageInpaint.QwenImageInpaintWorkflow"/> confines the
/// edit to a painted mask but rides the base Qwen-Image + InstantX ControlNet, which takes no references and no
/// Qwen2.5-VL instruction. This one is the Reddit "Qwen edit with mask &amp; reference" topology: the Qwen edit
/// conditioning (image1 = the source, image2/image3 = the references, an instruction through the VL encoder) is fed to
/// <c>InpaintModelConditioning</c>, whose noise mask confines the denoise to the region and pins everything outside it.
///
/// <para><b>Why no InstantX ControlNet.</b> That ControlNet was trained against base Qwen-Image, not the Edit
/// fine-tune (see <see cref="QwenImageInpaint.QwenInstantXInpaintBase"/>). The masked-conditioning approach here needs
/// no model of its own — <c>InpaintModelConditioning</c> with <c>noise_mask=true</c> is ComfyUI core and rides
/// whatever conditioning it is handed, so the Qwen edit reference latents pass straight through. On a model without
/// dedicated inpaint channels (Qwen edit) the node's concat latent is inert; what does the work is the noise mask —
/// the same per-step "pin the outside, denoise the hole" the FLUX Fill path relies on (see <see cref="FluxFillBase"/>).</para>
///
/// <para><b>Mask alignment.</b> The painted mask arrives at SOURCE resolution, but the shared head's <c>image1</c> /
/// fill pixels are the <c>FluxKontextImageScale</c> bucket rescale of the source. So the mask is resampled to the
/// runtime bucket dims (<c>GetImageSize</c> reads them; the mask round-trips through IMAGE with nearest-exact so it
/// stays binary) before it can gate that latent. Output lands at the bucket size, exactly like the plain Qwen edit.</para>
/// </summary>
public sealed class QwenImageEditInpaintWorkflow : EditWorkflow<QwenImageEditInpaintParams>
{
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;
    public override string Name => "qwen-image-edit-inpaint";
    public override WorkflowKind Kind => WorkflowKind.Inpaint;

    /// <summary>Only the masked region changes, and the composite enforces it — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema =
    [
        .. EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise),
        new() { Key = WorkflowParamKeys.MaskGrow, Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
        new() { Key = WorkflowParamKeys.MaskBlur, Type = ParamType.Int, Min = 0, Max = 31, Label = "Mask edge blur (px)" },
    ];

    /// <summary>Gaussian sigma for the mask-edge blur, in bucket pixels — several 8px latent cells wide so the
    /// composite's crossfade band spans more than one cell and does not decode as a hard 1px line at the join.</summary>
    private const double MaskBlurSigma = 8.0;

    protected override (int Width, int Height) EtaRenderSize(QwenImageEditInpaintParams p, ResolvedRequirements req,
        int sourceWidth, int sourceHeight, double? editMegapixels) =>
        EditWorkingResolution.Resolve(sourceWidth, sourceHeight,
            editMegapixels ?? EditWorkingResolution.NativeMegapixels, EditWorkingResolution.NativeStep,
            Math.Min(req.Resolution?.MaxW ?? 0, req.Resolution?.MaxH ?? 0));

    protected override ComfyWorkflowGraph Build(QwenImageEditInpaintParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);

        // Shared reference-encode head: Kontext-scale the source (image1 + the fill pixels), load references into
        // image2/image3, emit the positive/negative Qwen edit encodes + the 2511 sampling fix. aio:false — this is the
        // standard split model, never the all-in-one rapid checkpoint.
        double editMp = inputs.EditMegapixels ?? EditWorkingResolution.NativeMegapixels;
        (int sourceWidth, int sourceHeight) = EditWorkingResolution.Resolve(inputs.SourceWidth, inputs.SourceHeight,
            editMp, EditWorkingResolution.NativeStep,
            Math.Min(req.Resolution?.MaxW ?? 0, req.Resolution?.MaxH ?? 0));
        QwenRefHeadOut head = QwenReferenceHead.Emit(g, aio: false, model0, clip0, vae0, inputs, p.ReferenceInputs,
            p.ReferenceMax, p.ReferenceLatentsMethod, editMp, sourceWidth, sourceHeight);
        Output<Slot.Image> source = head.Kontext;

        // The painted mask (white-on-black upload, or the source alpha as a fallback) at SOURCE resolution, resampled
        // to the Kontext bucket so it lines up with the head's image1/fill pixels. nearest-exact keeps it binary; a
        // bilinear ramp here would stack with the deliberate soften below.
        Output<Slot.Mask> srcMask;
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            g[Nodes.MaskLoad] = new LoadImageMask { Image = inputs.MaskImageName, Channel = ComfyWidgets.MaskChannel.Red };
            srcMask = LoadImageMask.Out(Nodes.MaskLoad);
        }
        else
        {
            srcMask = LoadImage.MaskOut(EditNodes.Source);
        }

        g[Nodes.MaskSize] = new GetImageSize { Image = source };
        g[Nodes.MaskAsImage] = new MaskToImage { Mask = srcMask };
        g[Nodes.MaskScaled] = new ImageScaleFromSize
        {
            Image = MaskToImage.Out(Nodes.MaskAsImage),
            UpscaleMethod = ComfyWidgets.Upscale.NearestExact,
            Width = GetImageSize.WidthOut(Nodes.MaskSize),
            Height = GetImageSize.HeightOut(Nodes.MaskSize),
            Crop = ComfyWidgets.Crop.Disabled,
        };
        g[Nodes.MaskBack] = new ImageToMask { Image = ImageScaleFromSize.Out(Nodes.MaskScaled), Channel = ComfyWidgets.MaskChannel.Red };
        Output<Slot.Mask> softMask = SoftenMask(g, p, ImageToMask.Out(Nodes.MaskBack));

        // Native masked conditioning ON the Qwen edit conditioning. noise_mask=true confines the denoise to the region
        // and re-injects the noised original latents outside it each step (the exposure/content anchor); the returned
        // latent is what the sampler runs. The reference latents ride through in positive/negative untouched.
        g[Nodes.InpaintConditioning] = new InpaintModelConditioning
        {
            Positive = head.Cond,
            Negative = head.NegCond,
            Vae = vae0,
            Pixels = source,
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
            Model = head.KsModel,
            Positive = InpaintModelConditioning.PositiveOut(Nodes.InpaintConditioning),
            Negative = InpaintModelConditioning.NegativeOut(Nodes.InpaintConditioning),
            LatentImage = InpaintModelConditioning.LatentOut(Nodes.InpaintConditioning),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };

        // Paste-back so everything outside the mask is bit-identical to the (bucket-scaled) source rather than a VAE
        // round-trip of it. The same soft mask crossfades the band, held at a hard 1 over the fill region.
        g[Nodes.Composite] = new ImageCompositeMasked
        {
            Destination = source,
            Source = VAEDecode.Out(Nodes.Decode),
            X = 0,
            Y = 0,
            ResizeSource = false,
            Mask = softMask,
        };
        g[Nodes.Save] = new SaveImage { Images = ImageCompositeMasked.Out(Nodes.Composite), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }

    /// <summary>
    /// <c>GrowMask → MaskToImage → ImageBlur → ImageToMask → MaskComposite(add)</c> — the shared mask-soften recipe.
    /// The IMAGE round-trip is how a mask's own boundary gets blurred (no MASK-space node does it); the trailing
    /// <c>MaskComposite "add"</c> (+ its 0..1 clamp) restores a hard 1 over the raw fill region, so the ramp is
    /// one-sided — full over the hole, descending only outward over real source pixels. Grows/blurs the already
    /// bucket-aligned mask, so its px values are in bucket pixels (the same space the composite crossfades in).
    /// </summary>
    private static Output<Slot.Mask> SoftenMask(ComfyWorkflowGraph g, QwenImageEditInpaintParams p, Output<Slot.Mask> rawMask)
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

        g[Nodes.SoftenAsImage] = new MaskToImage { Mask = m };
        g[Nodes.SoftenBlur] = new ImageBlur { Image = MaskToImage.Out(Nodes.SoftenAsImage), BlurRadius = blur, Sigma = MaskBlurSigma };
        g[Nodes.SoftenBack] = new ImageToMask { Image = ImageBlur.Out(Nodes.SoftenBlur), Channel = ComfyWidgets.MaskChannel.Red };
        g[Nodes.SoftenComposite] = new MaskComposite
        {
            Destination = ImageToMask.Out(Nodes.SoftenBack),
            Source = rawMask,
            X = 0,
            Y = 0,
            Operation = ComfyWidgets.MaskOperation.Add,
        };
        return MaskComposite.Out(Nodes.SoftenComposite);
    }
}
