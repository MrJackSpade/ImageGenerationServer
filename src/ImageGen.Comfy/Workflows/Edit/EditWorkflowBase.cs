using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Base for the image-EDIT workflows. Each edit MODEL has its own subclass with its own self-contained graph. The
/// only thing shared here is the common head every edit graph emits — loading the model/CLIP/VAE and the source
/// image — plus the parameter menu.
/// </summary>
public abstract class EditWorkflowBase : IWorkflow
{
    public abstract string Name { get; }
    public WorkflowKind Kind => WorkflowKind.Edit;
    public virtual WorkflowMedia Media => WorkflowMedia.Image;
    public virtual bool PromptDirectsMotion => true;
    public virtual bool SupportsEndFrame => false;
    public virtual bool HasAudio => false;
    public virtual bool PreservesComposition => false;
    public virtual PromptSemantics PromptSemantics => PromptSemantics.Instruction;
    public virtual bool RequiresModel => true;
    public virtual bool TakesPrompt => true;
    public virtual FrameRule? FrameRule => null;
    public virtual ModelResolution? ResolutionEnvelope => null;
    public virtual IReadOnlyList<ParamSpec> Schema => SharedSchema;

    /// <summary>Each edit model implements its own self-contained graph.</summary>
    public abstract Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs);

    protected static readonly IReadOnlyList<ParamSpec> SharedSchema = new ParamSpec[]
    {
        new() { Key = "loader",    Type = ParamType.Enum,   Choices = new[] { "checkpoint", "unet", "unet_gguf" } },
        // UNETLoader cast-at-load. "default" keeps the file's own dtype; fp8_e4m3fn halves a bf16's VRAM so a 12B
        // model fits a 24GB card alongside its text encoder instead of swapping against it.
        new() { Key = "weight_dtype", Type = ParamType.String },
        new() { Key = "clip_type", Type = ParamType.String },
        new() { Key = "dual",      Type = ParamType.Bool },
        new() { Key = "steps",     Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps", EtaVariable = true },
        new() { Key = "cfg",       Type = ParamType.Double, Min = 1, Max = 30,  Label = "CFG scale" },
        new() { Key = "guidance",  Type = ParamType.Double },
        new() { Key = "sampler",   Type = ParamType.String },
        new() { Key = "scheduler", Type = ParamType.String },
        // Video shapes (wan/animatediff/ltxv): frame-size budget, clip length (frames), playback fps. 0 = builder default.
        new() { Key = "width",     Type = ParamType.Int },
        new() { Key = "height",    Type = ParamType.Int },
        new() { Key = "length",    Type = ParamType.Int,    Label = "Frames", EtaVariable = true },
        new() { Key = "fps",       Type = ParamType.Double },
        new() { Key = "motion_model", Type = ParamType.String, IsModelRef = true },
        // SD1.5 AnimateDiff's SparseCtrl-RGB adapter — a slot id resolved to a bound file, exactly like
        // motion_model. Without IsModelRef the raw slot id reaches ACN_SparseCtrlLoaderAdvanced and ComfyUI
        // rejects it (value_not_in_list), so animatediff-sd15 cannot render.
        new() { Key = "sparsectrl_name", Type = ParamType.String, IsModelRef = true },
        // The i2v vision encoder (CLIP-ViT-H for Wan/ChronoEdit, SigCLIP for HunyuanVideo 1.5). A slot id like every
        // other model reference, not a private const filename — a hardcoded filename would be one machine's disk
        // written into the application and unreachable from the models page.
        new() { Key = "clip_vision", Type = ParamType.String, IsModelRef = true },
        // SDXL AnimateDiff img2img: how far frames drift from the source. Low = stays put (little motion); high =
        // more motion but loses the source. Exposed for tuning the motion/fidelity tradeoff.
        new() { Key = "denoise",   Type = ParamType.Double, Min = 0.1, Max = 1.0, Label = "Denoise (source ↔ motion)" },
        // AnimateDiff only (ADE_UseEvolvedSampling). The WRONG schedule is what turns these into color-smear/no-motion
        // garbage, so it's a per-module setting, not an artistic one — exposed for iterative testing, to be locked
        // down once dialed in. No schema default: each AnimateDiff workflow falls back to its module's correct value.
        new() { Key = "beta_schedule", Type = ParamType.Enum, Label = "AnimateDiff schedule",
                Choices = new[] { "autoselect", "use existing", "sqrt_linear (AnimateDiff)", "linear (AnimateDiff-SDXL)",
                                  "linear (HotshotXL/default)", "avg(sqrt_linear,linear)", "lcm avg(sqrt_linear,linear)",
                                  "lcm", "lcm[100_ots]", "lcm >> sqrt_linear", "sqrt", "cosine", "squaredcos_cap_v2" } },
        // Reference images: how many extra images this editor accepts, and (Qwen) the encode-node slot names.
        new() { Key = "reference_max",    Type = ParamType.Int },
        new() { Key = "reference_inputs", Type = ParamType.String },   // ["image2","image3"]
        // Optional style/quality LoRA applied on top of the base model — lets a config be a "base + anime LoRA"
        // variant (e.g. WAN i2v + Flat Color) with no new graph code.
        new() { Key = "lora",          Type = ParamType.String, IsModelRef = true },
        new() { Key = "lora_strength", Type = ParamType.Double, Min = 0.0, Max = 1.5, Label = "LoRA strength" },
    };

    /// <summary>Emit the common edit head: the model/CLIP/VAE loaders (from the loader param + resolved
    /// requirements, mirroring the txt2img loader block, with a GGUF text-encoder going through CLIPLoaderGGUF) and
    /// the source <c>LoadImage</c> at node "10". Returns the model/clip/vae output refs.</summary>
    protected static void LoadModel(Dictionary<string, object> wf, ParamValues p, ResolvedRequirements req, WorkflowInputs inputs,
        out object model0, out object clip0, out object vae0)
    {
        var file = req.RequiredCheckpoint();
        var loader = p.StrReq("loader");
        if (loader == "checkpoint")                          // all-in-one checkpoint (model+clip+vae), e.g. Qwen AIO
        {
            wf["4"] = ComfyGraph.Node("CheckpointLoaderSimple", new { ckpt_name = file });
            model0 = ComfyGraph.Ref("4", 0); vae0 = ComfyGraph.Ref("4", 2);

            // A checkpoint's CLIP output is only usable when the checkpoint actually carries encoders. Several do
            // not — sd3.5_large ships without them — and taking output 1 regardless would hand CLIPTextEncode a null,
            // surfacing as "clip input is invalid: None" far from the real mistake. Declared encoders win.
            clip0 = req.TextEncoders.Count > 0
                ? BuildClipLoader(wf, "5", req.TextEncoders, p.Str("clip_type"))
                : ComfyGraph.Ref("4", 1);
        }
        else                                                 // split loaders (unet/gguf + clip + vae)
        {
            wf["4"] = p.Has("weight_dtype")
                ? ComfyGraph.DiffusionLoader(file, p.StrReq("weight_dtype"))   // config's explicit precision override (e.g. flux1-fill fp8)
                : ComfyGraph.DiffusionLoader(file);                            // no override → AutoWeightDtype
            wf["6"] = ComfyGraph.Node("VAELoader", new { vae_name = req.RequiredVae() });
            model0 = ComfyGraph.Ref("4", 0); vae0 = ComfyGraph.Ref("6", 0);
            clip0 = BuildClipLoader(wf, "5", req.TextEncoders, p.Str("clip_type"));
        }
        wf["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("This edit needs a source image, but none was provided.") });
    }

    /// <summary>
    /// The CLIP loader a model's encoders call for, chosen by HOW MANY it declares.
    ///
    /// <para>A <c>dual</c> boolean could express one encoder or two and nothing else, leaving a configuration that
    /// needs three or four no way to say so. The count is already in the requirements, so it decides — one
    /// CLIPLoader, two Dual, three Triple, four Quadruple.</para>
    ///
    /// <para>Triple and Quadruple take no <c>type</c>: the encoder set identifies the family on its own.</para>
    /// </summary>
    private static object BuildClipLoader(
        Dictionary<string, object> wf, string nodeId, IReadOnlyList<string> encoders, string? clipType)
    {
        string At(int i) => i < encoders.Count && !string.IsNullOrWhiteSpace(encoders[i])
            ? encoders[i]
            : throw new RenderValidationException($"This configuration needs text encoder #{i + 1} and none is bound to that slot on this machine.");

        wf[nodeId] = encoders.Count switch
        {
            >= 4 => ComfyGraph.Node("QuadrupleCLIPLoader", new
            {
                clip_name1 = At(0), clip_name2 = At(1), clip_name3 = At(2), clip_name4 = At(3),
            }),
            3 => ComfyGraph.Node("TripleCLIPLoader", new
            {
                clip_name1 = At(0), clip_name2 = At(1), clip_name3 = At(2),
            }),
            2 => ComfyGraph.Node("DualCLIPLoader", new
            {
                clip_name1 = At(0), clip_name2 = At(1), type = clipType, device = "default",
            }),
            _ => At(0).EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                ? ComfyGraph.Node("CLIPLoaderGGUF", new { clip_name = At(0), type = clipType })
                : ComfyGraph.Node("CLIPLoader", new { clip_name = At(0), type = clipType, device = "default" }),
        };
        return ComfyGraph.Ref(nodeId, 0);
    }

}
