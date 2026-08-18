using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Step1XEdit;

/// <summary>Step1X-Edit parameters — the DiT/AE model refs (<c>Model()</c> reads → <c>required</c>), the diffusion
/// knobs and the <c>size_level</c> (from <c>width</c>), plus the app's single-sourced seed (defaulted).</summary>
public sealed record Step1XParams
{
    [JsonPropertyName(WorkflowParamKeys.DiffusionModel)] public required string DiffusionModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Step1xVae)] public required string Step1xVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)] public required int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
