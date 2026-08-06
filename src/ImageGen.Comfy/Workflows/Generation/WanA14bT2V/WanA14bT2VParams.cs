using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.WanA14bT2V;

/// <summary>Wan 2.2 T2V-A14B text→video parameters. A custom-Build MoE model, so its own guidance is the dual
/// <c>cfg_high</c>/<c>cfg_low</c> (the base <c>cfg</c> is left unset), and its two experts drive their own loaders via
/// <c>unet_low</c> + <c>shift</c>. The MoE step window (<c>steps</c> from the base, <c>boundary</c>) and clip
/// <c>length</c>/<c>fps</c> are required; <c>refiner_steps</c> is optional (absent = the legacy shared-schedule tail).</summary>
public sealed record WanA14bT2VParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.UnetLow)]      public required string UnetLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]        public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Boundary)]     public required int Boundary { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgHigh)]      public required double CfgHigh { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgLow)]       public required double CfgLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSteps)] [AllowNullable("null = the config didn't set refiner_steps; absent means the legacy shared-schedule tail, distinct from a real 0 (decode the handoff as-is)")] public int? RefinerSteps { get; init; }
}
