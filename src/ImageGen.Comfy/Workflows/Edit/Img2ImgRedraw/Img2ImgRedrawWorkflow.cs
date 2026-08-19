using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.Img2ImgRedraw;

/// <summary>
/// Whole-image img2img REDRAW driven by a standard generation checkpoint. Reuses the edit rails: the source image is
/// uploaded and loaded via <c>LoadImage</c> (node "10", emitted by <see cref="EditWorkflow{TParams}.LoadModel"/>), VAE-
/// encoded to a latent, and re-sampled at a PARTIAL denoise with NO mask — so the whole frame is regenerated from the
/// source's own structure ("the noise thing"). The target use: take an off-model edit (e.g. a qwen-image-edit pose)
/// and reinterpret it through the checkpoint's prior + the prompt, keeping the composition but restoring the look.
/// Lower denoise = closer to the source; higher = more reinterpretation.
///
/// Model-agnostic: everything model-specific arrives as a configuration parameter, so a checkpoint gets a redraw by
/// adding a config that binds here — no new graph code. Anima and Photanima (a photographic finetune of the same 2B
/// architecture) both do exactly that; they differ only in weight, quality prefix, negative, and native resolution.
/// The FLUX family binds the same way: the three optional nodes each model may need — <c>FluxGuidance</c> for the
/// guidance-distilled weights, <c>ModelSamplingAuraFlow</c> for a flow-shift, and Chroma's <c>T5TokenizerOptions</c>
/// — are emitted only when the config declares them, so the Anima/Photanima graph stays byte-identical.
///
/// Like <see cref="AnimaInpaintWorkflow"/>, the edit submit path carries the positive (= the instruction) plus an
/// optional UI negative and applies no prefix, so this workflow adds the quality prefix itself
/// (<c>required_prefix</c>) and composes the negative as the config default with the UI negative merged in (see
/// <see cref="ComfyGraph.ComposeNegative"/>).
/// </summary>
public sealed class Img2ImgRedrawWorkflow : EditWorkflow<Img2ImgRedrawParams>
{
    public override bool NormalizesSourceResolution => true;
    public override string Name => "img2img-redraw";

    /// <summary>An img2img redraw can land close to the source at low denoise — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>The prompt is the FULL description of the resulting picture, not an instruction: the whole frame is
    /// re-rendered from it.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    /// <summary>Drop the shared <c>denoise</c> (its "source ↔ motion" label is wrong here) and re-add it as the redraw
    /// strength, plus the prompt-prefix/negative/clip-skip knobs the edit path doesn't supply and the model's native
    /// pixel budget.</summary>
    public override IReadOnlyList<ParamSpec> Schema => RedrawSchema;
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema =
    [
        .. EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise),
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Redraw strength" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Shift,           Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.NativePixels,   Type = ParamType.Int, Min = 1, Label = "Native pixel budget" },
    ];

    private static double NativeMegapixels(Img2ImgRedrawParams p) =>
        p.NativePixels is int pixels
            ? pixels / (1024.0 * 1024.0)
            : EditWorkingResolution.NativeMegapixels;

    protected override (int Width, int Height) EtaRenderSize(
        Img2ImgRedrawParams p,
        ResolvedRequirements req,
        int sourceWidth,
        int sourceHeight) =>
        EditWorkingResolution.Resolve(
            sourceWidth,
            sourceHeight,
            NativeMegapixels(p));

    protected override ComfyWorkflowGraph Build(Img2ImgRedrawParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // nodes 4/5/6 + LoadImage EditNodes.Source

        if (LoaderKindWire.Parse(p.Loader) == LoaderKind.Checkpoint && p.ClipSkip is int clipSkip && clipSkip > 0)
        {
            g[Nodes.ClipSkip] = new CLIPSetLastLayer { Clip = clip0, StopAtClipLayer = -Math.Abs(clipSkip) };
            clip0 = CLIPSetLastLayer.ClipOut(Nodes.ClipSkip);
        }

        // Chroma prompts through T5-XXL with min-padding disabled — its official graph puts a T5TokenizerOptions in
        // front of the encodes, and without it the padded conditioning degrades the render (see ChromaWorkflow).
        if (string.Equals(p.ClipType, ComfyWidgets.ClipType.Chroma, StringComparison.OrdinalIgnoreCase))
        {
            g[Nodes.TokenizerOptions] = new T5TokenizerOptions { Clip = clip0, MinPadding = 0, MinLength = 0 };
            clip0 = T5TokenizerOptions.Out(Nodes.TokenizerOptions);
        }

        // Positive = quality prefix + the user's full prompt; negative = the config default with the UI negative
        // (inputs.Negative) merged in — never replaced (see ComfyGraph.ComposeNegative).
        string? rp = p.RequiredPrefix;
        string prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        string neg = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative);
        g[Nodes.Positive] = new CLIPTextEncode { Text = prefix + inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // A guidance-distilled model (FLUX.1-dev/Krea, FLUX.2) takes its guidance in the conditioning, not as real CFG
        // — the same `guidance` param the txt2img/pixelize graphs use. Unset (Anima, schnell, Chroma) = omit the node.
        Output<Slot.Conditioning> posSrc = CLIPTextEncode.Out(Nodes.Positive);
        if (p.Guidance is double guidance)
        {
            g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = guidance };
            posSrc = FluxGuidance.Out(Nodes.Guidance);
        }
        // Flow-shift, when the model declares one (Chroma runs at shift 1.0).
        if (p.Shift is double shift)
        {
            g[Nodes.ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = shift };
            model0 = ModelSamplingAuraFlow.Out(Nodes.ModelSampling);
        }

        // Normalize BOTH small and large sources to the model's native pixel budget before VAE encoding. Upscaling
        // cannot invent missing source frequencies, but it prevents an already-small upload from being compressed
        // into an unnecessarily tiny latent grid. A missing configuration value inherits the shared 1 MP fallback;
        // zero/raw-resolution bypasses are deliberately unsupported.
        (int Width, int Height) current = (
            Ensure.GreaterThanZero(inputs.SourceWidth),
            Ensure.GreaterThanZero(inputs.SourceHeight));
        (int Width, int Height) target = EditWorkingResolution.Resolve(
            current.Width,
            current.Height,
            NativeMegapixels(p));
        Output<Slot.Image> encPixels = EditWorkingResolution.ScaleImage(
            g,
            Nodes.SourceScale,
            LoadImage.ImageOut(EditNodes.Source),
            current,
            target);

        // Encode the (native-res) source straight to a latent — NO mask, so the whole image is re-sampled. At denoise
        // < 1 the source's own structure survives; the prompt + the checkpoint's prior restyle it.
        g[Nodes.Encode] = new VAEEncode { Pixels = encPixels, Vae = vae0 };

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
            Positive = posSrc,
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = VAEEncode.Out(Nodes.Encode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
