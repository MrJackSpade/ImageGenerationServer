using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>The BiRefNet matte's parameters, deserialized from the merged bag before <c>Build</c> — just the alpha
/// cutoff. Shared by the still (<see cref="BiRefNetMatteWorkflow"/>) and video (<see cref="BiRefNetMatteVideoWorkflow"/>)
/// mattes, which take the same one input. <c>required</c> so an absent value throws at the deserializer (the
/// declarative form of the previous <c>DblReq</c> read).</summary>
public sealed record MatteParams
{
    [JsonPropertyName(WorkflowParamKeys.Threshold)]
    [Range(0.0, 1.0)] public required double Threshold { get; init; }
}
