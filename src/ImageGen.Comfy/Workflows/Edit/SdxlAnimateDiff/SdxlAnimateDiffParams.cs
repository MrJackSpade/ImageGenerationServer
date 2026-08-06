using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SdxlAnimateDiff;

/// <summary>SDXL AnimateDiff i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the clip length + playback fps, the motion
/// module, the AnimateDiff <c>beta_schedule</c>, and the img2img <c>denoise</c> (source ↔ motion tradeoff). The
/// <c>*Req</c>/<c>Model()</c> reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings;
/// <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record SdxlAnimateDiffParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(ParamBounds.DenoiseMin, ParamBounds.DenoiseMax)] public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MotionModel)]  public required string MotionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BetaSchedule)] public required string BetaSchedule { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
