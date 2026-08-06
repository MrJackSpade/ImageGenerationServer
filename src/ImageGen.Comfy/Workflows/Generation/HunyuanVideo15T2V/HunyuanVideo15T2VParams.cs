using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>HunyuanVideo 1.5 text→video parameters shared by BOTH SR contracts. The flow <c>shift</c>, clip <c>length</c>
/// and playback <c>fps</c> ride on top of the shared txt2img knobs. The super-resolution second pass is a CONTRACT, not a
/// set of nullable knobs: a config either asks for SR (<see cref="HunyuanVideo15T2VSrParams"/>, every <c>sr_*</c>
/// required) or does not (<see cref="HunyuanVideo15T2VNoSrParams"/>, none present); <see cref="HunyuanVideo15T2VParamsConverter"/>
/// reads the <c>sr</c> toggle and materializes the right one (audit #125 C).</summary>
public abstract record HunyuanVideo15T2VParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)]       public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]      public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]         public required double Fps { get; init; }
}
