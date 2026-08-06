using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.ChronoEdit;

/// <summary>ChronoEdit-14B parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the sampler settings, the trajectory <c>length</c>, and the i2v <c>clip_vision</c>
/// tower. The <c>*Req</c> reads are <c>required</c>; <c>clip_vision</c> is a required model-ref (resolved to a filename
/// in the bag); <c>weight_dtype</c>/<c>clip_type</c> are nullable strings; <c>lora</c> is a nullable model-ref and
/// <c>lora_strength</c> a defaulted double (only read when a LoRA is set); <c>seed</c> is the app's single-sourced
/// seed (defaulted).</summary>
public sealed record ChronoEditParams
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
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipVision)] public required string ClipVision { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
