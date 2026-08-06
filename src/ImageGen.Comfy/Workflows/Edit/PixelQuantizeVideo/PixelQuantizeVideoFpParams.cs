using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelQuantizeVideo;

/// <summary>The feature-preserving (fp) video contract — one global per-video palette. Every knob is <c>required</c>.</summary>
public sealed record PixelQuantizeVideoFpParams : PixelQuantizeVideoParams
{
    [JsonPropertyName(WorkflowParamKeys.Thicken)]
    [Range(0.0, 8.0)]                                       public required double Thicken { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Tau)]
    [Range(0.0, 2.0)]                                       public required double Tau { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lam)]
    [Range(0.001, 0.2)]                                     public required double Lam { get; init; }
    [JsonPropertyName(WorkflowParamKeys.K)]
    [Range(2, 128)]                                         public required int K { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Beta)]
    [Range(0.0, 4.0)]                                       public required double Beta { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Step)]
    [Range(1.0, 20.0)]                                      public required double Step { get; init; }
}
