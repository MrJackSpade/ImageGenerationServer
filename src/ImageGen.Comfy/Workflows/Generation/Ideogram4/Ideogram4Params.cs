using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Ideogram 4's extra knobs: the late-step CFG override, and the mu/std of its own logit-normal schedule.</summary>
public sealed record Ideogram4Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.CfgOverride)]
    [Range(1.0, 30.0)] public required double CfgOverride { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Mu)]
    [Range(-10.0, 10.0)] public required double Mu { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Std)]
    [Range(0.1, 5.0)] public required double Std { get; init; }
}
