using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Model-free OUTLINE-ONLY thickener — XDoG line extraction → thicken the extracted lines → multiply
/// them back over the original. Unlike the plain erode (which darkens every dark pixel), this touches
/// only the edges: <c>XDoGLines</c> (ComfyUI-PixelHarness) pulls the existing outlines out as
/// dark-lines-on-white, <c>LineThicken</c> boldens that line layer, and a multiply <c>ImageBlend</c>
/// composites it over the source so flat-colour interiors stay clean. No model, no VRAM. API-only.
/// </summary>
public sealed class LineThickenXDoGWorkflow : EditWorkflowBase
{
    public override string Name => "line-thicken-xdog";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => XDoGSchema;

    private static readonly IReadOnlyList<ParamSpec> XDoGSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int,    Min = 0,    Max = 32,  Label = "Line thickness (px)" },
        new() { Key = WorkflowParamKeys.Sigma,     Type = ParamType.Double, Min = 0.3,  Max = 8.0, Label = "Line scale (sigma)" },
        new() { Key = WorkflowParamKeys.K,         Type = ParamType.Double, Min = 1.0,  Max = 4.0 },
        new() { Key = WorkflowParamKeys.Tau,       Type = ParamType.Double, Min = 0.5,  Max = 1.0 },
        new() { Key = WorkflowParamKeys.Epsilon,   Type = ParamType.Double, Min = -1.0, Max = 1.0, Label = "Edge threshold (0=flats stay clean)" },
        new() { Key = WorkflowParamKeys.Phi,       Type = ParamType.Double, Min = 0.1,  Max = 50.0 },
    };

    /// <summary>This workflow's own node ids.</summary>
    private const string Lineart = "20";
    private const string Thicken = "21";
    private const string Blend = "22";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.") }),
        };
        var src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        // Extract the existing outlines as dark-lines-on-white...
        wf[Lineart] = ComfyGraph.Node(ComfyNodeTypes.XDoGLines, new
        {
            image = src,
            sigma = p.DblReq(WorkflowParamKeys.Sigma),
            k = p.DblReq(WorkflowParamKeys.K),
            tau = p.DblReq(WorkflowParamKeys.Tau),
            epsilon = p.DblReq(WorkflowParamKeys.Epsilon),
            phi = p.DblReq(WorkflowParamKeys.Phi),
        });
        // ...bolden that line layer...
        wf[Thicken] = ComfyGraph.Node(ComfyNodeTypes.LineThicken, new { image = ComfyGraph.Ref(Lineart, 0), thickness = p.IntReq(WorkflowParamKeys.Thickness) });
        // ...and multiply it back over the source so only the outlines darken (flat regions = white = unchanged).
        wf[Blend] = ComfyGraph.Node(ComfyNodeTypes.ImageBlend, new
        {
            image1 = src,
            image2 = ComfyGraph.Ref(Thicken, 0),
            blend_factor = 1.0,
            blend_mode = "multiply",
        });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Blend, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
