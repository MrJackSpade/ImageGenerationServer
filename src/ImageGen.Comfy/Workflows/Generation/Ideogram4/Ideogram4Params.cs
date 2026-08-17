using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Ideogram 4's correction strength, late-step CFG override, and logit-normal schedule.</summary>
public sealed record Ideogram4Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.DebannerStrength)]
    [Range(0.0, 2.0)] public required double DebannerStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgOverride)]
    [Range(1.0, 30.0)] public required double CfgOverride { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Mu)]
    [Range(-10.0, 10.0)] public required double Mu { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Std)]
    [Range(0.1, 5.0)] public required double Std { get; init; }
}
