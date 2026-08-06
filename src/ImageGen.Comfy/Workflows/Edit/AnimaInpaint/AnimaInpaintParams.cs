using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.AnimaInpaint;

/// <summary>Anima masked-inpaint parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings and the masked-region <c>denoise</c> (all
/// <c>required</c>), the required mask grow, and the optional prefix/negative (nullable strings) + Has-guarded
/// <c>clip_skip</c>. <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record AnimaInpaintParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]         public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]    public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]       public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]  public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]      public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]        public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]      public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.0, 1.0)]                                    public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]       public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipSkip)]
    [AllowNullable("null = the config didn't set clip skip; the CLIPSetLastLayer node is emitted only when set, distinct from a real 0")] public int? ClipSkip { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaskGrow)]
    [Range(0, 64)]                                       public required int MaskGrow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]           public long Seed { get; init; }
}
