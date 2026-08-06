using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>The t2v params for a config WITH the super-resolution second pass — every <c>sr_*</c> knob required.</summary>
public sealed record HunyuanVideo15T2VSrParams : HunyuanVideo15T2VParams, IHunyuanSrPass
{
    [JsonPropertyName(WorkflowParamKeys.SrModel)]     public required string SrModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrUpsampler)] public required string SrUpsampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrWidth)]     public required int SrWidth { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrHeight)]    public required int SrHeight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrSteps)]
    [Range(1, 50)]                                    public required int SrSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrDenoise)]
    [Range(ParamBounds.DenoiseMin, ParamBounds.DenoiseMax)] public required double SrDenoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrNoiseAug)]
    [Range(0.0, 1.0)]                                 public required double SrNoiseAug { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrCfg)]
    [Range(1.0, 12.0)]                                public required double SrCfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrShift)]
    [Range(1.0, 12.0)]                                public required double SrShift { get; init; }
}
