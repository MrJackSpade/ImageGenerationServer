using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelQuantize;

/// <summary>The median named-palette pixel-quantize contract: OKLab nearest snap onto a named/inline palette. Both knobs
/// are <c>required</c> — a median config supplies them; an fp config is a different shape.</summary>
public sealed record PixelQuantizeMedianParams : PixelQuantizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Palette)]           public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)]       public required string FinalMethod { get; init; }
}
