using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.PixelAnima;

/// <summary>Pixel-Anima parameters: the shared txt2img knobs plus the pixel-manifold grid/palette/virtual-resolution
/// and per-step projection ramp read by both the denoise-model patch and the final quantize. Virtual resolution sets
/// the sprite's pixel count on its longest edge (0 = use the explicit grid); grid_w/grid_h are the explicit fallback.</summary>
public sealed record PixelAnimaParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)] public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)] public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)] public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)] public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjMethod)] public required string ProjMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)] public required string FinalMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WStart)]
    [Range(0.0, 1.0)] public required double WStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WEnd)]
    [Range(0.0, 1.0)] public required double WEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StartPercent)]
    [Range(0.0, 1.0)] public required double StartPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPercent)]
    [Range(0.0, 1.0)] public required double EndPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjectEvery)]
    [Range(1, 8)] public required int ProjectEvery { get; init; }
}
