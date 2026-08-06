using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenAnime2Sketch;

/// <summary>The anime2sketch thickener's parameters. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c> reads).</summary>
public sealed record LineThickenAnime2SketchParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)] public required int Thickness { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resolution)]
    [Range(256, 2048)] public required int Resolution { get; init; }
}
