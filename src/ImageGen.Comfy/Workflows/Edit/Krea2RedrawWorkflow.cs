namespace ImageGen.Comfy;

/// <summary>
/// Whole-image "polish / redraw" on <b>Krea 2 Turbo</b>: take ANY existing image — typically one another model
/// generated — and hand it to the distilled Turbo weight for a partial-denoise pass that reworks texture and applies
/// Krea's aesthetic without redrawing the composition.
///
/// This is the second stage of <see cref="Krea2RefineWorkflow"/> lifted onto the edit rails. There, stage 1 renders a
/// latent on the Krea 2 RAW base and passes it straight to Turbo (no VAE round-trip, both share the Qwen-Image/Wan2.1
/// VAE). Here the source image REPLACES that base render: it is uploaded and loaded via <c>LoadImage</c> (node "10",
/// emitted by <see cref="EditWorkflowBase.LoadModel"/>), VAE-encoded to a latent, and re-sampled with NO mask — so
/// the whole frame is polished, from the source's own structure, at whatever model produced it. Only the Turbo weight
/// is loaded (no RAW base), so this is the cheap single-pass member of the Krea 2 family.
///
/// The single meaningful knob is the shared <c>denoise</c>, relabelled "Polish strength": how hard Turbo reworks the
/// source. Turbo is distilled and runs at cfg 1, so the negative is inert — it is wired to the sampler for graph
/// symmetry (matching <see cref="Krea2RefineWorkflow"/>'s polish pass) and the configuration declares
/// <c>negative_supported: false</c>. Inherits Krea 2's per-layer conditioning rebalance (<see cref="Krea2Rebalance"/>)
/// so the baked "uncensor" applies exactly as it does for the plain krea2 / krea2-turbo configs.
///
/// The source is sampled at its OWN resolution — no rescale. Unlike <see cref="Img2ImgRedrawWorkflow"/> (whose 2B
/// checkpoints must be downscaled to their ~1 MP bucket or they pad the frame with junk), Krea 2 is native at ~1K and
/// holds up to 2K, and a polish pass whose whole purpose is to preserve the incoming image has no business resampling
/// it. Equivalently: that workflow's <c>native_pixels</c> budget is 0 here.
/// </summary>
public sealed class Krea2RedrawWorkflow : EditWorkflowBase
{
    public override string Name => "krea2-redraw";

    /// <summary>A polish pass is meant to land close to the source at low denoise — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>The prompt describes the whole picture being polished, not a change to make to it.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    /// <summary>Drop the shared <c>denoise</c> (its "source ↔ motion" label and 0 default are wrong here) and re-add it
    /// as the polish strength, plus Krea 2's rebalance knobs. Step 0.01 so the 0.35 default is reachable.</summary>
    public override IReadOnlyList<ParamSpec> Schema => RedrawSchema;
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema = SharedSchema.Where(s => s.Key != "denoise").Concat(new ParamSpec[]
    {
        new() { Key = "denoise", Type = ParamType.Double, Min = 0.1, Max = 0.9, Step = 0.01,
                Label = "Polish strength",
                Help = "How hard Turbo reworks the source image. ~0.25–0.40 polishes texture and aesthetic while keeping "
                     + "the source's composition; higher redraws more of the image (and drifts toward the prompt rather "
                     + "than the source)." },
    }).Concat(Krea2Rebalance.Schema).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"
        model0 = ComfyGraph.ApplyLora(wf, model0, p);                                 // optional style/quality LoRA

        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["14"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clip0 });
        // Node ids 13/14 are the text-encodes on the edit rails, so the rebalance splices in at "15".
        object posSrc = Krea2Rebalance.Apply(wf, ComfyGraph.Ref("13", 0), p, "15");

        // Source RGB → latent at its native resolution. NO mask, so the whole frame is re-sampled; at denoise < 1 the
        // source's own structure survives and Turbo reworks the texture over it.
        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("10", 0), vae = vae0 });

        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = p.DblReq("denoise"),
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
