using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.MiniMaxH3Ref2V;

/// <summary>MiniMax-H3 reference→video parameters — the same native-audio + sampler knobs as i2v, plus the
/// <c>reference_max</c> cap (nullable: absent → no picker references beyond the source). The audio VAE (resolved model
/// ref), clip <c>length</c>, playback <c>fps</c> and sampler settings are <c>required</c>; <c>lora</c> is a nullable
/// model-ref and <c>lora_strength</c> a defaulted double (only read when a LoRA is set); <c>seed</c> is the app's
/// single-sourced seed (defaulted).</summary>
public sealed record MiniMaxH3Ref2VParams
{
    [JsonPropertyName(WorkflowParamKeys.AudioVae)] public required string AudioVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)][AllowNullable("null = the config didn't set reference_max; absent means no picker references beyond the source (treated as 0), distinct from a config that explicitly caps at a real 0")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
