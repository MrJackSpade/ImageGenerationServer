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
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema = SharedSchema.Where(s => s.Key != "denoise").Concat(new ParamSpec[]
    {
        new() { Key = "denoise",         Type = ParamType.Double, Min = 0.2, Max = 1.0, Step = 0.01, Label = "Redraw strength" },
        new() { Key = "required_prefix", Type = ParamType.String },
        new() { Key = "negative",        Type = ParamType.String },
        new() { Key = "clip_skip",       Type = ParamType.Int },
        // Flow-shift for the models that carry one (Chroma's ModelSamplingAuraFlow). Unset = omit the node.
        new() { Key = "shift",           Type = ParamType.Double },
        // The model's trained pixel budget (width × height of one of its native aspect buckets). A source meaningfully
        // over it is downscaled to it before the encode; 0 = sample the source at its own resolution, no rescale.
        new() { Key = "native_pixels",   Type = ParamType.Int },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"

        if (p.StrReq("loader") == "checkpoint" && p.Has("clip_skip") && p.IntReq("clip_skip") is int clipSkip && clipSkip > 0)
        {
            wf["19"] = ComfyGraph.Node("CLIPSetLastLayer", new { clip = clip0, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clip0 = ComfyGraph.Ref("19", 0);
        }

        // Chroma prompts through T5-XXL with min-padding disabled — its official graph puts a T5TokenizerOptions in
        // front of the encodes, and without it the padded conditioning degrades the render (see ChromaWorkflow).
        if (string.Equals(p.Str("clip_type"), "chroma", StringComparison.OrdinalIgnoreCase))
        {
            wf["17"] = ComfyGraph.Node("T5TokenizerOptions", new { clip = clip0, min_padding = 0, min_length = 0 });
            clip0 = ComfyGraph.Ref("17", 0);
        }

        // Positive = quality prefix + the user's full prompt; negative = the config default with the UI negative
        // (inputs.Negative) merged in — never replaced (see ComfyGraph.ComposeNegative).
        var rp = p.Str("required_prefix");
        var prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        var neg = ComfyGraph.ComposeNegative(p.Str("negative"), inputs.Negative);
        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = prefix + inputs.Positive, clip = clip0 });
        wf["14"] = ComfyGraph.Node("CLIPTextEncode", new { text = neg, clip = clip0 });

        // A guidance-distilled model (FLUX.1-dev/Krea, FLUX.2) takes its guidance in the conditioning, not as real CFG
        // — the same `guidance` param the txt2img/pixelize graphs use. Unset (Anima, schnell, Chroma) = omit the node.
        object posSrc = ComfyGraph.Ref("13", 0);
        if (p.DblOrNull("guidance") is double g)
        {
            wf["15"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("13", 0), guidance = g });
            posSrc = ComfyGraph.Ref("15", 0);
        }
        // Flow-shift, when the model declares one (Chroma runs at shift 1.0).
        if (p.DblOrNull("shift") is double shift)
        {
            wf["16"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = model0, shift });
            model0 = ComfyGraph.Ref("16", 0);
        }

        // Run at the model's NATIVE resolution. The source pose comes from another editor at its own size (often over
        // budget and off the model's aspect buckets); running a 2B anime/photo checkpoint far from its trained ~1 MP is
        // what makes it pad the frame with repeated/decorative junk. So downscale the source to the model's native pixel
        // budget (aspect preserved, snapped to /16) before the img2img — the same "render at a native bucket" the
        // generate path does. The result is left at that native resolution (a redraw is already a destructive
        // re-render; no point up-scaling it back). Only downscales; needs the source dims the edit path supplies (falls
        // back to the raw source if unavailable, or if the config declares no budget).
        static int Snap16(int v) => Math.Max(16, (int)Math.Round(v / 16.0) * 16);
        long budget = p.Has("native_pixels") ? p.IntReq("native_pixels") : 0;   // no budget declared → sample the source at its own resolution
        int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
        object encPixels = ComfyGraph.Ref("10", 0);
        if (budget > 0 && sw > 0 && sh > 0)
        {
            double f = Math.Sqrt(budget / ((double)sw * sh));
            if (f < 0.98)   // meaningfully over budget → downscale to native
            {
                wf["11"] = ComfyGraph.Node("ImageScale", new
                {
                    image = ComfyGraph.Ref("10", 0),
                    upscale_method = "lanczos",
                    width = Snap16((int)Math.Round(sw * f)),
                    height = Snap16((int)Math.Round(sh * f)),
                    crop = "disabled",
                });
                encPixels = ComfyGraph.Ref("11", 0);
            }
        }

        // Encode the (native-res) source straight to a latent — NO mask, so the whole image is re-sampled. At denoise
        // < 1 the source's own structure survives; the prompt + the checkpoint's prior restyle it.
        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = encPixels, vae = vae0 });

        double dn = p.DblReq("denoise");
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = dn,
            model = model0,
            positive = posSrc,
            negative = ComfyGraph.Ref("14", 0),
            latent_image = ComfyGraph.Ref("12", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
