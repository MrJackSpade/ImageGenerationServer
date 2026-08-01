namespace ImageGen.Comfy;

/// <summary>
/// Base for the text-to-image workflows. Every generation model has its OWN workflow subclass (its own name and
/// VRAM band), but they share this one txt2img topology — the single graph that already drove all of them through
/// the old <c>BuildWorkflow</c>, just parameterized. A model that needs to diverge overrides <see cref="Build"/>.
/// The node ids and wiring are an exact lift of <c>BuildWorkflow</c> so the emitted graph is byte-identical.
/// </summary>
public abstract class Txt2ImgWorkflowBase : IWorkflow
{
    public abstract string Name { get; }
    public WorkflowKind Kind => WorkflowKind.Generate;
    public virtual WorkflowMedia Media => WorkflowMedia.Image;
    public virtual bool PromptDirectsMotion => true;

    /// <summary>The full menu of txt2img parameters. Concrete values + which are UI-exposed come from the configuration.</summary>
    public virtual IReadOnlyList<ParamSpec> Schema => SharedSchema;

    protected static readonly IReadOnlyList<ParamSpec> SharedSchema = new ParamSpec[]
    {
        new() { Key = "loader",    Type = ParamType.Enum,   Choices = new[] { "checkpoint", "unet", "unet_gguf" }, Default = "checkpoint" },
        new() { Key = "clip_type", Type = ParamType.String },
        new() { Key = "dual",      Type = ParamType.Bool,   Default = false },
        // "pixel" = a pixel-space latent: (B,3,H,W) at spatial downscale 1, for models that diffuse
        // directly on RGB and have no VAE (PixelDiT, Chroma Radiance). Such a model is paired with the
        // identity "VAE" (pixel_space_vae.safetensors -> comfy's PixelspaceConversionVAE), so the VAEDecode
        // in the shared topology is a no-op passthrough and the graph stays byte-identical elsewhere.
        new() { Key = "latent",    Type = ParamType.Enum,   Choices = new[] { "std", "sd3", "flux2", "pixel" }, Default = "std" },
        new() { Key = "auraflow",  Type = ParamType.Double },
        new() { Key = "guidance",  Type = ParamType.Double },
        new() { Key = "clip_skip", Type = ParamType.Int,    Default = 0 },
        new() { Key = "steps",     Type = ParamType.Int,    Default = 25, Min = 1,  Max = 100, Label = "Steps" },
        new() { Key = "cfg",       Type = ParamType.Double, Default = 7,  Min = 1,  Max = 30,  Label = "CFG scale" },
        new() { Key = "sampler",   Type = ParamType.String, Default = "euler", Label = "Sampler" },
        new() { Key = "scheduler", Type = ParamType.String, Default = "normal" },
        new() { Key = "width",     Type = ParamType.Int,    Default = 1024 },
        new() { Key = "height",    Type = ParamType.Int,    Default = 1024 },
        new() { Key = "aspect",    Type = ParamType.String },   // { square/landscape/portrait: [w,h] } dims map
        new() { Key = "required_prefix",     Type = ParamType.String },
        new() { Key = "negative_supported",  Type = ParamType.Bool, Default = true },
        // Optional LoRA on the base model — lets a config be a "base + LoRA" txt2img variant (e.g. a Z-Image LoRA).
        new() { Key = "lora",          Type = ParamType.String, IsModelRef = true },
        new() { Key = "lora_strength", Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 2.0, Label = "LoRA strength" },
    };

    public virtual Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var file = req.Checkpoint;
        var loader = p.Str("loader") ?? "checkpoint";
        int sw = p.Int("width", 1024), sh = p.Int("height", 1024);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);

        var wf = new Dictionary<string, object>();
        object modelSrc, clipSrc, vaeSrc;

        if (loader == "checkpoint")
        {
            wf["4"] = ComfyGraph.Node("CheckpointLoaderSimple", new { ckpt_name = file });
            modelSrc = ComfyGraph.Ref("4", 0); clipSrc = ComfyGraph.Ref("4", 1); vaeSrc = ComfyGraph.Ref("4", 2);
        }
        else
        {
            wf["4"] = ComfyGraph.DiffusionLoader(file);
            modelSrc = ComfyGraph.Ref("4", 0);
            var enc = req.TextEncoders;
            var clipType = p.Str("clip_type");
            wf["20"] = p.Bool("dual")
                ? ComfyGraph.Node("DualCLIPLoader", new { clip_name1 = enc.ElementAtOrDefault(0) ?? "", clip_name2 = enc.ElementAtOrDefault(1) ?? "", type = clipType, device = "default" })
                : ComfyGraph.Node("CLIPLoader", new { clip_name = enc.ElementAtOrDefault(0) ?? "", type = clipType, device = "default" });
            clipSrc = ComfyGraph.Ref("20", 0);
            wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
            vaeSrc = ComfyGraph.Ref("21", 0);
        }

        modelSrc = ComfyGraph.ApplyLora(wf, modelSrc, p);   // optional LoRA on the base model

        // clip-skip applies only to a checkpoint's baked CLIP (SD/SDXL)
        int clipSkip = p.Int("clip_skip");
        if (clipSkip > 0 && loader == "checkpoint")
        {
            wf["10"] = ComfyGraph.Node("CLIPSetLastLayer", new { clip = clipSrc, stop_at_clip_layer = -Math.Abs(clipSkip) });
            clipSrc = ComfyGraph.Ref("10", 0);
        }
        if (p.DblOrNull("auraflow") is double shift)
        {
            wf["11"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = modelSrc, shift });
            modelSrc = ComfyGraph.Ref("11", 0);
        }

        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clipSrc });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip = clipSrc });
        object posSrc = ComfyGraph.Ref("6", 0);
        if (p.DblOrNull("guidance") is double g)
        {
            wf["12"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("6", 0), guidance = g });
            posSrc = ComfyGraph.Ref("12", 0);
        }
        posSrc = PostEncodePositive(wf, posSrc, p);   // model-specific positive-conditioning transform (default: identity)

        var latent = p.Str("latent") ?? "std";
        var latentClass = latent == "sd3" ? "EmptySD3LatentImage"
                        : latent == "flux2" ? "EmptyFlux2LatentImage"
                        : latent == "pixel" ? "EmptyChromaRadianceLatentImage" : "EmptyLatentImage";
        wf["5"] = ComfyGraph.Node(latentClass, new { width = w, height = h, batch_size = 1 });
        modelSrc = PatchDenoiseModel(wf, modelSrc, vaeSrc, p);   // model-patch hook before the sampler (default: identity)
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.Int("steps", 25),
            cfg = p.Dbl("cfg", 7),
            sampler_name = ComfyGraph.MapSampler(p.Str("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")),
            denoise = 1.0,
            model = modelSrc,
            positive = posSrc,
            negative = ComfyGraph.Ref("7", 0),
            latent_image = ComfyGraph.Ref("5", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vaeSrc });
        // post-decode hook before save (default: identity) — a pixelizer inserts a final PixelQuantize here.
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = PostDecodeImage(wf, ComfyGraph.Ref("8", 0), p), filename_prefix = "forgemcp" });
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
