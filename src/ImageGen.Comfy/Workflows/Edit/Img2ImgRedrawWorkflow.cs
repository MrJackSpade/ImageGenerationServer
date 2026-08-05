using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

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
    private static readonly IReadOnlyList<ParamSpec> RedrawSchema = EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise).Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Denoise,         Type = ParamType.Double, Min = 0.0, Max = 1.0, Step = 0.01, Label = "Redraw strength" },
        new() { Key = WorkflowParamKeys.RequiredPrefix, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Negative,        Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipSkip,       Type = ParamType.Int },
        // Flow-shift for the models that carry one (Chroma's ModelSamplingAuraFlow). Unset = omit the node.
        new() { Key = WorkflowParamKeys.Shift,           Type = ParamType.Double },
        // The model's trained pixel budget (width × height of one of its native aspect buckets). A source meaningfully
        // over it is downscaled to it before the encode; 0 = sample the source at its own resolution, no rescale.
        new() { Key = WorkflowParamKeys.NativePixels,   Type = ParamType.Int },
    }).ToArray();

    protected override ComfyWorkflowGraph Build(Img2ImgRedrawParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
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

        // Run at the model's NATIVE resolution. The source pose comes from another editor at its own size (often over
        // budget and off the model's aspect buckets); running a 2B anime/photo checkpoint far from its trained ~1 MP is
        // what makes it pad the frame with repeated/decorative junk. So downscale the source to the model's native pixel
        // budget (aspect preserved, snapped to /16) before the img2img — the same "render at a native bucket" the
        // generate path does. The result is left at that native resolution (a redraw is already a destructive
        // re-render; no point up-scaling it back). Only downscales. No budget declared → the source is sampled at its
        // own resolution; a budget with a broken (zero-dimension) source is refused, not silently sampled at raw scale.
        static int Snap16(int v) => Math.Max(16, (int)Math.Round(v / 16.0) * 16);
        long budget = p.NativePixels ?? 0;   // no budget declared → sample the source at its own resolution
        int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
        Output<Slot.Image> encPixels = LoadImage.ImageOut(EditNodes.Source);
        if (budget > 0)
        {
            // A budget is declared, so downscale to it. The source is a still with measured dims — refuse a zero
            // rather than silently sampling the raw source at the wrong scale.
            Ensure.GreaterThanZero(sw);
            Ensure.GreaterThanZero(sh);
            double f = Math.Sqrt(budget / ((double)sw * sh));
            if (f < 0.98)   // meaningfully over budget → downscale to native
            {
                g[Nodes.SourceScale] = new ImageScale
                {
                    Image = LoadImage.ImageOut(EditNodes.Source),
                    UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
                    Width = Snap16((int)Math.Round(sw * f)),
                    Height = Snap16((int)Math.Round(sh * f)),
                    Crop = ComfyWidgets.Crop.Disabled,
                };
                encPixels = ImageScale.Out(Nodes.SourceScale);
            }
        }

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

/// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes).</summary>
file static class Nodes
{
    public const string ClipSkip = "19";
    public const string TokenizerOptions = "17";
    public const string Positive = "13";
    public const string Negative = "14";
    public const string Guidance = "15";
    public const string ModelSampling = "16";
    public const string SourceScale = "11";
    public const string Encode = "12";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}

/// <summary>Img2img-redraw parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the sampler settings and the redraw <c>denoise</c> strength (all <c>required</c>),
/// and the optional per-model knobs: <c>required_prefix</c>/<c>negative</c> (nullable strings), <c>clip_skip</c>/
/// <c>native_pixels</c> (Has-guarded nullable ints), and <c>guidance</c>/<c>shift</c> (nullable doubles — the node they
/// drive is emitted only when set). <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record Img2ImgRedrawParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]         public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]    public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]       public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]  public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]      public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]        public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]      public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.0, 1.0)]                                    public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]       public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)]
    [AllowNullable("null = the config didn't set clip skip; the CLIPSetLastLayer node is emitted only when set, distinct from a real 0")] public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]
    [AllowNullable("null = the config declares no distilled guidance; the FluxGuidance node is emitted only when set, distinct from a real 0")] public double? Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]
    [AllowNullable("null = the config declares no flow shift; the ModelSamplingAuraFlow node is emitted only when set, distinct from a real 0")] public double? Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.NativePixels)]
    [AllowNullable("null = the config declares no native pixel budget (source sampled at its own resolution); distinct from a real 0")] public int? NativePixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]           public long Seed { get; init; }
}
