using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// BiRefNet background-removal matte for a SINGLE image — the still sibling of
/// <see cref="BiRefNetMatteVideoWorkflow"/>, for callers working per-frame (e.g. the sprite pipeline keying an
/// individually re-pixelized frame, where there is no clip to matte). Same <c>BiRefNetMatte</c> node, image IO:
/// <c>LoadImage → BiRefNetMatte → SaveImage</c>; the RGBA output saves as PNG so the alpha survives. No checkpoint
/// (the node loads its own model), so it must not be hidden by the catalog's no-model guard. Composition-preserving.
/// </summary>
public sealed class BiRefNetMatteWorkflow : EditWorkflowBase
{
    public override string Name => "birefnet-matte";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => MatteSchema;

    private static readonly IReadOnlyList<ParamSpec> MatteSchema = new ParamSpec[]
    {
        new() { Key = "threshold", Type = ParamType.Double, Default = 0, Min = 0, Max = 1, Label = "Alpha cutoff", Help = "0 = soft matte (caller thresholds); >0 = hard cutoff at this matte value" },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // No flatten-on-white: the matte wants the source verbatim (mirrors the video matte, which feeds frames as-is).
        return new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? "" }),
            // BiRefNetMatte output 0 = RGBA (frame + matte as alpha); SaveImage writes PNG, which keeps the alpha.
            ["20"] = ComfyGraph.Node("BiRefNetMatte", new { image = ComfyGraph.Ref("10", 0), threshold = p.Dbl("threshold", 0) }),
            ["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("20", 0), filename_prefix = "forgemcp_edit" }),
        };
    }
}
