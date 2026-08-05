using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

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

    private static readonly IReadOnlyList<ParamSpec> SketchSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Thickness, Type = ParamType.Int,    Min = 0,   Max = 32,  Label = "Line thickness (px)" },
        new() { Key = WorkflowParamKeys.Threshold, Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Sketch threshold" },
    };

    protected override ComfyWorkflowGraph Build(LineThickenSketchKerasParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
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

/// <summary>This workflow's own node ids.</summary>
file static class Nodes
{
    public const string Lineart = "20";
    public const string Thicken = "21";
    public const string Blend = "22";
    public const string Save = "9";
}

/// <summary>The sketchKeras thickener's parameters. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c>/<c>DblReq</c> reads).</summary>
public sealed record LineThickenSketchKerasParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)]                                  public required int Thickness { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Threshold)]
    [Range(0.0, 1.0)]                               public required double Threshold { get; init; }
}
