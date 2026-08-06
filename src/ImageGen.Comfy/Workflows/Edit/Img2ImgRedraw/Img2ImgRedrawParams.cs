using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Img2ImgRedraw;

/// <summary>Img2img-redraw parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the sampler settings and the redraw <c>denoise</c> strength (all <c>required</c>),
/// and the optional per-model knobs: <c>required_prefix</c>/<c>negative</c> (nullable strings), <c>clip_skip</c>/
/// <c>native_pixels</c> (Has-guarded nullable ints), and <c>guidance</c>/<c>shift</c> (nullable doubles — the node they
/// drive is emitted only when set). <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record Img2ImgRedrawParams
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
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)] public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)]
    [AllowNullable("null = the config didn't set clip skip; the CLIPSetLastLayer node is emitted only when set, distinct from a real 0")] public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]
    [AllowNullable("null = the config declares no distilled guidance; the FluxGuidance node is emitted only when set, distinct from a real 0")] public double? Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]
    [AllowNullable("null = the config declares no flow shift; the ModelSamplingAuraFlow node is emitted only when set, distinct from a real 0")] public double? Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.NativePixels)]
    [AllowNullable("null = the config declares no native pixel budget (source sampled at its own resolution); distinct from a real 0")] public int? NativePixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
