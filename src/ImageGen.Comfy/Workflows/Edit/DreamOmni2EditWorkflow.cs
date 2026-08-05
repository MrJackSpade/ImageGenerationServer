using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// DreamOmni2 reference-based instruction editor, via the HM-RunningHub ComfyUI nodes (no llama.cpp — the VLM runs
/// through HF Transformers). The node is a self-contained pipeline (FLUX.1-Kontext base int8-quantized via
/// optimum-quanto + model-cpu-offload, plus a Qwen2.5-VL VLM that rewrites the instruction), so this graph just
/// loads the source + a reference image and drives <c>RH_DreamOmni2_Edit_Pipeline</c> → <c>RH_DreamOmni2_Editor</c>.
/// All weights are loaded internally from E:\AI\models (the node's paths point there); no ComfyUI loader
/// nodes, hence <see cref="RequiresModel"/> = false. The Editor REQUIRES a reference image — if the user attaches
/// none, the source doubles as its own reference.
/// </summary>
public sealed class DreamOmni2EditWorkflow : EditWorkflow<DreamOmni2Params>
{
    public override string Name => "dreamomni2-edit";
    /// <summary>Self-contained pipeline node (int8 + cpu offload internally) — no ComfyUI model loaders to presence-gate.</summary>
    public override bool RequiresModel => false;

    /// <summary>This subclass's own node ids (the source LoadImage reuses <c>Nodes.Source</c>).</summary>
    private const string Reference = "11";
    private const string Pipeline = "1";
    private const string Editor = "2";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(DreamOmni2Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("DreamOmni2 edit needs a source image, but none was provided.") },
        };
        // The Editor requires a reference image; use the first attached reference, else the source itself.
        Output<Slot.Image> refImg;
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
        if (refNames.Count > 0) { g[Reference] = new LoadImage { Image = refNames[0] }; refImg = LoadImage.ImageOut(Reference); }
        else refImg = LoadImage.ImageOut(Nodes.Source);

        g[Pipeline] = new RunningHubDreamOmni2EditPipeline();
        g[Editor] = new RunningHubDreamOmni2Editor
        {
            Pipeline = RunningHubDreamOmni2EditPipeline.Out(Pipeline),
            SrcImage = LoadImage.ImageOut(Nodes.Source),
            RefImage = refImg,
            Prompt = inputs.Positive,
            NumInferenceSteps = p.Steps,
            GuidanceScale = p.Cfg,
            Seed = ComfyGraph.Seed(p.Seed),
        };
        g[Save] = new SaveImage { Images = RunningHubDreamOmni2Editor.Out(Editor), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>DreamOmni2 parameters — the two diffusion knobs read by the pipeline (<c>*Req</c> reads → <c>required</c>)
/// plus the app's single-sourced seed (defaulted; folded through <see cref="ComfyGraph.Seed(long)"/> in Build).</summary>
public sealed record DreamOmni2Params
{
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]     public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]  public long Seed { get; init; }
}
