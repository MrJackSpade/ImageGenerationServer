namespace ImageGen.Comfy;

/// <summary>
/// DreamOmni2 reference-based instruction editor, via the HM-RunningHub ComfyUI nodes (no llama.cpp — the VLM runs
/// through HF Transformers). The node is a self-contained pipeline (FLUX.1-Kontext base int8-quantized via
/// optimum-quanto + model-cpu-offload, plus a Qwen2.5-VL VLM that rewrites the instruction), so this graph just
/// loads the source + a reference image and drives <c>RH_DreamOmni2_Edit_Pipeline</c> → <c>RH_DreamOmni2_Editor</c>.
/// All weights are loaded internally from E:\AI\models (the node's paths were repointed there); no ComfyUI loader
/// nodes, hence <see cref="RequiresModel"/> = false. The Editor REQUIRES a reference image — if the user attaches
/// none, the source doubles as its own reference.
/// </summary>
public sealed class DreamOmni2EditWorkflow : EditWorkflowBase
{
    public override string Name => "dreamomni2-edit";
    /// <summary>Self-contained pipeline node (int8 + cpu offload internally) — no ComfyUI model loaders to presence-gate.</summary>
    public override bool RequiresModel => false;

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? "" }),
        };
        // The Editor requires a reference image; use the first attached reference, else the source itself.
        object refImg;
        var refNames = inputs.ReferenceImageNames;
        if (refNames.Count > 0) { wf["11"] = ComfyGraph.Node("LoadImage", new { image = refNames[0] }); refImg = ComfyGraph.Ref("11", 0); }
        else refImg = ComfyGraph.Ref("10", 0);

        wf["1"] = ComfyGraph.Node("RunningHub DreamOmni2 Edit Pipeline", new { });
        wf["2"] = ComfyGraph.Node("RunningHub DreamOmni2 Editor", new
        {
            pipeline = ComfyGraph.Ref("1", 0),
            src_image = ComfyGraph.Ref("10", 0),
            ref_image = refImg,
            prompt = inputs.Positive,
            num_inference_steps = p.Int("steps", 30),
            guidance_scale = p.Dbl("cfg", 3.5),
            seed = ComfyGraph.Seed(p),
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("2", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
