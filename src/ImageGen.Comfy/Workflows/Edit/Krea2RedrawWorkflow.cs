using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Krea 2 redraw parameters: the shared edit loader-head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings and the polish <c>denoise</c> strength (all
/// <c>required</c>), Krea 2's per-layer conditioning rebalance (<c>rebalance_multiplier</c> + <c>per_layer_weights</c>),
/// the optional base-model <c>lora</c>, and the app's single-sourced <c>seed</c>.</summary>
public sealed record Krea2RedrawParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]             public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]        public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]           public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]     public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]         public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]            public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]          public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.1, 0.9)]                                       public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RebalanceMultiplier)]
    [Range(1.0, 8.0)]                                       public required double Multiplier { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PerLayerWeights)]    public required string PerLayerWeights { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)]              public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]              public long Seed { get; init; }
}

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
/// The source is sampled at its OWN resolution — no rescale. Unlike <see cref="Img2ImgRedrawWorkflow"/> (whose 2B
/// checkpoints must be downscaled to their ~1 MP bucket or they pad the frame with junk), Krea 2 is native at ~1K and
/// holds up to 2K, and a polish pass whose whole purpose is to preserve the incoming image has no business resampling
/// it. Equivalently: that workflow's <c>native_pixels</c> budget is 0 here.
/// </summary>
public sealed class Krea2RedrawWorkflow : EditWorkflow<Krea2RedrawParams>
{
    public override string Name => "krea2-redraw";

    /// <summary>A polish pass is meant to land close to the source at low denoise — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>The prompt describes the whole picture being polished, not a change to make to it.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    /// <summary>Drop the shared <c>denoise</c> (its "source ↔ motion" label and 0 default are wrong here) and re-add it
    /// as the polish strength, plus Krea 2's rebalance knobs. Step 0.01 so the 0.35 default is reachable.</summary>
    public override IReadOnlyList<ParamSpec> Schema => RedrawSchema;
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema = EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise, Type = ParamType.Double, Min = 0.1, Max = 0.9, Step = 0.01,
                Label = "Polish strength",
                Help = "How hard Turbo reworks the source image. ~0.25–0.40 polishes texture and aesthetic while keeping "
                     + "the source's composition; higher redraws more of the image (and drifts toward the prompt rather "
                     + "than the source)." },
    }).Concat(Krea2Rebalance.Schema).ToArray();

    /// <summary>This workflow's own nodes; the model/CLIP/VAE/source head reuses <see cref="EditWorkflow{TParams}.Nodes"/>.</summary>
    private const string Encode = "12";
    private const string Positive = "13";
    private const string Negative = "14";
    private const string Rebalance = "15";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(Krea2RedrawParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);                                 // optional style/quality LoRA

        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        // Node ids 13/14 are the text-encodes on the edit rails, so the rebalance splices in at "15".
        Output<Slot.Conditioning> posSrc = Krea2Rebalance.Apply(g, CLIPTextEncode.Out(Positive), p.Multiplier, p.PerLayerWeights, Rebalance);

        // Source RGB → latent at its native resolution. NO mask, so the whole frame is re-sampled; at denoise < 1 the
        // source's own structure survives and Turbo reworks the texture over it.
        g[Encode] = new VAEEncode { Pixels = LoadImage.ImageOut(Nodes.Source), Vae = vae0 };

        g[Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = p.Denoise,
            Model = model0,
            Positive = posSrc,
            Negative = CLIPTextEncode.Out(Negative),
            LatentImage = VAEEncode.Out(Encode),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}
