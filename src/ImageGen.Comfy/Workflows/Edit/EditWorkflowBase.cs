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

    /// <summary>The node ids of the shared edit head emitted by <see cref="LoadModel"/>, named by role. The VALUE is
    /// the graph-local node key (preserved exactly, so the emitted graph and the id-asserting tests are byte-identical);
    /// the NAME replaces the bare <c>"4"</c>/<c>"5"</c>/<c>"6"</c>/<c>"10"</c> literals. A subclass reuses these for the
    /// head and declares <c>private const string</c>s for its own additional nodes.</summary>
    protected static class Nodes
    {
        public const string Model = "4";
        public const string Clip = "5";
        public const string Vae = "6";
        public const string Source = "10";
    }

    protected static readonly IReadOnlyList<ParamSpec> SharedSchema = new ParamSpec[]
    {
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKinds.Choices },
        // UNETLoader cast-at-load. "default" keeps the file's own dtype; fp8_e4m3fn halves a bf16's VRAM so a 12B
        // model fits a 24GB card alongside its text encoder instead of swapping against it.
        new() { Key = WorkflowParamKeys.WeightDtype, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = 1, Max = 30,  Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        // Video shapes (wan/animatediff/ltxv): frame-size budget, clip length (frames), playback fps. 0 = builder default.
        new() { Key = WorkflowParamKeys.Width,     Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Height,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Length,    Type = ParamType.Int,    Label = "Frames", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Fps,       Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.MotionModel, Type = ParamType.String, IsModelRef = true },
        // SD1.5 AnimateDiff's SparseCtrl-RGB adapter — a slot id resolved to a bound file, exactly like
        // motion_model. Without IsModelRef the raw slot id reaches ACN_SparseCtrlLoaderAdvanced and ComfyUI
        // rejects it (value_not_in_list), so animatediff-sd15 cannot render.
        new() { Key = WorkflowParamKeys.SparsectrlName, Type = ParamType.String, IsModelRef = true },
        // The i2v vision encoder (CLIP-ViT-H for Wan/ChronoEdit, SigCLIP for HunyuanVideo 1.5). A slot id like every
        // other model reference, not a private const filename — a hardcoded filename would be one machine's disk
        // written into the application and unreachable from the models page.
        new() { Key = WorkflowParamKeys.ClipVision, Type = ParamType.String, IsModelRef = true },
        // SDXL AnimateDiff img2img: how far frames drift from the source. Low = stays put (little motion); high =
        // more motion but loses the source. Exposed for tuning the motion/fidelity tradeoff.
        new() { Key = WorkflowParamKeys.Denoise,   Type = ParamType.Double, Min = 0.1, Max = 1.0, Label = "Denoise (source ↔ motion)" },
        // AnimateDiff only (ADE_UseEvolvedSampling). The WRONG schedule is what turns these into color-smear/no-motion
        // garbage, so it's a per-module setting, not an artistic one — exposed for iterative testing, to be locked
        // down once dialed in. No schema default: each AnimateDiff workflow falls back to its module's correct value.
        new() { Key = WorkflowParamKeys.BetaSchedule, Type = ParamType.Enum, Label = "AnimateDiff schedule",
                Choices = new[] { "autoselect", "use existing", "sqrt_linear (AnimateDiff)", "linear (AnimateDiff-SDXL)",
                                  "linear (HotshotXL/default)", "avg(sqrt_linear,linear)", "lcm avg(sqrt_linear,linear)",
                                  "lcm", "lcm[100_ots]", "lcm >> sqrt_linear", "sqrt", "cosine", "squaredcos_cap_v2" } },
        // Reference images: how many extra images this editor accepts, and (Qwen) the encode-node slot names.
        new() { Key = WorkflowParamKeys.ReferenceMax,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.ReferenceInputs, Type = ParamType.String },   // ["image2","image3"]
        // Optional style/quality LoRA applied on top of the base model — lets a config be a "base + anime LoRA"
        // variant (e.g. WAN i2v + Flat Color) with no new graph code.
        new() { Key = WorkflowParamKeys.Lora,          Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.LoraStrength, Type = ParamType.Double, Min = 0.0, Max = 1.5, Label = "LoRA strength" },
    };

    /// <summary>Emit the common edit head: the model/CLIP/VAE loaders (from the loader param + resolved
    /// requirements, mirroring the txt2img loader block, with a GGUF text-encoder going through CLIPLoaderGGUF) and
    /// the source <c>LoadImage</c> at node "10". Returns the model/clip/vae output refs.</summary>
    protected static void LoadModel(Dictionary<string, object> wf, ParamValues p, ResolvedRequirements req, WorkflowInputs inputs,
        out object model0, out object clip0, out object vae0)
    {
        var file = req.RequiredCheckpoint();
        var loader = p.Loader();
        if (loader == LoaderKind.Checkpoint)                          // all-in-one checkpoint (model+clip+vae), e.g. Qwen AIO
        {
            wf[Nodes.Model] = ComfyGraph.Node(ComfyNodeTypes.CheckpointLoaderSimple, new { ckpt_name = file });
            model0 = ComfyGraph.Ref(Nodes.Model, 0); vae0 = ComfyGraph.Ref(Nodes.Model, 2);

            // A checkpoint's CLIP output is only usable when the checkpoint actually carries encoders. Several do
            // not — sd3.5_large ships without them — and taking output 1 regardless would hand CLIPTextEncode a null,
            // surfacing as "clip input is invalid: None" far from the real mistake. Declared encoders win.
            clip0 = req.TextEncoders.Count > 0
                ? BuildClipLoader(wf, Nodes.Clip, req.TextEncoders, p.Str(WorkflowParamKeys.ClipType))
                : ComfyGraph.Ref(Nodes.Model, 1);
        }
        else                                                 // split loaders (unet/gguf + clip + vae)
        {
            wf[Nodes.Model] = p.Has(WorkflowParamKeys.WeightDtype)
                ? ComfyGraph.DiffusionLoader(file, p.StrReq(WorkflowParamKeys.WeightDtype))   // config's explicit precision override (e.g. flux1-fill fp8)
                : ComfyGraph.DiffusionLoader(file);                            // no override → AutoWeightDtype
            wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
            model0 = ComfyGraph.Ref(Nodes.Model, 0); vae0 = ComfyGraph.Ref(Nodes.Vae, 0);
            clip0 = BuildClipLoader(wf, Nodes.Clip, req.TextEncoders, p.Str(WorkflowParamKeys.ClipType));
        }
        wf[Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("This edit needs a source image, but none was provided.") });
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
            >= 4 => ComfyGraph.Node(ComfyNodeTypes.QuadrupleCLIPLoader, new
            {
                clip_name1 = At(0), clip_name2 = At(1), clip_name3 = At(2), clip_name4 = At(3),
            }),
            3 => ComfyGraph.Node(ComfyNodeTypes.TripleCLIPLoader, new
            {
                clip_name1 = At(0), clip_name2 = At(1), clip_name3 = At(2),
            }),
            2 => ComfyGraph.Node(ComfyNodeTypes.DualCLIPLoader, new
            {
                clip_name1 = At(0), clip_name2 = At(1), type = clipType, device = "default",
            }),
            _ => ComfyGraph.IsGguf(At(0))
                ? ComfyGraph.Node(ComfyNodeTypes.CLIPLoaderGGUF, new { clip_name = At(0), type = clipType })
                : ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = At(0), type = clipType, device = "default" }),
        };
        return ComfyGraph.Ref(nodeId, 0);
    }

}
