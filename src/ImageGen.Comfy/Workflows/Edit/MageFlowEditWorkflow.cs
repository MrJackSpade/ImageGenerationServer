using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Mage-Flow-Edit instruction-based image editing (Mage-VAE + Native-Resolution DiT + Qwen3-VL). One unified node —
/// <c>TextEncodeMageFlowEdit</c> — takes the CLIP, the instruction (+ optional negative), the VAE and the reference
/// image(s) and emits (positive, negative) conditioning plus a zero latent sized to the output. The primary edited
/// image is <c>image_1</c>; extra references are <c>image_2..N</c>. Width/height are left 0 so the node follows the
/// (pre-scaled) reference size — Mage's RoPE aligns reference and target by position, so all references are resized
/// to the output resolution before encoding. The source is pre-scaled to the ~1MP native range (aligned to /16),
/// mirroring the official <c>image_mage_flow_edit_int8</c> template. Flow shift (6.0) is baked in at load.
/// </summary>
public abstract class MageFlowEditBase : EditWorkflow<MageFlowEditParams>
{
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 512, MinH = 512, MaxW = 2048, MaxH = 2048, Step = 16 };

    /// <summary>Own node ids (source LoadImage is the inherited <c>Nodes.Source</c>). These must NOT reuse the
    /// inherited loader-head ids — <c>Nodes.Clip</c> ("5") / <c>Nodes.Vae</c> ("6") carry the live CLIP/VAE loaders
    /// that <c>clip0</c>/<c>vae0</c> point at; the split-loader path keeps them, so reusing "5"/"6" here would
    /// overwrite the loaders and leave the clip/vae edges dangling into this node's own outputs.</summary>
    private const string ScaledSource = "11";
    private const string Encode = "7";
    private const string Sampler = "12";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(MageFlowEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // UNETLoader / CLIPLoader(type=mage) / VAELoader + LoadImage at "10"

        // Pre-scale the source into Mage's native ~1MP range, aligned to a /16 grid (matches the template's
        // ImageScaleToTotalPixels: lanczos, 1.0 MP, 16-px steps). Keeps a large upload inside the training
        // distribution instead of asking the model to render at, e.g., 3000px.
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 16 };

        // Extra reference images -> image_2, image_3, ... (scaled the same way).
        Dictionary<string, object> refs = new Dictionary<string, object>();
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
        // No reference_max declared → no extra refs (capacity 0). Supplying more references than the capacity is
        // REFUSED, not silently truncated.
        int rm = p.ReferenceMax ?? 0;
        if (refNames.Count > rm)
            throw new RenderValidationException($"This configuration accepts at most {rm} reference image(s); got {refNames.Count}.");
        int rn = refNames.Count;
        for (int i = 0; i < rn; i++)
        {
            string load = $"{40 + i * 2}", scale = $"{41 + i * 2}";
            g[load] = new LoadImage { Image = refNames[i] };
            g[scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(load), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 16 };
            refs[$"image_{i + 2}"] = ImageScaleToTotalPixels.Out(scale);
        }

        g[Encode] = new TextEncodeMageFlowEdit
        {
            Clip = clip0,
            Prompt = inputs.Positive,
            NegativePrompt = inputs.Negative ?? "",
            Vae = vae0,
            Width = 0,      // 0 -> follow the (scaled) reference's own size
            Height = 0,
            BatchSize = 1,
            Image1 = ImageScaleToTotalPixels.Out(ScaledSource),
            Extra = refs.Count > 0 ? refs : null,
        };
        g[Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = TextEncodeMageFlowEdit.PositiveOut(Encode),
            Negative = TextEncodeMageFlowEdit.NegativeOut(Encode),
            LatentImage = TextEncodeMageFlowEdit.LatentOut(Encode),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}

/// <summary>Mage-Flow-Edit parameters, shared by the standard and Turbo subclasses — the shared loader head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, and the
/// optional <c>reference_max</c> cap (Has-guarded nullable int: absent → no extra references). The <c>*Req</c> reads are
/// <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings; <c>seed</c> is the app's single-sourced
/// seed (defaulted). The negative is read from the request inputs, not a param.</summary>
public sealed record MageFlowEditParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}

/// <summary>Mage-Flow-Edit (RL-aligned) — full CFG (cfg 5, negatives supported), ~30 steps.</summary>
public sealed class MageFlowEditWorkflow : MageFlowEditBase { public override string Name => "mage-flow-edit"; }

/// <summary>Mage-Flow-Edit-Turbo — 4-step distilled, cfg 1 (no negative).</summary>
public sealed class MageFlowEditTurboWorkflow : MageFlowEditBase { public override string Name => "mage-flow-edit-turbo"; }
