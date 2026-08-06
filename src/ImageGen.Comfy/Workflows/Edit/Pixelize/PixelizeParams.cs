using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Pixelize;

/// <summary>Diffusion-pixelizer parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the grid/palette/virtual-resolution + the
/// projection ramp, and the megapixel working area. The <c>*Req</c>/grid/ramp reads are <c>required</c>;
/// <c>weight_dtype</c>/<c>clip_type</c>/<c>style_prompt</c> are nullable strings, <c>guidance</c> is a nullable double
/// (omit the node when unset), <c>reference</c>/<c>width</c>/<c>height</c> are defaulted ints and <c>snap_resolution</c>
/// a defaulted bool; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record PixelizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guidance)]
    [AllowNullable("null = the config declares no Flux distilled guidance; the FluxGuidance node is emitted only when set (omitted for non-flux), distinct from a real 0")] public double? Guidance { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)] public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Reference)]
    [AllowNullable("null = the config didn't set the reference %; read via the denoise map only when present, distinct from a real 0% (generate fresh)")]
    [Range(0, 100)] public int? Reference { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)] public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)] public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)] public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    [Range(0.1, 4.0)] public required double Megapixels { get; init; }
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
