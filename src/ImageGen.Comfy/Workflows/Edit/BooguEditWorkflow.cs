using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Boogu-Image-0.1-Edit (instruction image edit / TI2I). Built on ComfyUI's native <c>TextEncodeBooguEdit</c> node,
/// which does, in one node, the reference plumbing the Qwen/Flux editors wire up by hand: it caps the reference to
/// the VLM's 384px vision input, VAE-encodes a ~1K (16px-aligned) reference latent, and emits BOTH conditionings —
/// the instruction's vision tokens on the positive only, the reference latent on positive AND negative (so it cancels
/// under CFG and preserves identity while CFG amplifies the instruction). We just hand it the source image and sample
/// from a matching-size empty latent.
///
/// ComfyUI's Boogu model class bakes the flow shift (3.16) in at load, so there is no ModelSampling node here. The
/// model card targets ~1K reference resolution, so the source is scaled to 1MP on a 16px grid (FLUX VAE /8 x patch 2)
/// and that same scaled image both feeds the encode node and seeds the sampling latent (denoise 1.0, shape only). The
/// 0.1 Edit release supports a single reference image, so only the source is used (no extra reference slots).
/// </summary>
public sealed class BooguEditWorkflow : EditWorkflow<BooguParams>
{
    public override string Name => "boogu-edit";

    /// <summary>Boogu runs real CFG with an (optionally empty) negative; expose it like the inpaint editor does.</summary>
    public override IReadOnlyList<ParamSpec> Schema => BooguSchema;
    private static readonly IReadOnlyList<ParamSpec> BooguSchema = EditWorkflowBase.SharedSchema
        .Concat(new ParamSpec[]
        {
            new() { Key = WorkflowParamKeys.Negative,   Type = ParamType.String },
            new() { Key = WorkflowParamKeys.Megapixels, Type = ParamType.Double, Min = 0.5, Max = 4.0, Label = "Edit resolution (MP)" },
        })
        .ToArray();

    /// <summary>This workflow's own node ids.</summary>
    private const string ScaledSource = "11";
    private const string ModelSampling = "33";
    private const string Encode = "13";
    private const string SourceSize = "17";
    private const string Latent = "50";
    private const string SamplerSelect = "16";
    private const string Sigmas = "26";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(BooguParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);   // 4=unet 5=clip(boogu) 6=vae 10=LoadImage(source)

        // Lift of the official Comfy-Org image_boogu_image_0_1_edit template. Resize the source to ~1 MP (lanczos) —
        // 1 MP is what the template uses; rendering bigger than the model's ~1 MP reference just soft-upscales. The
        // "megapixels" param stays for tuning but defaults to 1.0.
        double mp = p.Megapixels;
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = mp, ResolutionSteps = 16 };

        // Apply the flow-matching shift EXPLICITLY (the template does this even though Boogu's model class also carries
        // 3.16) — sampling quality depends on it being on the model the scheduler/sampler see.
        g[ModelSampling] = new ModelSamplingAuraFlow { Model = model0, Shift = 3.16 };
        Output<Slot.Model> modelS = ModelSamplingAuraFlow.Out(ModelSampling);

        // Boogu edit conditioning: instruction (+ vision tokens) on positive, empty/explicit negative => DROP. The node
        // VAE-encodes the reference itself and returns positive[0] / negative[1] with the reference latent on both. The
        // reference is the node's "images" Autogrow (COMFY_AUTOGROW_V3) input, keyed by its finalized dotted path
        // "images.image_1" (id "." template-name), which the v3 executor rebuilds into images={"image_1": <IMAGE>}. A
        // bare "image_1" is rejected; the dotted key is expressed by the record's [JsonPropertyName("images.image_1")].
        string neg = inputs.Negative ?? p.Negative ?? "";
        g[Encode] = new TextEncodeBooguEdit
        {
            Clip = clip0,
            Prompt = inputs.Positive,
            NegativePrompt = neg,
            Vae = vae0,
            ImagesImage1 = ImageScaleToTotalPixels.Out(ScaledSource),
        };

        // Output latent: an EMPTY latent sized to the resized source (template uses GetImageSize -> EmptyLatentImage),
        // NOT a VAEEncode of the source. Sample with SamplerCustom + KSamplerSelect(dpmpp_2m) + BasicScheduler sigmas —
        // a plain euler KSampler produces soft/blurry edits.
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[Latent] = new EmptyLatentFromSize { Width = GetImageSize.WidthOut(SourceSize), Height = GetImageSize.HeightOut(SourceSize), BatchSize = 1 };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Sigmas] = new BasicScheduler { Model = modelS, Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Steps = p.Steps, Denoise = 1.0 };

        g[Sampler] = new SamplerCustom
        {
            Model = modelS,
            AddNoise = true,
            NoiseSeed = ComfyGraph.Seed(p.Seed),
            Cfg = p.Cfg,
            Positive = TextEncodeBooguEdit.PositiveOut(Encode),
            Negative = TextEncodeBooguEdit.NegativeOut(Encode),
            Sampler = KSamplerSelect.Out(SamplerSelect),
            Sigmas = BasicScheduler.Out(Sigmas),
            LatentImage = EmptyLatentFromSize.Out(Latent),
        };
        g[Decode] = new VAEDecode { Samples = SamplerCustom.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>Boogu-edit parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for
/// the typed <c>LoadModel</c>), the CFG diffusion knobs, the reference megapixel budget, and the optional negative. The
/// <c>*Req</c> reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c>/<c>negative</c> are nullable string reads;
/// <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record BooguParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]      public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]    public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]   public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]     public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]   public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    [Range(0.5, 4.0)]                                 public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]    public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]        public long Seed { get; init; }
}
