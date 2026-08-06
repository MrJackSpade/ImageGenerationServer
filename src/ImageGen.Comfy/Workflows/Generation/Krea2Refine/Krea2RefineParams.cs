using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Krea2Refine;

/// <summary>Krea 2 two-stage refine parameters: the shared Krea 2 knobs (<see cref="Krea2Params"/>) plus the Turbo
/// polish pass — how hard Turbo reworks the base render (<c>polish_denoise</c>) and its own step count / CFG, with an
/// optional sampler/scheduler override (null → reuse the base pass's).</summary>
public sealed record Krea2RefineParams : Krea2Params
{
    [JsonPropertyName(WorkflowParamKeys.PolishDenoise)]
    [Range(0.0, 0.9)] public required double PolishDenoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSteps)]
    [Range(1, 30)] public required int RefinerSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerCfg)]
    [Range(1.0, 4.0)] public required double RefinerCfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSampler)]   public string? RefinerSampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerScheduler)] public string? RefinerScheduler { get; init; }
}
