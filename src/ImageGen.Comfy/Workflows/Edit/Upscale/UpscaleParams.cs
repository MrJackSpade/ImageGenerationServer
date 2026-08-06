using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.Upscale;

/// <summary>Upscaler parameters. <c>model_scale</c>/<c>scale</c> were read as doubles (the ratio math); the model-ref
/// filename and the resampler are <c>required</c> (the old Model()/StrReq reads throw on absent).</summary>
public sealed record UpscaleParams
{
    [JsonPropertyName(WorkflowParamKeys.UpscaleModel)] public required string UpscaleModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ModelScale)]
    [Range(1.0, 8.0)]                                  public required double ModelScale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scale)]
    [Range(1.0, 4.0)]                                  public required double Scale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resample)]     public required string Resample { get; init; }
}
