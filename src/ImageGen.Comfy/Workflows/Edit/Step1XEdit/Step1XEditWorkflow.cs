using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.Step1XEdit;

/// <summary>
/// Step1X-Edit (original i1258) instruction editor, via the raykindle ComfyUI nodes. A self-contained loader
/// (<c>Step1XEditModelLoader</c>: DiT fp8 + Flux AE + a full Qwen2.5-VL-7B HF folder, int8-quantized + offloaded)
/// feeds <c>Step1XEditGenerate</c>. The node's flash-attn requirement is patched to fall back to PyTorch SDPA
/// (flash-attn won't build on this box), so no flash-attn is needed. Files resolve from the E category dirs
/// (diffusion_models → Stable-diffusion, vae → VAE, text_encoders → text_encoder). No ComfyUI model loaders here,
/// hence <see cref="RequiresModel"/> = false. NOTE: superseded by Step1X-Edit v1p2; this is the older release.
/// </summary>
public sealed class Step1XEditWorkflow : EditWorkflow<Step1XParams>
{
    public override string Name => "step1x-edit-i1258";
    /// <summary>Self-contained loader node (manages its own VRAM: int8 + offload) — no ComfyUI loaders to presence-gate.</summary>
    public override bool RequiresModel => false;

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema =
    [
        .. EditWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.DiffusionModel, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.Step1xVae,      Type = ParamType.String, IsModelRef = true },
    ];

    protected override ComfyWorkflowGraph Build(Step1XParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("Step1X-Edit needs a source image, but none was provided.") },
            [Nodes.ModelLoader] = new Step1XEditModelLoader
            {
                DiffusionModel = p.DiffusionModel,
                Vae = p.Step1xVae,
                TextEncoder = Nodes.TextEncoder,
                Dtype = ComfyWidgets.WeightDtype.BFloat16,
                Quantized = true,
                Offload = true,
            },
        };
        g[Nodes.Generate] = new Step1XEditGenerate
        {
            Model = Step1XEditModelLoader.Out(Nodes.ModelLoader),
            InputImage = LoadImage.ImageOut(EditNodes.Source),
            Prompt = inputs.Positive,
            NegativePrompt = string.Empty,
            NumSteps = p.Steps,
            CfgGuidance = p.Cfg,
            Seed = ComfyGraph.Seed(p.Seed),
            SizeLevel = p.Width,
        };
        g[Nodes.Save] = new SaveImage { Images = Step1XEditGenerate.Out(Nodes.Generate), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}