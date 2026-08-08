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
    /// <summary>Self-contained pipeline node (int8 + cpu offload internally) — no ComfyUI model loaders to presence-gate.
    /// The one thing it DOES take is which diffusers base drives it (<see cref="WorkflowParamKeys.BaseModel"/>), a
    /// model-ref slot the picker binds, so <see cref="RequiresModel"/> stays false while the base is still assignable.</summary>
    public override bool RequiresModel => false;

    /// <summary>The shared edit menu plus the assignable diffusers base (FLUX.1-Kontext family). The base is a model-ref
    /// slot — resolved to a filename in <c>Build</c> and gated/bound like every other edit/gen workflow's model.</summary>
    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema =
    [
        new() { Key = WorkflowParamKeys.BaseModel, Type = ParamType.String, IsModelRef = true },
        .. EditWorkflowBase.SharedSchema,
    ];

    protected override ComfyWorkflowGraph Build(DreamOmni2Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("DreamOmni2 edit needs a source image, but none was provided.") },
        };
        // The Editor wires exactly one reference image (its single ref_image slot). Refuse an over-supply rather than
        // silently dropping the extras, so a config that widens the ＋ ref affordance past what this graph consumes
        // surfaces at submit instead of losing the user's uploads without a word.
        IReadOnlyList<string> refNames = inputs.ImageReferences;
        if (refNames.Count > 1)
        {
            throw new RenderValidationException($"DreamOmni2 edit accepts at most 1 reference image; got {refNames.Count}.");
        }

        // The Editor requires a reference image; use the attached reference, else the source itself.
        Output<Slot.Image> refImg;
        if (refNames.Count > 0)
        {
            g[Nodes.Reference] = new LoadImage { Image = refNames[0] };
            refImg = LoadImage.ImageOut(Nodes.Reference);
        }
        else
        {
            refImg = LoadImage.ImageOut(EditNodes.Source);
        }

        g[Nodes.Pipeline] = new RunningHubDreamOmni2EditPipeline { BaseModel = p.BaseModel };
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
