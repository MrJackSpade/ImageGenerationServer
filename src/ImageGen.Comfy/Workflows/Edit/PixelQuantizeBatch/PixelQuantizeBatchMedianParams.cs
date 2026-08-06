using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.PixelQuantizeBatch;

/// <summary>The median named-palette batch contract. Both knobs are <c>required</c>.</summary>
public sealed record PixelQuantizeBatchMedianParams : PixelQuantizeBatchParams
{
    [JsonPropertyName(WorkflowParamKeys.Palette)] public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)] public required string FinalMethod { get; init; }
}
