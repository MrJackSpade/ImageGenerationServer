using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// Whole-image img2img REDRAW driven by a standard generation checkpoint. Reuses the edit rails: the source image is
/// uploaded and loaded via <c>LoadImage</c> (node "10", emitted by <see cref="EditWorkflowBase.LoadModel"/>), VAE-
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
public sealed class Img2ImgRedrawWorkflow : EditWorkflowBase
{
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
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema = SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.2, Max = 1.0, Step = 0.01, Label = "Redraw strength" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
        // Flow-shift for the models that carry one (Chroma's ModelSamplingAuraFlow). Unset = omit the node.
        new() { Key = WorkflowParamKeys.Shift,           Type = ParamType.Double },
        // The model's trained pixel budget (width × height of one of its native aspect buckets). A source meaningfully
        // over it is downscaled to it before the encode; 0 = sample the source at its own resolution, no rescale.
        new() { Key = WorkflowParamKeys.NativePixels,   Type = ParamType.Int },
    }).ToArray();

    /// <summary>The <c>clip_type</c> value that marks a Chroma text encoder (needs the T5TokenizerOptions pass).</summary>
    private const string ChromaClipType = "chroma";

    /// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes).</summary>
    private const string ClipSkip = "19";
    private const string TokenizerOptions = "17";
    private const string Positive = "13";
    private const string Negative = "14";
    private const string Guidance = "15";
    private const string ModelSampling = "16";
    private const string SourceScale = "11";
    private const string Encode = "12";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);   // nodes 4/5/6 + LoadImage Nodes.Source

        if (p.Loader() == LoaderKind.Checkpoint && p.Has(WorkflowParamKeys.ClipSkip) && p.IntReq(WorkflowParamKeys.ClipSkip) is int clipSkip && clipSkip > 0)
        {
            wf[ClipSkip] = ComfyGraph.Node(ComfyNodeTypes.CLIPSetLastLayer, new { clip = clip0, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clip0 = ComfyGraph.Ref(ClipSkip, 0);
        }

        // Chroma prompts through T5-XXL with min-padding disabled — its official graph puts a T5TokenizerOptions in
        // front of the encodes, and without it the padded conditioning degrades the render (see ChromaWorkflow).
        if (string.Equals(p.Str(WorkflowParamKeys.ClipType), ChromaClipType, StringComparison.OrdinalIgnoreCase))
        {
            wf[TokenizerOptions] = ComfyGraph.Node(ComfyNodeTypes.T5TokenizerOptions, new { clip = clip0, min_padding = 0, min_length = 0 });
            clip0 = ComfyGraph.Ref(TokenizerOptions, 0);
        }

        // Positive = quality prefix + the user's full prompt; negative = the config default with the UI negative
        // (inputs.Negative) merged in — never replaced (see ComfyGraph.ComposeNegative).
        string? rp = p.Str(WorkflowParamKeys.RequiredPrefix);
        string prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        string neg = ComfyGraph.ComposeNegative(p.Str(WorkflowParamKeys.Negative), inputs.Negative);
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = prefix + inputs.Positive, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = neg, clip = clip0 });

        // A guidance-distilled model (FLUX.1-dev/Krea, FLUX.2) takes its guidance in the conditioning, not as real CFG
        // — the same `guidance` param the txt2img/pixelize graphs use. Unset (Anima, schnell, Chroma) = omit the node.
        object posSrc = ComfyGraph.Ref(Positive, 0);
        if (p.DblOrNull(WorkflowParamKeys.Guidance) is double g)
        {
            wf[Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(Positive, 0), guidance = g });
            posSrc = ComfyGraph.Ref(Guidance, 0);
        }
        // Flow-shift, when the model declares one (Chroma runs at shift 1.0).
        if (p.DblOrNull(WorkflowParamKeys.Shift) is double shift)
        {
            wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingAuraFlow, new { model = model0, shift });
            model0 = ComfyGraph.Ref(ModelSampling, 0);
        }

        // Run at the model's NATIVE resolution. The source pose comes from another editor at its own size (often over
        // budget and off the model's aspect buckets); running a 2B anime/photo checkpoint far from its trained ~1 MP is
        // what makes it pad the frame with repeated/decorative junk. So downscale the source to the model's native pixel
        // budget (aspect preserved, snapped to /16) before the img2img — the same "render at a native bucket" the
        // generate path does. The result is left at that native resolution (a redraw is already a destructive
        // re-render; no point up-scaling it back). Only downscales. No budget declared → the source is sampled at its
        // own resolution; a budget with a broken (zero-dimension) source is refused, not silently sampled at raw scale.
        static int Snap16(int v) => Math.Max(16, (int)Math.Round(v / 16.0) * 16);
        long budget = p.Has(WorkflowParamKeys.NativePixels) ? p.IntReq(WorkflowParamKeys.NativePixels) : 0;   // no budget declared → sample the source at its own resolution
        int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
        object encPixels = ComfyGraph.Ref(Nodes.Source, 0);
        if (budget > 0)
        {
            // A budget is declared, so downscale to it. The source is a still with measured dims — refuse a zero
            // rather than silently sampling the raw source at the wrong scale.
            Ensure.GreaterThanZero(sw);
            Ensure.GreaterThanZero(sh);
            double f = Math.Sqrt(budget / ((double)sw * sh));
            if (f < 0.98)   // meaningfully over budget → downscale to native
            {
                wf[SourceScale] = ComfyGraph.Node(ComfyNodeTypes.ImageScale, new
                {
                    image = ComfyGraph.Ref(Nodes.Source, 0),
                    upscale_method = "lanczos",
                    width = Snap16((int)Math.Round(sw * f)),
                    height = Snap16((int)Math.Round(sh * f)),
                    crop = "disabled",
                });
                encPixels = ComfyGraph.Ref(SourceScale, 0);
            }
        }

        // Encode the (native-res) source straight to a latent — NO mask, so the whole image is re-sampled. At denoise
        // < 1 the source's own structure survives; the prompt + the checkpoint's prior restyle it.
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = encPixels, vae = vae0 });

        double dn = p.DblReq(WorkflowParamKeys.Denoise);
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = dn,
            model = model0,
            positive = posSrc,
            negative = ComfyGraph.Ref(Negative, 0),
            latent_image = ComfyGraph.Ref(Encode, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
