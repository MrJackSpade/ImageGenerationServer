using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.PixelVideo;

/// <summary>The pixel-video decorator's own knobs. The quantizer's grid/palette/method/virtual-resolution are
/// <c>required</c> (every pixel-video config sets them and <see cref="PixelVideoGraph.QuantizeFrames"/> always reads
/// them); <c>guided</c> is a defaulted toggle; the projection ramp (<c>w_start</c>/<c>w_end</c>/<c>start_percent</c>/
/// <c>end_percent</c>/<c>project_every</c>) is nullable — read only when <c>guided</c>, via the <c>Required*</c>
/// accessors that refuse an absent value exactly as <c>DblReq</c>/<c>IntReq</c> would.</summary>
public sealed record PixelVideoParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)] public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)] public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)] public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)] public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Method)] public required string Method { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guided)] public bool Guided { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WStart)]
    [AllowNullable("null = the config didn't set the projection ramp w_start; read only when guided (via RequiredWStart), distinct from a real 0 weight")] public double? WStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WEnd)]
    [AllowNullable("null = the config didn't set the projection ramp w_end; read only when guided (via RequiredWEnd), distinct from a real 0 weight")] public double? WEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StartPercent)]
    [AllowNullable("null = the config didn't set the projection window start_percent; read only when guided (via RequiredStartPercent), distinct from a real 0")] public double? StartPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPercent)]
    [AllowNullable("null = the config didn't set the projection window end_percent; read only when guided (via RequiredEndPercent), distinct from a real 0")] public double? EndPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjectEvery)]
    [AllowNullable("null = the config didn't set project_every; read only when guided (via RequiredProjectEvery), distinct from a real 0")] public int? ProjectEvery { get; init; }

    public double RequiredWStart() => WStart ?? throw Missing(WorkflowParamKeys.WStart);
    public double RequiredWEnd() => WEnd ?? throw Missing(WorkflowParamKeys.WEnd);
    public double RequiredStartPercent() => StartPercent ?? throw Missing(WorkflowParamKeys.StartPercent);
    public double RequiredEndPercent() => EndPercent ?? throw Missing(WorkflowParamKeys.EndPercent);
    public int RequiredProjectEvery() => ProjectEvery ?? throw Missing(WorkflowParamKeys.ProjectEvery);

    private static RenderValidationException Missing(string key) => new(
        $"This configuration needs a value for '{key}' and none is set. It must supply one — there is no default.");
}
