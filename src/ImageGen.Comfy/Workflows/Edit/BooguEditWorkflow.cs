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
public sealed class BooguEditWorkflow : EditWorkflowBase
{
    public override string Name => "boogu-edit";

    /// <summary>Boogu runs real CFG with an (optionally empty) negative; expose it like the inpaint editor does.</summary>
    public override IReadOnlyList<ParamSpec> Schema => BooguSchema;
    private static readonly IReadOnlyList<ParamSpec> BooguSchema = SharedSchema
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

    /// <summary>The TextEncodeBooguEdit node's input-field names. Values are the ComfyUI input names, preserved
    /// exactly — note the dotted Autogrow key <c>images.image_1</c> (see Build).</summary>
    private static class Inputs
    {
        public const string Clip = "clip";
        public const string Prompt = "prompt";
        public const string NegativePrompt = "negative_prompt";
        public const string Vae = "vae";
        public const string ImagesImage1 = "images.image_1";
    }

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // 4=unet 5=clip(boogu) 6=vae 10=LoadImage(source)

        // Lift of the official Comfy-Org image_boogu_image_0_1_edit template. Resize the source to ~1 MP (lanczos) —
        // 1 MP is what the template uses; rendering bigger than the model's ~1 MP reference just soft-upscales. The
        // "megapixels" param stays for tuning but defaults to 1.0.
        double mp = p.DblReq(WorkflowParamKeys.Megapixels);
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = mp, resolution_steps = 16 });

        // Apply the flow-matching shift EXPLICITLY (the template does this even though Boogu's model class also carries
        // 3.16) — sampling quality depends on it being on the model the scheduler/sampler see.
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingAuraFlow, new { model = model0, shift = 3.16 });
        object modelS = ComfyGraph.Ref(ModelSampling, 0);

        // Boogu edit conditioning: instruction (+ vision tokens) on positive, empty/explicit negative => DROP. The node
        // VAE-encodes the reference itself and returns positive[0] / negative[1] with the reference latent on both. The
        // reference is the node's "images" Autogrow (COMFY_AUTOGROW_V3) input, keyed by its finalized dotted path
        // "images.image_1" (id "." template-name), which the v3 executor rebuilds into images={"image_1": <IMAGE>}. A
        // bare "image_1" is rejected; the dotted key needs a Dictionary (a C# anonymous type can't express the dot).
        var neg = inputs.Negative ?? p.Str(WorkflowParamKeys.Negative) ?? "";
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.TextEncodeBooguEdit, new Dictionary<string, object>
        {
            [Inputs.Clip] = clip0,
            [Inputs.Prompt] = inputs.Positive,
            [Inputs.NegativePrompt] = neg,
            [Inputs.Vae] = vae0,
            [Inputs.ImagesImage1] = ComfyGraph.Ref(ScaledSource, 0),
        });

        // Output latent: an EMPTY latent sized to the resized source (template uses GetImageSize -> EmptyLatentImage),
        // NOT a VAEEncode of the source. Sample with SamplerCustom + KSamplerSelect(dpmpp_2m) + BasicScheduler sigmas —
        // a plain euler KSampler produces soft/blurry edits.
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[Latent] = ComfyGraph.Node(ComfyNodeTypes.EmptyLatentImage, new { width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1), batch_size = 1 });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Sigmas] = ComfyGraph.Node(ComfyNodeTypes.BasicScheduler, new { model = modelS, scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), steps = p.IntReq(WorkflowParamKeys.Steps), denoise = 1.0 });

        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustom, new
        {
            model = modelS,
            add_noise = true,
            noise_seed = ComfyGraph.Seed(p),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            positive = ComfyGraph.Ref(Encode, 0),
            negative = ComfyGraph.Ref(Encode, 1),
            sampler = ComfyGraph.Ref(SamplerSelect, 0),
            sigmas = ComfyGraph.Ref(Sigmas, 0),
            latent_image = ComfyGraph.Ref(Latent, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
