using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.Krea2Redraw;

/// <summary>
/// Whole-image "polish / redraw" on <b>Krea 2 Turbo</b>: take ANY existing image — typically one another model
/// generated — and hand it to the distilled Turbo weight for a partial-denoise pass that reworks texture and applies
/// Krea's aesthetic without redrawing the composition.
///
/// This is the second stage of <see cref="Krea2RefineWorkflow"/> lifted onto the edit rails. There, stage 1 renders a
/// latent on the Krea 2 RAW base and passes it straight to Turbo (no VAE round-trip, both share the Qwen-Image/Wan2.1
/// VAE). Here the source image REPLACES that base render: it is uploaded and loaded via <c>LoadImage</c> (node "10",
/// emitted by <see cref="EditWorkflow{TParams}.LoadModel"/>), VAE-encoded to a latent, and re-sampled with NO mask — so
/// the whole frame is polished, from the source's own structure, at whatever model produced it. Only the Turbo weight
/// is loaded (no RAW base), so this is the cheap single-pass member of the Krea 2 family.
///
/// The single meaningful knob is the shared <c>denoise</c>, relabelled "Polish strength": how hard Turbo reworks the
/// source. Turbo is distilled and runs at cfg 1, so the negative is inert — it is wired to the sampler for graph
/// symmetry (matching <see cref="Krea2RefineWorkflow"/>'s polish pass) and the configuration declares
/// <c>negative_supported: false</c>. Inherits Krea 2's per-layer conditioning rebalance (<see cref="Krea2Rebalance"/>)
/// so the baked "uncensor" applies exactly as it does for the plain krea2 / krea2-turbo configs.
///
/// The source is normalized to Krea's configured native pixel budget before VAE encoding. This gives a small upload a
/// useful latent grid, reduces an oversized upload before sampling, preserves aspect ratio, and caps extreme aspect
/// ratios at Krea's configured maximum long edge.
/// </summary>
public sealed class Krea2RedrawWorkflow : EditWorkflow<Krea2RedrawParams>
{
    public override bool NormalizesSourceResolution => true;
    public override string Name => "krea2-redraw";

    /// <summary>A polish pass is meant to land close to the source at low denoise — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>The prompt describes the whole picture being polished, not a change to make to it.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    /// <summary>Drop the shared <c>denoise</c> (its "source ↔ motion" label and 0 default are wrong here) and re-add it
    /// as the polish strength, plus Krea 2's rebalance knobs. Step 0.01 so the 0.35 default is reachable. Floor 0 (a
    /// single-pass KSampler at denoise 0 passes the source latent through) = "don't polish".</summary>
    public override IReadOnlyList<ParamSpec> Schema => RedrawSchema;
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema =
    [
        .. EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise),
        new() { Key = WorkflowParamKeys.Denoise, Type = ParamType.Double, Min = 0.0, Max = 0.9, Step = 0.01,
                Label = "Polish strength",
                Help = "How hard Turbo reworks the source image. ~0.25–0.40 polishes texture and aesthetic while keeping "
                     + "the source's composition; higher redraws more of the image (and drifts toward the prompt rather "
                     + "than the source)." },
        .. Krea2Rebalance.Schema,
        new() { Key = WorkflowParamKeys.NativePixels, Type = ParamType.Int, Min = 1, Label = "Native pixel budget" },
        new() { Key = WorkflowParamKeys.MaxDimension, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Max long edge (px)" },
    ];

    private static double NativeMegapixels(Krea2RedrawParams p) => p.NativePixels / (1024.0 * 1024.0);

    protected override (int Width, int Height) EtaRenderSize(
        Krea2RedrawParams p,
        ResolvedRequirements req,
        int sourceWidth,
        int sourceHeight) =>
        EditWorkingResolution.Resolve(
            sourceWidth,
            sourceHeight,
            NativeMegapixels(p),
            maxDimension: p.MaxDimension);

    protected override ComfyWorkflowGraph Build(Krea2RedrawParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // nodes 4/5/6 + LoadImage "10"
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);                                 // optional style/quality LoRA

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        // Node ids 13/14 are the text-encodes on the edit rails, so the rebalance splices in at "15".
        Output<Slot.Conditioning> posSrc = Krea2Rebalance.Apply(g, CLIPTextEncode.Out(Nodes.Positive), p.Multiplier, p.PerLayerWeights, Nodes.Rebalance);

        // Normalize source RGB before the lossy VAE boundary. NO mask, so the whole frame is re-sampled; at denoise
        // < 1 the source's own structure survives and Turbo reworks the texture over it.
        (int Width, int Height) current = (
            Ensure.GreaterThanZero(inputs.SourceWidth),
            Ensure.GreaterThanZero(inputs.SourceHeight));
        (int Width, int Height) target = EditWorkingResolution.Resolve(
            current.Width,
            current.Height,
            NativeMegapixels(p),
            maxDimension: p.MaxDimension);
        Output<Slot.Image> encPixels = EditWorkingResolution.ScaleImage(
            g,
            Nodes.SourceScale,
            LoadImage.ImageOut(EditNodes.Source),
            current,
            target);
        g[Nodes.Encode] = new VAEEncode { Pixels = encPixels, Vae = vae0 };

        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = p.Denoise,
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
