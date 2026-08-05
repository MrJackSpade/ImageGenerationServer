using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Model-free line thickener — grayscale morphological erosion (the min filter). Grows the dark
/// lines by <c>thickness</c> pixels via the <c>LineThicken</c> ComfyUI node (ComfyUI-PixelHarness):
/// LoadImage → flatten-on-white → erode → save. No model, no VRAM. This is the cv2.erode /
/// ImageMagick <c>-morphology Erode</c> / Photoshop "Minimum" algorithm. Grows every dark pixel,
/// interior detail included. Exempt from the no-change gate (it restyles in place).
/// </summary>
public sealed class LineThickenErodeWorkflow : EditWorkflowBase
{
    public override string Name => "line-thicken-erode";
    /// <summary>Restyle in place — exempt from the whole-image no-change gate.</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure CPU op — no checkpoint, must not be hidden by the no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => ErodeSchema;

    private static readonly IReadOnlyList<ParamSpec> ErodeSchema = new ParamSpec[]
    {
        // Growth radius in pixels = iterations of a 3x3 minimum filter. 1 ≈ +1px lines.
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int, Min = 0, Max = 32, Label = "Line thickness (px)" },
    };

    /// <summary>This workflow's own node ids.</summary>
    private const string Thicken = "20";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.") }),
        };
        object src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        wf[Thicken] = ComfyGraph.Node(ComfyNodeTypes.LineThicken, new { image = src, thickness = p.IntReq(WorkflowParamKeys.Thickness) });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Thicken, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
