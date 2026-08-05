using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
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
public sealed class Step1XEditWorkflow : EditWorkflow<Step1XParams>
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
    private static readonly IReadOnlyList<ParamSpec> _schema = EditWorkflowBase.SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.DiffusionModel, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.Step1xVae,      Type = ParamType.String, IsModelRef = true },
    }).ToArray();

    protected override ComfyWorkflowGraph Build(Step1XParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("Step1X-Edit needs a source image, but none was provided.") },
            [ModelLoader] = new Step1XEditModelLoader
            {
                DiffusionModel = p.DiffusionModel,
                Vae = p.Step1xVae,
                TextEncoder = TextEncoder,
                Dtype = ComfyWidgets.WeightDtype.BFloat16,
                Quantized = true,
                Offload = true,
            },
        };
        g[Generate] = new Step1XEditGenerate
        {
            Model = Step1XEditModelLoader.Out(ModelLoader),
            InputImage = LoadImage.ImageOut(Nodes.Source),
            Prompt = inputs.Positive,
            NegativePrompt = string.Empty,
            NumSteps = p.Steps,
            CfgGuidance = p.Cfg,
            Seed = ComfyGraph.Seed(p.Seed),
            SizeLevel = p.Width,
        };
        g[Save] = new SaveImage { Images = Step1XEditGenerate.Out(Generate), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>Step1X-Edit parameters — the DiT/AE model refs (<c>Model()</c> reads → <c>required</c>), the diffusion
/// knobs and the <c>size_level</c> (from <c>width</c>), plus the app's single-sourced seed (defaulted).</summary>
public sealed record Step1XParams
{
    [JsonPropertyName(WorkflowParamKeys.DiffusionModel)] public required string DiffusionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Step1xVae)]      public required string Step1xVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]  public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]      public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)]          public required int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]           public long Seed { get; init; }
}
