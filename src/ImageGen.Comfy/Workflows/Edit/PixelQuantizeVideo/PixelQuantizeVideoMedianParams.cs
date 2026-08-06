using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.PixelQuantizeVideo;

/// <summary>The median named-palette video contract (a locked palette, temporally consistent). Both knobs are <c>required</c>.</summary>
public sealed record PixelQuantizeVideoMedianParams : PixelQuantizeVideoParams
{
    [JsonPropertyName(WorkflowParamKeys.Palette)] public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)] public required string FinalMethod { get; init; }
}
