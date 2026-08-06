using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.DreamOmni2Pixelize;

/// <summary>DreamOmni2 pixelizer parameters — the two diffusion knobs the self-contained editor consumes
/// (<c>steps</c>/<c>cfg</c>, both <c>required</c>), the grid/palette/virtual-resolution + the projection ramp it runs
/// internally, and the <c>reference</c> %% (read via the img2img-strength map, so a defaulted int). <c>style_prompt</c>
/// is a nullable string; <c>width</c>/<c>height</c> are defaulted ints, <c>snap_resolution</c> a defaulted bool;
/// <c>seed</c> is the app's single-sourced seed (there is no <c>LoadModel</c> head — the editor loads its own weights).</summary>
public sealed record DreamOmni2PixelizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)] public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Reference)]
    [AllowNullable("null = the config didn't set the reference %; read via the img2img-strength map only when present, distinct from a real 0% (full generation)")]
    [Range(0, 100)] public int? Reference { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)] public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)] public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)] public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)]
    [Range(0, 4096)] public int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)]
    [Range(0, 4096)] public int Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SnapResolution)] public bool SnapResolution { get; init; }
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
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
