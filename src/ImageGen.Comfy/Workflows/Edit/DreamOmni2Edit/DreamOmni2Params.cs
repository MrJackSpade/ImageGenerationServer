using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.DreamOmni2Edit;

/// <summary>DreamOmni2 parameters — the two diffusion knobs read by the pipeline (<c>*Req</c> reads → <c>required</c>)
/// plus the app's single-sourced seed (defaulted; folded through <see cref="ComfyGraph.Seed(long)"/> in Build).</summary>
public sealed record DreamOmni2Params
{
    [JsonPropertyName(WorkflowParamKeys.BaseModel)] public required string BaseModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
