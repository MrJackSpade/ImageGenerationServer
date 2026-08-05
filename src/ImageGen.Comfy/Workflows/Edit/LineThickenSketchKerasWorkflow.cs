using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// sketchKeras line extractor → thicken → composite. The <c>SketchKerasLines</c> node
/// (ComfyUI-PixelHarness, weights loaded from disk) extracts the source's lines as dark-on-white at
/// the input size; the lines are boldened with <c>LineThicken</c> and multiplied over the source so
/// only the extracted lines darken. No diffusion checkpoint.
/// </summary>
public sealed class LineThickenSketchKerasWorkflow : EditWorkflowBase
{
    public override string Name => "line-thicken-sketchkeras";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => SketchSchema;

    private static readonly IReadOnlyList<ParamSpec> SketchSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int,    Min = 0,   Max = 32,  Label = "Line thickness (px)" },
        new() { Key = WorkflowParamKeys.Threshold, Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Sketch threshold" },
    };

    /// <summary>This workflow's own node ids.</summary>
    private const string Lineart = "20";
    private const string Thicken = "21";
    private const string Blend = "22";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.") }),
        };
        object src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        // Extract lines as dark-on-white (already at input size), bolden, multiply over the source.
        wf[Lineart] = ComfyGraph.Node(ComfyNodeTypes.SketchKerasLines, new { image = src, threshold = p.DblReq(WorkflowParamKeys.Threshold) });
        wf[Thicken] = ComfyGraph.Node(ComfyNodeTypes.LineThicken, new { image = ComfyGraph.Ref(Lineart, 0), thickness = p.IntReq(WorkflowParamKeys.Thickness) });
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
