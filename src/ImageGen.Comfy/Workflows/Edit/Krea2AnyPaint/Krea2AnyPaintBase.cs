using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.Krea2AnyPaint;

/// <summary>
/// Arbitrary-mask INPAINT / OUTPAINT on <b>Krea 2 Turbo</b> via the yijunwang2 <b>AnyPaint</b> LoRA and the
/// ComfyUI-Krea2-AnyPaint node pack. One graph handles interior inpainting, border outpainting, disconnected
/// regions, and mixed edits: the region to regenerate is the union of the painted mask and any added padding, and
/// everything outside it is preserved.
///
/// <para><b>How it differs from the FLUX Fill path.</b> Fill (<see cref="FluxFillBase"/>) makes the mask a native
/// conditioning channel and pastes the untouched pixels back afterwards through a colour-corrected composite. AnyPaint
/// instead conditions on a full-canvas <em>semantic reference</em> (a 384px preview of the desired result, seen by
/// both Krea 2's reference attention and its Qwen3-VL encoder) and pins the known tokens at every denoise step via a
/// token-aligned <c>noise_mask</c>. Preservation is therefore intrinsic to sampling — there is deliberately NO
/// paste-back composite, and adding one would be wrong (the model has already kept the known pixels exact).</para>
///
/// <para><b>The three custom nodes</b> (see <see cref="Krea2AnyPaintPrepare"/> / <see cref="Krea2AnyPaintEncode"/> /
/// <see cref="Krea2AnyPaintModelPatch"/>): Prepare builds the padded known canvas plus the generate/keep masks and
/// the semantic reference; Encode attaches the reference latents to the positive conditioning and produces the
/// latent + noise mask; ModelPatch registers the reference over the target grid and caches its isolated K/V (once per
/// run). ModelPatch goes AFTER the LoRA loader and before the sampler.</para>
///
/// <para>Krea 2 Turbo settings are its own: 8 steps, euler/simple, cfg 1 (distilled, no negative — the negative is
/// wired only for graph symmetry), LoRA strength 1.0. The prepared source-plus-padding canvas is normalized to the
/// native edit MP budget before encoding; its keep mask takes the exact same resize.</para>
/// </summary>
public abstract class Krea2AnyPaintBase : EditWorkflow<Krea2AnyPaintParams>
{
    /// <summary>Only the painted region and/or the added padding change; every other pixel is pinned each step.</summary>
    public override bool PreservesComposition => true;

    /// <summary>AnyPaint's prompt describes the whole intended composition ("a complete coherent image…"), not an
    /// instruction or a region patch — the reference attention conditions on the full canvas.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    protected static readonly IReadOnlyList<ParamSpec> AnyPaintSchema =
    [
        .. EditWorkflowBase.SharedSchema.Where(s => s.Key != WorkflowParamKeys.Denoise),
        new() { Key = WorkflowParamKeys.ReferenceMaxEdge, Type = ParamType.Int, Min = 128, Max = 768, Step = 16, Label = "Reference detail (px)" },
        new() { Key = WorkflowParamKeys.BoundaryRedrawPx, Type = ParamType.Int, Min = 0, Max = 256, Label = "Boundary redraw (px)" },
        new() { Key = WorkflowParamKeys.VlmReference, Type = ParamType.Bool, Label = "VLM reference" },
        new() { Key = WorkflowParamKeys.KvCache, Type = ParamType.Bool, Label = "Reference K/V cache" },
        .. Krea2Rebalance.Schema,
    ];

    /// <summary>Produce the interior region to regenerate (null = none, a pure outpaint) and the per-side padding
    /// that grows the canvas.</summary>
    protected abstract void ResolveRegion(ComfyWorkflowGraph g, Krea2AnyPaintParams p, WorkflowInputs inputs,
        out Output<Slot.Mask>? generatedMask, out int left, out int top, out int right, out int bottom);

    protected override (int Width, int Height) EtaRenderSize(
        Krea2AnyPaintParams p,
        ResolvedRequirements req,
        int sourceWidth,
        int sourceHeight) =>
        EditWorkingResolution.Resolve(
            sourceWidth + p.PadLeft + p.PadRight,
            sourceHeight + p.PadTop + p.PadBottom);

    protected override ComfyWorkflowGraph Build(Krea2AnyPaintParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // 4/5/6 + LoadImage "10"
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // AnyPaint LoRA (model-only), "90"

        // Register the semantic reference over the target grid + cache its isolated K/V. AFTER the LoRA, before the sampler.
        g[Nodes.ModelPatch] = new Krea2AnyPaintModelPatch { Model = model0, KvCache = p.KvCache };
        Output<Slot.Model> patched = Krea2AnyPaintModelPatch.Out(Nodes.ModelPatch);

        ResolveRegion(g, p, inputs, out Output<Slot.Mask>? generatedMask, out int left, out int top, out int right, out int bottom);

        g[Nodes.Prepare] = new Krea2AnyPaintPrepare
        {
            Source = LoadImage.ImageOut(EditNodes.Source),
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
            ReferenceMaxEdge = p.ReferenceMaxEdge,
            BoundaryRedrawPx = p.BoundaryRedrawPx,
            GeneratedMask = generatedMask,
        };

        (int Width, int Height) current = (
            Ensure.GreaterThanZero(inputs.SourceWidth) + left + right,
            Ensure.GreaterThanZero(inputs.SourceHeight) + top + bottom);
        (int Width, int Height) target = EditWorkingResolution.Resolve(current.Width, current.Height);
        Output<Slot.Image> knownImage = Krea2AnyPaintPrepare.KnownImageOut(Nodes.Prepare);
        Output<Slot.Mask> keepMask = Krea2AnyPaintPrepare.KeepMaskOut(Nodes.Prepare);
        EditWorkingResolution.ScalePair(
            g,
            Nodes.WorkingImage,
            Nodes.WorkingMaskAsImage,
            Nodes.WorkingMaskImage,
            Nodes.WorkingMask,
            current,
            target,
            ref knownImage,
            ref keepMask);

        g[Nodes.Encode] = new Krea2AnyPaintEncode
        {
            Clip = clip0,
            Prompt = inputs.Positive,
            Vae = vae0,
            SemanticReference = Krea2AnyPaintPrepare.SemanticReferenceOut(Nodes.Prepare),
            KnownImage = knownImage,
            KeepMask = keepMask,
            VlmReference = p.VlmReference,
        };

        // Krea 2's per-layer conditioning rebalance (the uncensor knob), spliced AFTER the AnyPaint encode: the node
        // rescales only the conditioning tensor and shallow-copies the extras dict, so the reference latents Encode
        // attached ride through untouched. Neutral knobs emit no node (matching the other Krea 2 graphs).
        Output<Slot.Conditioning> positive = Krea2Rebalance.Apply(
            g, Krea2AnyPaintEncode.PositiveOut(Nodes.Encode), p.Multiplier, p.PerLayerWeights, Nodes.Rebalance);

        // Krea 2 Turbo runs at cfg 1, so the negative is inert; wired for graph symmetry (matching the Krea 2 edit paths).
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };

        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = patched,
            Positive = positive,
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = Krea2AnyPaintEncode.LatentOut(Nodes.Encode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Krea 2 AnyPaint's own node ids, named by role; the model/CLIP/VAE/source head reuses
/// <see cref="EditWorkflow{TParams}"/>'s <c>EditNodes</c> (4/5/6/10) and the LoRA reuses <c>ComfyGraph.ApplyLora</c>'s
/// default "90".</summary>
internal static class Nodes
{
    public const string ModelPatch = "91";
    public const string Rebalance = "15";
    public const string Prepare = "20";
    public const string WorkingImage = "172";
    public const string WorkingMaskAsImage = "173";
    public const string WorkingMaskImage = "174";
    public const string WorkingMask = "175";
    public const string Encode = "21";
    public const string Mask = "22";
    public const string Negative = "16";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}

/// <summary>Krea 2 AnyPaint parameters, shared by the inpaint and outpaint subclasses: the shared loader-head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the Turbo sampler settings,
/// the AnyPaint LoRA (<c>lora</c>/<c>lora_strength</c>), the reference/preservation knobs
/// (<c>reference_max_edge</c>/<c>boundary_redraw_px</c>/<c>vlm_reference</c>/<c>kv_cache</c>), Krea 2's per-layer
/// conditioning rebalance (<c>rebalance_multiplier</c> + <c>per_layer_weights</c>), the outpaint per-side
/// pads, and the app's single-sourced <c>seed</c>. The <c>*Req</c> reads are <c>required</c>;
/// <c>vlm_reference</c>/<c>kv_cache</c> default to true; the <c>pad_*</c> reads default to 0 (no growth).</summary>
public sealed record Krea2AnyPaintParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMaxEdge)]
    [Range(128, 768)] public required int ReferenceMaxEdge { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BoundaryRedrawPx)]
    [Range(0, 256)] public int BoundaryRedrawPx { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RebalanceMultiplier)]
    [Range(1.0, 8.0)] public required double Multiplier { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PerLayerWeights)] public required string PerLayerWeights { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VlmReference)] public bool VlmReference { get; init; } = true;
    [JsonPropertyName(WorkflowParamKeys.KvCache)] public bool KvCache { get; init; } = true;
    [JsonPropertyName(WorkflowParamKeys.PadLeft)]
    [Range(0, 4096)] public int PadLeft { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTop)]
    [Range(0, 4096)] public int PadTop { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRight)]
    [Range(0, 4096)] public int PadRight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottom)]
    [Range(0, 4096)] public int PadBottom { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
