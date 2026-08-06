using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.FluxKontextEdit;

/// <summary>Flux.1 Kontext parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the sampler settings, the distilled <c>guidance</c>, and the optional
/// <c>reference_max</c> cap (nullable: absent → this editor takes no reference images). The <c>*Req</c> reads are
/// <c>required</c>; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record FluxKontextParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]     public required double Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]
    [AllowNullable("null = the config declares no reference-image cap; distinct from a real 0 cap")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
