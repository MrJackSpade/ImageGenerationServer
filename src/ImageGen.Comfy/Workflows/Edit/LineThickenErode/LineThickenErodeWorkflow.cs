using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.LineThickenErode;

/// <summary>
/// Model-free line thickener — grayscale morphological erosion (the min filter). Grows the dark
/// lines by <c>thickness</c> pixels via the <c>LineThicken</c> ComfyUI node (ComfyUI-PixelHarness):
/// LoadImage → flatten-on-white → erode → save. No model, no VRAM. This is the cv2.erode /
/// ImageMagick <c>-morphology Erode</c> / Photoshop "Minimum" algorithm. Grows every dark pixel,
/// interior detail included. Exempt from the no-change gate (it restyles in place).
/// </summary>
public sealed class LineThickenErodeWorkflow : EditWorkflow<LineThickenErodeParams>
{
    public override string Name => "line-thicken-erode";
    /// <summary>Restyle in place — exempt from the whole-image no-change gate.</summary>
    public override bool PreservesComposition => true;
    /// <summary>Pure CPU op — no checkpoint, must not be hidden by the no-model guard.</summary>
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => ErodeSchema;

    private static readonly IReadOnlyList<ParamSpec> ErodeSchema =
    [
        // Growth radius in pixels = iterations of a 3x3 minimum filter. 1 ≈ +1px lines.
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int, Min = 0, Max = 32, Label = "Line thickness (px)" },
    ];

    protected override ComfyWorkflowGraph Build(LineThickenErodeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new()
        {
            [EditNodes.Source] = new LoadImage { Image = source },
        };
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);   // flatten alpha onto white (nodes 11-14)
        g[Nodes.Thicken] = new LineThicken { Image = src, Thickness = p.Thickness };
        g[Nodes.Save] = new SaveImage { Images = LineThicken.Out(Nodes.Thicken), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}