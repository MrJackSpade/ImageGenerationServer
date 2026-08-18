using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.QwenImageEditInpaint;

/// <summary>
/// Parameters for the masked Qwen-Image-Edit editor — the same reference-encode knobs as the plain
/// <c>QwenEditParams</c> (loader head, sampler settings, the reference cap + encode-node slot names, the required
/// per-model reference-latent stitch method) but WITHOUT the canvas-mask reframe percentages (this editor uses a real
/// painted mask instead) and WITH the mask-softening grow/blur. There is no <c>denoise</c>: the fill is a full denoise
/// inside the region, confined by <c>InpaintModelConditioning</c>'s noise mask. The <c>*Req</c> reads are
/// <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings, <c>reference_max</c> is a Has-guarded
/// nullable int, <c>reference_inputs</c> is a nullable string array (empty when absent); <c>seed</c> is the app's
/// single-sourced seed (defaulted).
/// </summary>
public sealed record QwenImageEditInpaintParams
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
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]
    [AllowNullable("null = the config declares no reference-image cap; distinct from a real 0 cap")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceInputs)] public string[]? ReferenceInputs { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceLatentsMethod)] public required string ReferenceLatentsMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)] public int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskBlur)]
    [Range(0, 31)] public required int MaskBlur { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
