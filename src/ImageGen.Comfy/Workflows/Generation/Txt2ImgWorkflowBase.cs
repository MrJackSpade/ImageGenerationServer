namespace ImageGen.Comfy;

/// <summary>
/// Base for the text-to-image workflows. Every generation model has its OWN workflow subclass (its own name and
/// VRAM band), but they share this one parameterized txt2img topology. A model that needs to diverge overrides
/// <see cref="Build"/>.
/// </summary>
public abstract class Txt2ImgWorkflowBase : IWorkflow
{
    public abstract string Name { get; }
    public WorkflowKind Kind => WorkflowKind.Generate;
    public virtual WorkflowMedia Media => WorkflowMedia.Image;
    public virtual bool PromptDirectsMotion => true;

    /// <summary>The model's stepped frame-count rule (valid clip length = Base + k*Step), or null for stills / any
    /// length. Declared virtual here (mirroring <see cref="EditWorkflowBase.FrameRule"/>) so a text-to-VIDEO generator
    /// can enforce its grid at enqueue via <see cref="IWorkflow.Normalize"/>; null default keeps every existing
    /// txt2img workflow byte-identical.</summary>
    public virtual FrameRule? FrameRule => null;

    /// <summary>Whether the output clip carries a native audio track. Null-audio default; only a real audio video model
    /// (MiniMax-H3) sets it. Mirrors <see cref="IWorkflow.HasAudio"/>.</summary>
    public virtual bool HasAudio => false;

    /// <summary>The full menu of txt2img parameters. Concrete values + which are UI-exposed come from the configuration.</summary>
    public virtual IReadOnlyList<ParamSpec> Schema => SharedSchema;

    protected static readonly IReadOnlyList<ParamSpec> SharedSchema = new ParamSpec[]
    {
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKinds.Choices },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        // "pixel" = a pixel-space latent: (B,3,H,W) at spatial downscale 1, for models that diffuse
        // directly on RGB and have no VAE (PixelDiT, Chroma Radiance). Such a model is paired with the
        // identity "VAE" (pixel_space_vae.safetensors -> comfy's PixelspaceConversionVAE), so the VAEDecode
        // in the shared topology is a no-op passthrough and the graph stays byte-identical elsewhere.
        new() { Key = WorkflowParamKeys.Latent,    Type = ParamType.Enum,   Choices = new[] { LatentKind.Std, LatentKind.Sd3, LatentKind.Flux2, LatentKind.Pixel } },
        new() { Key = WorkflowParamKeys.Auraflow,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.ClipSkip, Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = 1,  Max = 100, Label = "Steps", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = 1,  Max = 30,  Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String, Label = "Sampler" },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Width,     Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Height,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Aspect,    Type = ParamType.String },   // { square/landscape/portrait: [w,h] } dims map
        // Video shapes for the text-to-VIDEO generators (wan/hunyuan/minimax-h3): clip length (frames) and playback
        // fps. Present on the shared schema so a config that exposes `length` renders it as a NUMERIC control — the
        // control's type is read from here, and without an entry an exposed length falls back to a text box. Image
        // models simply never expose these. Mirrors EditWorkflowBase.
        new() { Key = WorkflowParamKeys.Length,    Type = ParamType.Int,    Label = "Frames", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Fps,       Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.RequiredPrefix,     Type = ParamType.String },
        new() { Key = WorkflowParamKeys.NegativeSupported,  Type = ParamType.Bool },
        // Optional LoRA on the base model — lets a config be a "base + LoRA" txt2img variant (e.g. a Z-Image LoRA).
        new() { Key = WorkflowParamKeys.Lora,          Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.LoraStrength, Type = ParamType.Double, Min = 0.0, Max = 2.0, Label = "LoRA strength" },
    };

    /// <summary>The shared txt2img topology's node ids, named by role. The VALUE is the graph-local node key
    /// (preserved exactly, so the emitted ComfyUI graph — and the tests that assert on ids — are byte-identical); the
    /// NAME gives each node its meaning at the use sites, replacing the bare <c>"4"</c>/<c>"20"</c> literals. Ids
    /// <c>"13"/"35"/"36"</c> are reserved for the <see cref="PostEncodePositive"/> / <see cref="PatchDenoiseModel"/> /
    /// <see cref="PostDecodeImage"/> hooks (a subclass inserts its node there).</summary>
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

    /// <summary>The <c>latent</c> param's kind values — which empty-latent node the topology emits. Written once so
    /// the schema's choice list and the emit-time selection share one spelling.</summary>
    private static class LatentKind
    {
        public const string Std = "std";
        public const string Sd3 = "sd3";
        public const string Flux2 = "flux2";
        public const string Pixel = "pixel";
    }

    public virtual Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string file = req.RequiredCheckpoint();
        LoaderKind loader = p.Loader();
        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));

        Dictionary<string, object> wf = new Dictionary<string, object>();
        object modelSrc, clipSrc, vaeSrc;

        if (loader == LoaderKind.Checkpoint)
        {
            wf[Nodes.Model] = ComfyGraph.Node(ComfyNodeTypes.CheckpointLoaderSimple, new { ckpt_name = file });
            modelSrc = ComfyGraph.Ref(Nodes.Model, 0); clipSrc = ComfyGraph.Ref(Nodes.Model, 1); vaeSrc = ComfyGraph.Ref(Nodes.Model, 2);
        }
        else
        {
            wf[Nodes.Model] = ComfyGraph.DiffusionLoader(file);
            modelSrc = ComfyGraph.Ref(Nodes.Model, 0);
            string clipType = p.StrReq(WorkflowParamKeys.ClipType);
            wf[Nodes.Clip] = p.Bool(WorkflowParamKeys.Dual)
                ? ComfyGraph.Node(ComfyNodeTypes.DualCLIPLoader, new { clip_name1 = req.TextEncoder(0), clip_name2 = req.TextEncoder(1), type = clipType, device = "default" })
                : ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = clipType, device = "default" });
            clipSrc = ComfyGraph.Ref(Nodes.Clip, 0);
            wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
            vaeSrc = ComfyGraph.Ref(Nodes.Vae, 0);
        }

        modelSrc = ComfyGraph.ApplyLora(wf, modelSrc, p);   // optional LoRA on the base model

        // clip-skip applies only to a checkpoint's baked CLIP (SD/SDXL); absent = no skip — an optional feature, not a default value.
        if (loader == LoaderKind.Checkpoint && p.Has(WorkflowParamKeys.ClipSkip) && p.IntReq(WorkflowParamKeys.ClipSkip) is int clipSkip && clipSkip > 0)
        {
            wf[Nodes.ClipSkip] = ComfyGraph.Node(ComfyNodeTypes.CLIPSetLastLayer, new { clip = clipSrc, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clipSrc = ComfyGraph.Ref(Nodes.ClipSkip, 0);
        }

        // The user's LoRA stack (model + CLIP), chained on top of the preset LoRA and clip-skip so a style/character
        // LoRA reaches the text encoders below. Nodes 91+; an empty stack is a no-op and the graph stays byte-identical.
        (modelSrc, clipSrc) = ComfyGraph.ApplyLoraStack(wf, modelSrc, clipSrc, inputs.Loras);

        if (p.DblOrNull(WorkflowParamKeys.Auraflow) is double shift)
        {
            wf[Nodes.ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingAuraFlow, new { model = modelSrc, shift });
            modelSrc = ComfyGraph.Ref(Nodes.ModelSampling, 0);
        }

        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clipSrc });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip = clipSrc });
        object posSrc = ComfyGraph.Ref(Nodes.Positive, 0);
        if (p.DblOrNull(WorkflowParamKeys.Guidance) is double g)
        {
            wf[Nodes.Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(Nodes.Positive, 0), guidance = g });
            posSrc = ComfyGraph.Ref(Nodes.Guidance, 0);
        }
        posSrc = PostEncodePositive(wf, posSrc, p);   // model-specific positive-conditioning transform (default: identity)

        string latent = p.StrReq(WorkflowParamKeys.Latent);
        string latentClass = latent == LatentKind.Sd3 ? "EmptySD3LatentImage"
                        : latent == LatentKind.Flux2 ? "EmptyFlux2LatentImage"
                        : latent == LatentKind.Pixel ? "EmptyChromaRadianceLatentImage" : "EmptyLatentImage";
        wf[Nodes.Latent] = ComfyGraph.Node(latentClass, new { width = w, height = h, batch_size = 1 });
        modelSrc = PatchDenoiseModel(wf, modelSrc, vaeSrc, p);   // model-patch hook before the sampler (default: identity)
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = modelSrc,
            positive = posSrc,
            negative = ComfyGraph.Ref(Nodes.Negative, 0),
            latent_image = ComfyGraph.Ref(Nodes.Latent, 0),
        });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = vaeSrc });
        // post-decode hook before save (default: identity) — a pixelizer inserts a final PixelQuantize here.
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = PostDecodeImage(wf, ComfyGraph.Ref(Nodes.Decode, 0), p), filename_prefix = "forgemcp" });
        return wf;
    }

    /// <summary>Hook to transform the positive conditioning after text-encode (and any FluxGuidance) and before the
    /// sampler. The default is identity — the graph is byte-identical to the plain txt2img topology. A model whose
    /// conditioning needs a post-encode node (e.g. Krea 2's per-layer rebalance) overrides this instead of the whole
    /// <see cref="Build"/>. Node id "13" is reserved by the base for the inserted node so overrides don't collide.</summary>
    protected virtual object PostEncodePositive(Dictionary<string, object> wf, object positive, ParamValues p) => positive;

    /// <summary>Hook to patch the denoise model after all loaders/LoRA/sampling-shift and before the sampler. The
    /// default is identity — the graph is byte-identical to the plain txt2img topology. A model that diffuses under a
    /// per-step model patch (e.g. the pixel-manifold projection) overrides this instead of the whole <see cref="Build"/>.
    /// Node id "35" is reserved by the base for the inserted patch so overrides don't collide with the loader nodes.</summary>
    protected virtual object PatchDenoiseModel(Dictionary<string, object> wf, object model, object vae, ParamValues p) => model;

    /// <summary>Hook to transform the decoded image after VAEDecode and before SaveImage. The default is identity —
    /// the graph is byte-identical to the plain txt2img topology. A model whose output needs a deterministic post-pass
    /// (e.g. a final PixelQuantize render) overrides this. Node id "36" is reserved by the base for the inserted node.</summary>
    protected virtual object PostDecodeImage(Dictionary<string, object> wf, object image, ParamValues p) => image;
}
