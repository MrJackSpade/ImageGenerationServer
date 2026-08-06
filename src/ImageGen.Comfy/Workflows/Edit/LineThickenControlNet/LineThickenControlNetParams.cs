using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LineThickenControlNet;

/// <summary>ControlNet lineart re-render parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the lineart preprocessor's coarse/resolution
/// and the ControlNet strength. The <c>*Req</c>-read values are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c>/
/// <c>style_prompt</c>/<c>negative</c> are nullable strings; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record LineThickenControlNetParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(ParamBounds.DenoiseMin, ParamBounds.DenoiseMax)] public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)] public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)] public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Coarse)] public required string Coarse { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ControlnetStrength)]
    [Range(0.0, 2.0)] public required double ControlnetStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resolution)]
    [Range(256, 2048)] public required int Resolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
