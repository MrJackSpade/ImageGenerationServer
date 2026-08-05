using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Step1X-Edit (original i1258) instruction editor, via the raykindle ComfyUI nodes. A self-contained loader
/// (<c>Step1XEditModelLoader</c>: DiT fp8 + Flux AE + a full Qwen2.5-VL-7B HF folder, int8-quantized + offloaded)
/// feeds <c>Step1XEditGenerate</c>. The node's flash-attn requirement is patched to fall back to PyTorch SDPA
/// (flash-attn won't build on this box), so no flash-attn is needed. Files resolve from the E category dirs
/// (diffusion_models → Stable-diffusion, vae → VAE, text_encoders → text_encoder). No ComfyUI model loaders here,
/// hence <see cref="RequiresModel"/> = false. NOTE: superseded by Step1X-Edit v1p2; this is the older release.
/// </summary>
public sealed class Step1XEditWorkflow : EditWorkflowBase
{
    public override string Name => "step1x-edit-i1258";
    /// <summary>Self-contained loader node (manages its own VRAM: int8 + offload) — no ComfyUI loaders to presence-gate.</summary>
    public override bool RequiresModel => false;

    /// <summary>The DiT and AE are slot ids on the configuration, resolved to this machine's bound files — a const
    /// filename here would bake one person's disk into the application, unreachable from the models page. The text
    /// encoder stays a literal: it is not a file but the name of a Hugging Face folder the node loads from its own
    /// directory, so there is nothing to bind.</summary>
    private const string TextEncoder = "Qwen2.5-VL-7B-Instruct";

    /// <summary>Step1X-Edit's own node ids (source LoadImage reuses the inherited <c>Nodes.Source</c>).</summary>
    private const string ModelLoader = "1";
    private const string Generate = "2";
    private const string Save = "9";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.DiffusionModel, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.Step1xVae,      Type = ParamType.String, IsModelRef = true },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("Step1X-Edit needs a source image, but none was provided.") }),
            [ModelLoader] = ComfyGraph.Node(ComfyNodeTypes.Step1XEditModelLoader, new
            {
                diffusion_model = p.Model(WorkflowParamKeys.DiffusionModel),
                vae = p.Model(WorkflowParamKeys.Step1xVae),
                text_encoder = TextEncoder,
                dtype = "bfloat16",
                quantized = true,
                offload = true,
            }),
        };
        wf[Generate] = ComfyGraph.Node(ComfyNodeTypes.Step1XEditGenerate, new
        {
            model = ComfyGraph.Ref(ModelLoader, 0),
            input_image = ComfyGraph.Ref(Nodes.Source, 0),
            prompt = inputs.Positive,
            negative_prompt = "",
            num_steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg_guidance = p.DblReq(WorkflowParamKeys.Cfg),
            seed = ComfyGraph.Seed(p),
            size_level = p.IntReq(WorkflowParamKeys.Width),
        });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Generate, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
