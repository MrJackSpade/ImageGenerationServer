using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>The outputs of the shared Qwen-Image-Edit reference-encode head (<see cref="QwenReferenceHead.Emit"/>):
/// the positive/negative conditioning (source-edit reference latents stitched by the config's method), the sampler-ready
/// model (<c>ModelSamplingAuraFlow</c>+<c>CFGNorm</c> unless AIO bakes its own sampling), the real or synthesized
/// <c>image1</c> canvas, its VAE latent, and the VAE edge.</summary>
internal readonly record struct QwenRefHeadOut(
    Output<Slot.Conditioning> Cond,
    Output<Slot.Conditioning> NegCond,
    Output<Slot.Model> KsModel,
    Output<Slot.Image> Kontext,
    Output<Slot.Latent> SourceLatent,
    Output<Slot.Vae> Vae);

/// <summary>
/// The reference-aware encode head shared by every Qwen-Image-Edit topology (the plain instruction editor
/// <see cref="QwenEditBase"/> and the masked <c>QwenImageEditInpaintWorkflow</c>). It emits, from an already-loaded
/// model/CLIP/VAE and optional source <c>LoadImage</c>: a normalized real source or synthesized blank <c>image1</c>,
/// reference image loads into <c>image2</c>/<c>image3</c>, and positive/negative
/// <c>TextEncodeQwenImageEditPlus</c> encodes, with reference-latent stitching only when a real source image exists.
///
/// <para>Extracted so the subtle bits live in ONE place: the negative is a SECOND full encode of the same images with
/// an EMPTY instruction — the official 2511 blueprint's CFG contrast, not a zeroed-out positive (issue #218) — and
/// the stitch method is the CONFIG's per-model declaration (Qwen handles <c>index_timestep_zero</c>; issue #215),
/// refused here rather than surfacing as a Comfy error when unknown. What a caller does with the conditioning AFTER
/// this head diverges: the plain editor uses the source latent only for Reference shape, otherwise supplies an empty
/// target latent; the inpaint editor routes the required source through <c>InpaintModelConditioning</c>.</para>
/// </summary>
internal static class QwenReferenceHead
{
    /// <summary>The <c>TextEncodeQwenImageEditPlus</c> node's <c>vae</c> input name, added to the reference overflow
    /// bag only when both a real source and at least one reference are present (so the edit images become reference
    /// latents without injecting source-free attachments into the target stream).</summary>
    private static class Inputs
    {
        public const string Vae = "vae";
    }

    /// <summary>Emit the head into <paramref name="g"/>. The caller has already run <c>LoadModel</c> (model/CLIP/VAE +
    /// the optional source <c>LoadImage</c> at <see cref="EditNodes.Source"/>) and passes those edges in.
    /// <paramref name="aio"/> is true for the all-in-one rapid checkpoint (skips the standard 2511
    /// <c>ModelSamplingAuraFlow</c>+<c>CFGNorm</c>).</summary>
    public static QwenRefHeadOut Emit(ComfyWorkflowGraph g, bool aio,
        Output<Slot.Model> model0, Output<Slot.Clip> clip0, Output<Slot.Vae> vae0,
        WorkflowInputs inputs, string[]? referenceInputs, int? referenceMax, string referenceLatentsMethod,
        double editMegapixels, int sourceWidth, int sourceHeight)
    {
        // A real primary image is scaled without cropping and enters Qwen as image1. When the user omits it, synthesize
        // a blank image1 canvas instead: full-denoise sampling turns its latent into noise, while attached images keep
        // their exact normal image2/image3 Qwen-VL + reference-latent roles. The user never has to upload whitespace.
        bool hasRealSource = inputs.SourceImageName is { Length: > 0 };
        Output<Slot.Image> kontext;
        if (inputs.SourceImageName is { Length: > 0 })
        {
            g[Nodes.KontextScale] = new ImageScale { Image = LoadImage.ImageOut(EditNodes.Source),
                UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Width = sourceWidth, Height = sourceHeight,
                Crop = ComfyWidgets.Crop.Disabled };
            kontext = ImageScale.Out(Nodes.KontextScale);
        }
        else
        {
            g[Nodes.BlankImage1] = new EmptyImageLiteral
            {
                Width = Ensure.GreaterThanZero(sourceWidth),
                Height = Ensure.GreaterThanZero(sourceHeight),
                BatchSize = 1,
                Color = CanvasMaskConstants.BlockedFillRgb,
            };
            kontext = EmptyImageLiteral.Out(Nodes.BlankImage1);
        }

        g[Nodes.SourceEncode] = new VAEEncode { Pixels = kontext, Vae = vae0 };
        Output<Slot.Latent> sourceLatent = VAEEncode.Out(Nodes.SourceEncode);

        string[] qInputs = referenceInputs ?? [];
        IReadOnlyList<string> refNames = inputs.ImageReferences;
        // Capacity is the smaller of the model's reference_max and the graph's available image slots — both hard
        // structural limits. More references than that is REFUSED, not silently truncated to fit.
        int refCapacity = Math.Min(referenceMax ?? 0, qInputs.Length);
        if (refNames.Count > refCapacity)
        {
            throw new RenderValidationException($"This configuration accepts at most {refCapacity} reference image(s); got {refNames.Count}.");
        }

        int qn = refNames.Count;
        Dictionary<string, object> encRefs = [];
        for (int i = 0; i < qn; i++)
        {
            string load = $"{40 + (i * 2)}", scale = $"{41 + (i * 2)}";
            g[load] = new LoadImage { Image = refNames[i] };
            g[scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(load),
                UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = editMegapixels, ResolutionSteps = EditWorkingResolution.NativeStep };
            encRefs[qInputs[i]] = ImageScaleToTotalPixels.Out(scale);
        }

        string instruction = inputs.Positive;
        Output<Slot.Conditioning> cond;
        Output<Slot.Conditioning> negCond;
        if (qn > 0)
        {
            // The stitch method is the CONFIG's declaration of what its model supports: Qwen-Image handles
            // index_timestep_zero, but LongCat (plain-Flux modulation) crashes on it and takes index — see
            // ComfyWidgets.ReferenceLatents. An unknown value is refused here rather than surfacing as a Comfy error.
            string refMethod = referenceLatentsMethod switch
            {
                ComfyWidgets.ReferenceLatents.Offset or ComfyWidgets.ReferenceLatents.Index or
                ComfyWidgets.ReferenceLatents.UxoUno or ComfyWidgets.ReferenceLatents.IndexTimestepZero => referenceLatentsMethod,
                _ => throw new ArgumentException($"unknown reference_latents_method '{referenceLatentsMethod}'."),
            };
            // TextEncodeQwenImageEditPlus's optional VAE encodes EVERY image into reference_latents. That is the
            // official edit path when a real image1 exists, but in a source-free request it also injects image2/image3
            // into the denoising latent stream. Keep the synthesized blank image1 above so the Qwen-VL prompt roles
            // remain Picture 1/Picture 2/Picture 3, but omit VAE reference latents so the target starts independently.
            bool emitReferenceLatents = hasRealSource;
            if (emitReferenceLatents)
            {
                encRefs[Inputs.Vae] = vae0;
            }

            g[Nodes.Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = kontext, Prompt = instruction, Extra = encRefs };
            if (emitReferenceLatents)
            {
                g[Nodes.MultiRefLatent] = new FluxKontextMultiReferenceLatentMethod { Conditioning = TextEncodeQwenImageEditPlus.Out(Nodes.Encode), ReferenceLatentsMethod = refMethod };
                cond = FluxKontextMultiReferenceLatentMethod.Out(Nodes.MultiRefLatent);
            }
            else
            {
                cond = TextEncodeQwenImageEditPlus.Out(Nodes.Encode);
            }

            // The official 2511 blueprint's negative is a second full encode — the SAME images and reference latents
            // with an EMPTY instruction — not a zeroed-out positive. With real CFG the contrast is then "with vs
            // without the instruction" over identical image conditioning; zeroing everything made CFG push away from
            // the references themselves, which read as the source and reference ghosting into each other (#218).
            g[Nodes.NegativeEncode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = kontext, Prompt = string.Empty, Extra = encRefs };
            if (emitReferenceLatents)
            {
                g[Nodes.NegMultiRefLatent] = new FluxKontextMultiReferenceLatentMethod { Conditioning = TextEncodeQwenImageEditPlus.Out(Nodes.NegativeEncode), ReferenceLatentsMethod = refMethod };
                negCond = FluxKontextMultiReferenceLatentMethod.Out(Nodes.NegMultiRefLatent);
            }
            else
            {
                negCond = TextEncodeQwenImageEditPlus.Out(Nodes.NegativeEncode);
            }
        }
        else
        {
            if (!hasRealSource)
            {
                throw new RenderValidationException("Qwen reference-only generation requires at least one attached image reference.");
            }

            g[Nodes.Encode] = new TextEncodeQwenImageEditPlus { Clip = clip0, Image1 = kontext, Prompt = instruction };
            g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = TextEncodeQwenImageEditPlus.Out(Nodes.Encode), Latent = sourceLatent };
            cond = ReferenceLatent.Out(Nodes.RefLatent);
            g[Nodes.ZeroNegative] = new ConditioningZeroOut { Conditioning = cond };
            negCond = ConditioningZeroOut.Out(Nodes.ZeroNegative);
        }

        Output<Slot.Model> ksModel = model0;
        if (!aio)                                             // standard 2511 needs ModelSamplingAuraFlow + CFGNorm
        {
            g[Nodes.ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = 3.1 };
            g[Nodes.CfgNorm] = new CFGNorm { Model = ModelSamplingAuraFlow.Out(Nodes.ModelSampling), Strength = 1.0 };
            ksModel = CFGNorm.Out(Nodes.CfgNorm);
        }

        return new QwenRefHeadOut(cond, negCond, ksModel, kontext, sourceLatent, vae0);
    }

    /// <summary>The head's node ids (role-named), preserved from <see cref="QwenEditBase"/> so its emitted graph is
    /// unchanged. The per-reference load nodes stay computed (<c>$"{40+i*2}"</c>).</summary>
    private static class Nodes
    {
        public const string KontextScale = "11";
        public const string BlankImage1 = "12";
        public const string Encode = "13";
        public const string SourceEncode = "14";
        public const string RefLatent = "30";
        public const string MultiRefLatent = "70";
        public const string NegativeEncode = "71";
        public const string NegMultiRefLatent = "72";
        public const string ZeroNegative = "26";
        public const string ModelSampling = "2";
        public const string CfgNorm = "7";
    }
}
