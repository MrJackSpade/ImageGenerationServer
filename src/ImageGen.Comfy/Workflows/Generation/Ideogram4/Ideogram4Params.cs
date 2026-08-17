using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Ideogram 4's extra knobs: its two debanner stages, late-step CFG override, and logit-normal schedule.</summary>
public sealed record Ideogram4Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.DebannerStage1Strength)]
    [Range(0.0, 2.0)] public required double DebannerStage1Strength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.DebannerStage2Strength)]
    [Range(0.0, 3.0)] public required double DebannerStage2Strength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgOverride)]
    [Range(1.0, 30.0)] public required double CfgOverride { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Mu)]
    [Range(-10.0, 10.0)] public required double Mu { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Std)]
    [Range(0.1, 5.0)] public required double Std { get; init; }
}
