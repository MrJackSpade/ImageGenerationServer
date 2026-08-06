using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.LineThickenXDoG;

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
