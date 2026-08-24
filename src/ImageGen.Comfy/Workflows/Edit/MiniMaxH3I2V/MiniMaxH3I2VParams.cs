using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.MiniMaxH3I2V;

/// <summary>MiniMax-H3 image→video parameters (its own record — the H3 graph emits its own loaders, so none of the
/// shared edit loader-head knobs apply). The audio VAE (resolved model ref), clip <c>length</c>, playback <c>fps</c>,
/// sampler settings are <c>required</c>; <c>lora</c> is a nullable model-ref and <c>lora_strength</c> a defaulted
/// double (only read when a LoRA is set); <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record MiniMaxH3I2VParams
{
    [JsonPropertyName(WorkflowParamKeys.AudioVae)] public required string AudioVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]
    [Range(H3.MinFrames, H3.MaxFrames)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CkAttention)] public bool CkAttention { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PreviewEvery)]
    [Range(0, ParamBounds.StepsMax)] public required int PreviewEvery { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
