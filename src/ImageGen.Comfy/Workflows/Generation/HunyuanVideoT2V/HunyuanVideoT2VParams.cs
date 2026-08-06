using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.HunyuanVideoT2V;

/// <summary>Original HunyuanVideo 13B text→video parameters. Adds the flow <c>shift</c>, clip <c>length</c> and playback
/// <c>fps</c> to the shared txt2img knobs; the embedded FluxGuidance value is the base <c>guidance</c> (required by this
/// guidance-distilled graph, read through <see cref="RequiredGuidance"/>).</summary>
public sealed record HunyuanVideoT2VParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)]  public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]    public required double Fps { get; init; }

    /// <summary>The embedded-guidance value this graph cannot build without — the base's nullable <c>guidance</c>, or a
    /// refusal naming it (the typed form of <c>DblReq(guidance)</c>).</summary>
    public double RequiredGuidance() => Guidance ?? throw new RenderValidationException(
        $"This configuration needs a value for '{WorkflowParamKeys.Guidance}' and none is set. It must supply one — there is no default.");
}
