using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenSketchKeras;

/// <summary>The sketchKeras thickener's parameters. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c>/<c>DblReq</c> reads).</summary>
public sealed record LineThickenSketchKerasParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)] public required int Thickness { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Threshold)]
    [Range(0.0, 1.0)] public required double Threshold { get; init; }
}
