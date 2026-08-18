using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Ideogram4Refine;

/// <summary>Ideogram 4 whole-image refine parameters: its dual-model guidance and native schedule controls plus the
/// partial-denoise strength that selects the tail of that schedule.</summary>
public sealed record Ideogram4RefineParams
{
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgOverride)]
    [Range(1.0, 30.0)] public required double CfgOverride { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Mu)]
    [Range(-10.0, 10.0)] public required double Mu { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Std)]
    [Range(0.1, 5.0)] public required double Std { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(ParamBounds.DenoiseMin, ParamBounds.DenoiseMax)] public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.DebannerStrength)]
    [Range(0.0, 2.0)] public required double DebannerStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
