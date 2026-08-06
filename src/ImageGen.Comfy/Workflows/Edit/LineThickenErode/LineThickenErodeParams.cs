using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenErode;

/// <summary>The erode thickener's one parameter. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c> read).</summary>
public sealed record LineThickenErodeParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)] public required int Thickness { get; init; }
}
