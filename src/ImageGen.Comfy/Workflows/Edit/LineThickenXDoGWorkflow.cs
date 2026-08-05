using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Model-free OUTLINE-ONLY thickener — XDoG line extraction → thicken the extracted lines → multiply
/// them back over the original. Unlike the plain erode (which darkens every dark pixel), this touches
/// only the edges: <c>XDoGLines</c> (ComfyUI-PixelHarness) pulls the existing outlines out as
/// dark-lines-on-white, <c>LineThicken</c> boldens that line layer, and a multiply <c>ImageBlend</c>
/// composites it over the source so flat-colour interiors stay clean. No model, no VRAM. API-only.
/// </summary>
public sealed class LineThickenXDoGWorkflow : EditWorkflow<LineThickenXDoGParams>
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

    protected override ComfyWorkflowGraph Build(LineThickenXDoGParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [EditNodes.Source] = new LoadImage { Image = source },
        };
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);   // flatten alpha onto white (nodes 11-14)
        // Extract the existing outlines as dark-lines-on-white...
        g[Nodes.Lineart] = new XDoGLines
        {
            Image = src,
            Sigma = p.Sigma,
            K = p.K,
            Tau = p.Tau,
            Epsilon = p.Epsilon,
            Phi = p.Phi,
        };
        // ...bolden that line layer...
        g[Nodes.Thicken] = new LineThicken { Image = XDoGLines.Out(Nodes.Lineart), Thickness = p.Thickness };
        // ...and multiply it back over the source so only the outlines darken (flat regions = white = unchanged).
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

/// <summary>The XDoG outline thickener's parameters. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c>/<c>DblReq</c> reads).</summary>
public sealed record LineThickenXDoGParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)]                                  public required int Thickness { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sigma)]
    [Range(0.3, 8.0)]                               public required double Sigma { get; init; }
    [JsonPropertyName(WorkflowParamKeys.K)]
    [Range(1.0, 4.0)]                               public required double K { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Tau)]
    [Range(0.5, 1.0)]                               public required double Tau { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Epsilon)]
    [Range(-1.0, 1.0)]                              public required double Epsilon { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Phi)]
    [Range(0.1, 50.0)]                              public required double Phi { get; init; }
}
