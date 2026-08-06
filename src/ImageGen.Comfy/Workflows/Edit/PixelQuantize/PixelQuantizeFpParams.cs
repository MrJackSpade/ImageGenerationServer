using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.PixelQuantize;

/// <summary>The feature-preserving (fp) pixel-quantize contract: L0 flatten + XDoG thicken + de-AA + DIN99d global
/// palette. Every knob is <c>required</c> — an fp config supplies them all; a median config is a different shape and
/// carries none of them.</summary>
public sealed record PixelQuantizeFpParams : PixelQuantizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Thicken)]
    [Range(0.0, 8.0)] public required double Thicken { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Tau)]
    [Range(0.0, 2.0)] public required double Tau { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lam)]
    [Range(0.001, 0.2)] public required double Lam { get; init; }
    [JsonPropertyName(WorkflowParamKeys.K)]
    [Range(2, 128)] public required int K { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Beta)]
    [Range(0.0, 4.0)] public required double Beta { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Step)]
    [Range(1.0, 20.0)] public required double Step { get; init; }
    /// <summary>Replay globals from a previous fp run — genuinely optional WITHIN the fp contract (empty = derive from
    /// this image), so nullable strings, not a branch on another shape.</summary>
    [JsonPropertyName(WorkflowParamKeys.FpPalette)] public string? FpPalette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FpFrequencies)] public string? FpFrequencies { get; init; }
}
