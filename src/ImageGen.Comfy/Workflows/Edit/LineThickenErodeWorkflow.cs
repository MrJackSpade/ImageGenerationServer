using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

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

    private static readonly IReadOnlyList<ParamSpec> ErodeSchema = new ParamSpec[]
    {
        // Growth radius in pixels = iterations of a 3x3 minimum filter. 1 ≈ +1px lines.
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int, Min = 0, Max = 32, Label = "Line thickness (px)" },
    };

    /// <summary>This workflow's own node ids.</summary>
    private const string Thicken = "20";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(LineThickenErodeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage { Image = source },
        };
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);   // flatten alpha onto white (nodes 11-14)
        g[Thicken] = new LineThicken { Image = src, Thickness = p.Thickness };
        g[Save] = new SaveImage { Images = LineThicken.Out(Thicken), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>The erode thickener's one parameter. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c> read).</summary>
public sealed record LineThickenErodeParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)]                                  public required int Thickness { get; init; }
}
