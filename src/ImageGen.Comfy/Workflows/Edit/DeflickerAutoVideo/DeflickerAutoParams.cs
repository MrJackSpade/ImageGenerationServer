using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.DeflickerAutoVideo;

/// <summary>The deflicker pass's parameters, deserialized from the merged bag before <c>Build</c> — all four the robust
/// flag/correct thresholds. <c>required</c> so an absent value throws at the deserializer (the declarative form of the
/// previous <c>DblReq</c> reads).</summary>
public sealed record DeflickerAutoParams
{
    [JsonPropertyName(WorkflowParamKeys.MadK)]
    [Range(0.5, 20.0)] public required double MadK { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MinDev)]
    [Range(0.0, 16.0)] public required double MinDev { get; init; }
    [JsonPropertyName(WorkflowParamKeys.AlphaCut)]
    [Range(0.0, 1.0)] public required double AlphaCut { get; init; }
    [JsonPropertyName(WorkflowParamKeys.TimeSigma)]
    [Range(0.1, 32.0)] public required double TimeSigma { get; init; }
}
