using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.WanA14bI2V;

/// <summary>Wan 2.2 I2V-A14B image→video parameters (its own record — the two MoE experts drive their own loaders, so
/// none of the shared loader-head knobs apply). The <c>unet_low</c> resolved model ref + the sampler/MoE knobs
/// (<c>shift</c>/<c>steps</c>/<c>boundary</c>/<c>cfg_high</c>/<c>cfg_low</c>) are <c>required</c>; the render budget
/// (<c>width</c>/<c>height</c>), clip <c>length</c> and <c>fps</c> are required; <c>refiner_steps</c>, the four
/// <c>pad_*_pct</c> + four <c>end_pad_*_pct</c> percentages, the model's own <c>negative</c> and the <c>seed</c> are
/// optional (an absent pad % is 0 — no pad on that side).</summary>
public sealed record WanA14bI2VParams
{
    [JsonPropertyName(WorkflowParamKeys.UnetLow)] public required string UnetLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)] public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Boundary)] public required int Boundary { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgHigh)] public required double CfgHigh { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgLow)] public required double CfgLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)] public required int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)] public required int Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSteps)]
    [Range(0, 40)][AllowNullable("null = the config didn't set refiner_steps; absent means the legacy shared-schedule tail, distinct from a real 0 (decode the handoff as-is)")] public int? RefinerSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadLeftPct)]
    [Range(0, 2000)][AllowNullable("null = the config didn't set this pad percentage; distinct from a real 0%")] public int? PadLeftPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRightPct)]
    [Range(0, 2000)][AllowNullable("null = the config didn't set this pad percentage; distinct from a real 0%")] public int? PadRightPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTopPct)]
    [Range(0, 2000)][AllowNullable("null = the config didn't set this pad percentage; distinct from a real 0%")] public int? PadTopPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottomPct)]
    [Range(0, 2000)][AllowNullable("null = the config didn't set this pad percentage; distinct from a real 0%")] public int? PadBottomPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadLeftPct)][AllowNullable("null = the config didn't set this end-frame pad percentage; distinct from a real 0%")] public int? EndPadLeftPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadRightPct)][AllowNullable("null = the config didn't set this end-frame pad percentage; distinct from a real 0%")] public int? EndPadRightPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadTopPct)][AllowNullable("null = the config didn't set this end-frame pad percentage; distinct from a real 0%")] public int? EndPadTopPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadBottomPct)][AllowNullable("null = the config didn't set this end-frame pad percentage; distinct from a real 0%")] public int? EndPadBottomPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)] public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
