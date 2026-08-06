using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.DreamOmni2Edit;

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

    protected override ComfyWorkflowGraph Build(DreamOmni2Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("DreamOmni2 edit needs a source image, but none was provided.") },
        };
        // The Editor requires a reference image; use the first attached reference, else the source itself.
        Output<Slot.Image> refImg;
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
        if (refNames.Count > 0)
        {
            g[Nodes.Reference] = new LoadImage { Image = refNames[0] };
            refImg = LoadImage.ImageOut(Nodes.Reference);
        }
        else
        {
            refImg = LoadImage.ImageOut(EditNodes.Source);
        }

        g[Nodes.Pipeline] = new RunningHubDreamOmni2EditPipeline();
        g[Nodes.Editor] = new RunningHubDreamOmni2Editor
        {
            Pipeline = RunningHubDreamOmni2EditPipeline.Out(Nodes.Pipeline),
            SrcImage = LoadImage.ImageOut(EditNodes.Source),
            RefImage = refImg,
            Prompt = inputs.Positive,
            NumInferenceSteps = p.Steps,
            GuidanceScale = p.Cfg,
            Seed = ComfyGraph.Seed(p.Seed),
        };
        g[Nodes.Save] = new SaveImage { Images = RunningHubDreamOmni2Editor.Out(Nodes.Editor), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
