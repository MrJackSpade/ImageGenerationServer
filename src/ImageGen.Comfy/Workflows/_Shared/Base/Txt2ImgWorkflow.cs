using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>The shared text-to-image parameters, deserialized once from the merged bag. A model with extra knobs
/// declares a record deriving from this (e.g. Krea2's rebalance, HiDream's shift).</summary>
public record Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public string? Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Dual)] public bool Dual { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Latent)] public string? Latent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Auraflow)][AllowNullable("null = the config didn't set auraflow shift; the ModelSamplingAuraFlow node is emitted only when set, distinct from a real 0")] public double? Auraflow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)][AllowNullable("null = the config didn't set guidance; the FluxGuidance node input is emitted only when set, distinct from a real 0")] public double? Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)][AllowNullable("null = the config didn't set clip-skip; the CLIPSetLastLayer node is emitted only when set, distinct from a real 0")] public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)][AllowNullable("null = the config didn't set CFG (a custom-build model supplies its own guidance); 0 is a real CFG value, and RequiredCfg() throws when it's needed and unset")] public double? Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.GenLoraStrengthMin, ParamBounds.GenLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)][AllowNullable("null = the config didn't set a flat width (it may supply an aspect map instead); Dims() throws when neither is present, so a real 0 is never invented")] public int? Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)][AllowNullable("null = the config didn't set a flat height (it may supply an aspect map instead); Dims() throws when neither is present, so a real 0 is never invented")] public int? Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Aspect)] public Dictionary<string, int[]>? Aspect { get; init; }

    /// <summary>The required render size: the aspect map's <paramref name="sub"/> entry, else the flat width/height,
    /// else a refusal — no invented pixel size ever reaches the graph.</summary>
    public (int w, int h) Dims(string sub)
    {
        if (Aspect is not null && Aspect.TryGetValue(sub, out int[]? wh) && wh.Length >= 2)
        {
            return (wh[0], wh[1]);
        }

        if (Width is int w && Height is int h)
        {
            return (w, h);
        }

        throw new RenderValidationException(
            $"This configuration needs a render size — an '{WorkflowParamKeys.Aspect}' map with a '{sub}' entry, or width/height — and declares neither.");
    }

    /// <summary>The clip-loader type, required in the split-loader path — the typed form of <c>StrReq(clip_type)</c>.</summary>
    public string RequiredClipType() => Required(ClipType, WorkflowParamKeys.ClipType);

    /// <summary>The loader kind, required by the standard topology (a custom-Build model that emits its own loader
    /// leaves it unset).</summary>
    public string RequiredLoader() => Required(Loader, WorkflowParamKeys.Loader);

    /// <summary>The empty-latent kind, required by the standard topology (a custom-Build model that emits its own
    /// latent leaves it unset).</summary>
    public string RequiredLatent() => Required(Latent, WorkflowParamKeys.Latent);

    /// <summary>CFG, required by the standard sampler (a custom-Build model with its own guidance — e.g. WAN-MoE's
    /// dual cfg_high/cfg_low — leaves it unset).</summary>
    public double RequiredCfg() => Cfg ?? throw new RenderValidationException($"This configuration needs a value for '{WorkflowParamKeys.Cfg}' and none is set. It must supply one — there is no default.");

    private static string Required(string? value, string key) => value is { Length: > 0 } v
        ? v
        : throw new RenderValidationException($"This configuration needs a value for '{key}' and none is set. It must supply one — there is no default.");
}

/// <summary>
/// Typed base for the text-to-image workflows: a <see cref="Workflow{TParams}"/> carrying the shared txt2img topology
/// (loader head → optional LoRA/clip-skip/model-sampling → text encode → latent → sampler → decode → save) over typed
/// nodes. A model with its own graph overrides <see cref="Workflow{TParams}.Build(TParams, ResolvedRequirements, WorkflowInputs)"/>;
/// one that only tweaks the conditioning/model/image overrides a hook. Subclasses migrate here individually from the
/// older <see cref="Txt2ImgWorkflowBase"/>.
/// </summary>
public abstract class Txt2ImgWorkflow<TParams> : Workflow<TParams> where TParams : Txt2ImgParams
{
    public override WorkflowKind Kind => WorkflowKind.Generate;
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override bool PromptDirectsMotion => true;
    public override IReadOnlyList<ParamSpec> Schema => Txt2ImgWorkflowBase.SharedSchema;

    /// <summary>The shared txt2img topology's node ids (values preserved so the emitted graph is byte-identical).</summary>
    protected static class Nodes
    {
        public const string Model = "4";
        public const string Clip = "20";
        public const string Vae = "21";
        public const string ClipSkip = "10";
        public const string ModelSampling = "11";
        public const string Positive = "6";
        public const string Negative = "7";
        public const string Guidance = "12";
        public const string Latent = "5";
        public const string Sampler = "3";
        public const string Decode = "8";
        public const string Save = "9";
        public const string PostEncode = "13";
        public const string DenoisePatch = "35";
        public const string PostDecode = "36";
    }

    /// <summary>The <c>latent</c> param's kind values — which empty-latent node the topology emits.</summary>
    private static class LatentKind
    {
        public const string Std = "std";
        public const string Sd3 = "sd3";
        public const string Flux2 = "flux2";
        public const string Pixel = "pixel";
    }

    private static string LatentClass(string latent) => latent switch
    {
        LatentKind.Sd3 => ComfyNodeTypes.EmptySD3LatentImage,
        LatentKind.Flux2 => ComfyNodeTypes.EmptyFlux2LatentImage,
        LatentKind.Pixel => ComfyNodeTypes.EmptyChromaRadianceLatentImage,
        _ => ComfyNodeTypes.EmptyLatentImage,
    };

    protected override ComfyWorkflowGraph Build(TParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string file = req.RequiredCheckpoint();
        LoaderKind loader = LoaderKindWire.Parse(p.RequiredLoader());
        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));

        ComfyWorkflowGraph g = new();
        Output<Slot.Model> modelSrc;
        Output<Slot.Clip> clipSrc;
        Output<Slot.Vae> vaeSrc;

        if (loader == LoaderKind.Checkpoint)
        {
            g[Nodes.Model] = new CheckpointLoaderSimple { CkptName = file };
            modelSrc = CheckpointLoaderSimple.ModelOut(Nodes.Model);
            clipSrc = CheckpointLoaderSimple.ClipOut(Nodes.Model);
            vaeSrc = CheckpointLoaderSimple.VaeOut(Nodes.Model);
        }
        else
        {
            g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(file);
            modelSrc = UNETLoader.ModelOut(Nodes.Model);
            string clipType = p.RequiredClipType();
            g[Nodes.Clip] = p.Dual
                ? new DualCLIPLoader { ClipName1 = req.TextEncoder(0), ClipName2 = req.TextEncoder(1), Type = clipType, Device = ComfyWidgets.Device.Default }
                : new CLIPLoader { ClipName = req.TextEncoder(0), Type = clipType, Device = ComfyWidgets.Device.Default };
            clipSrc = new Output<Slot.Clip>(Nodes.Clip, 0);
            g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
            vaeSrc = VAELoader.VaeOut(Nodes.Vae);
        }

        modelSrc = ComfyGraph.ApplyLora(g, modelSrc, p.Lora, p.LoraStrength);

        if (loader == LoaderKind.Checkpoint && p.ClipSkip is int clipSkip && clipSkip > 0)
        {
            g[Nodes.ClipSkip] = new CLIPSetLastLayer { Clip = clipSrc, StopAtClipLayer = -Math.Abs(clipSkip) };
            clipSrc = CLIPSetLastLayer.ClipOut(Nodes.ClipSkip);
        }

        (modelSrc, clipSrc) = ComfyGraph.ApplyLoraStack(g, modelSrc, clipSrc, inputs.Loras);

        if (p.Auraflow is double shift)
        {
            g[Nodes.ModelSampling] = new ModelSamplingAuraFlow { Model = modelSrc, Shift = shift };
            modelSrc = ModelSamplingAuraFlow.Out(Nodes.ModelSampling);
        }

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clipSrc };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clipSrc };
        Output<Slot.Conditioning> posSrc = CLIPTextEncode.Out(Nodes.Positive);
        if (p.Guidance is double guid)
        {
            g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = guid };
            posSrc = FluxGuidance.Out(Nodes.Guidance);
        }

        posSrc = PostEncodePositive(g, posSrc, p);

        g[Nodes.Latent] = new EmptyLatent(LatentClass(p.RequiredLatent())) { Width = w, Height = h, BatchSize = 1 };
        modelSrc = PatchDenoiseModel(g, modelSrc, vaeSrc, p);
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = modelSrc,
            Positive = posSrc,
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = EmptyLatent.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vaeSrc };
        g[Nodes.Save] = new SaveImage { Images = PostDecodeImage(g, VAEDecode.Out(Nodes.Decode), p), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }

    /// <summary>Transform the positive conditioning after text-encode/guidance (default identity). Node "13" is reserved.</summary>
    protected virtual Output<Slot.Conditioning> PostEncodePositive(ComfyWorkflowGraph g, Output<Slot.Conditioning> positive, TParams p) => positive;

    /// <summary>Patch the denoise model before the sampler (default identity). Node "35" is reserved.</summary>
    protected virtual Output<Slot.Model> PatchDenoiseModel(ComfyWorkflowGraph g, Output<Slot.Model> model, Output<Slot.Vae> vae, TParams p) => model;

    /// <summary>Transform the decoded image before save (default identity). Node "36" is reserved.</summary>
    protected virtual Output<Slot.Image> PostDecodeImage(ComfyWorkflowGraph g, Output<Slot.Image> image, TParams p) => image;
}
