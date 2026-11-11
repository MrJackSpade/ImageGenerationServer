using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.MiniMaxH3ImageEdit;

/// <summary>MiniMax-H3-Image edit parameters: the distilled sampler knobs, the source's megapixel budget (the edit-quality
/// tier overrides it per request), and the cap on extra reference pictures beyond the edit target. No negative — the
/// model runs at CFG 1.</summary>
public sealed record MiniMaxH3ImageEditParams
{
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    [Range(MiniMaxH3ImageEditWorkflow.MinMegapixels, MiniMaxH3ImageEditWorkflow.MaxMegapixels)] public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)]
    [Range(0, MiniMaxH3ImageEditWorkflow.MaxExtraReferences)] public required int ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
