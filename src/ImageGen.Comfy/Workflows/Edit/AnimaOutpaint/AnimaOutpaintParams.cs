using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.AnimaOutpaint;

/// <summary>Anima LLLite-outpaint parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings + fill <c>denoise</c>, the required seam
/// <c>feather</c> + <c>mask_grow</c>, the LLLite strength/start/end (all <c>required</c>), the optional prefix/negative
/// (nullable strings) + Has-guarded <c>clip_skip</c>, and the Has-guarded per-side <c>pad_*</c> ints. <c>seed</c> is the
/// app's single-sourced seed (defaulted).</summary>
public sealed record AnimaOutpaintParams
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
    [Range(0.0, 1.0)] public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadLeft)]
    [Range(0, 4096)] public int PadLeft { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTop)]
    [Range(0, 4096)] public int PadTop { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRight)]
    [Range(0, 4096)] public int PadRight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottom)]
    [Range(0, 4096)] public int PadBottom { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Feather)]
    [Range(0, 256)] public required int Feather { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)] public required int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LlliteStrength)]
    [Range(0.0, 2.0)] public required double LlliteStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LlliteStart)]
    [Range(0.0, 1.0)] public required double LlliteStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LlliteEnd)]
    [Range(0.0, 1.0)] public required double LlliteEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)] public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)]
    [AllowNullable("null = the config didn't set clip skip; the CLIPSetLastLayer node is emitted only when set, distinct from a real 0")] public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
