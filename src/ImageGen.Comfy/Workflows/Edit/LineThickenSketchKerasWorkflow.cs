//TODO: CHECK FOR FALLBACKS
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
        new() { Key = "thickness", Type = ParamType.Int,    Default = 2,   Min = 0,   Max = 32,  Label = "Line thickness (px)" },
        new() { Key = "threshold", Type = ParamType.Double, Default = 0.1, Min = 0.0, Max = 1.0, Label = "Sketch threshold" },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? "" }),
        };
        var src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        // Extract lines as dark-on-white (already at input size), bolden, multiply over the source.
        wf["20"] = ComfyGraph.Node("SketchKerasLines", new { image = src, threshold = p.Dbl("threshold", 0.1) });
        wf["21"] = ComfyGraph.Node("LineThicken", new { image = ComfyGraph.Ref("20", 0), thickness = p.Int("thickness", 2) });
        wf["22"] = ComfyGraph.Node("ImageBlend", new
        {
            image1 = src,
            image2 = ComfyGraph.Ref("21", 0),
            blend_factor = 1.0,
            blend_mode = "multiply",
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("22", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
