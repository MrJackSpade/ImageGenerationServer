using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.LineThickenSketchKeras;

/// <summary>
/// sketchKeras line extractor → thicken → composite. The <c>SketchKerasLines</c> node
/// (ComfyUI-PixelHarness, weights loaded from disk) extracts the source's lines as dark-on-white at
/// the input size; the lines are boldened with <c>LineThicken</c> and multiplied over the source so
/// only the extracted lines darken. No diffusion checkpoint.
/// </summary>
public sealed class LineThickenSketchKerasWorkflow : EditWorkflow<LineThickenSketchKerasParams>
{
    public override string Name => "line-thicken-sketchkeras";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => SketchSchema;

    private static readonly IReadOnlyList<ParamSpec> SketchSchema =
    [
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int,    Min = 0,   Max = 32,  Label = "Line thickness (px)" },
        new() { Key = WorkflowParamKeys.Threshold, Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Sketch threshold" },
    ];

    protected override ComfyWorkflowGraph Build(LineThickenSketchKerasParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = source },
        };
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);   // flatten alpha onto white (nodes 11-14)
        // Extract lines as dark-on-white (already at input size), bolden, multiply over the source.
        g[Nodes.Lineart] = new SketchKerasLines { Image = src, Threshold = p.Threshold };
        g[Nodes.Thicken] = new LineThicken { Image = SketchKerasLines.Out(Nodes.Lineart), Thickness = p.Thickness };
        g[Nodes.Blend] = new ImageBlend
        {
            Image1 = src,
            Image2 = LineThicken.Out(Nodes.Thicken),
            BlendFactor = 1.0,
            BlendMode = ComfyWidgets.Blend.Multiply,
        };
        g[Nodes.Save] = new SaveImage { Images = ImageBlend.Out(Nodes.Blend), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}