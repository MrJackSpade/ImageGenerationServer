using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.MiniMaxH3T2V;

/// <summary>MiniMax-H3 text→video parameters — the shared txt2img knobs plus the native-audio extras: the audio VAE
/// (a resolved model ref), the clip <c>length</c> (frames) and playback <c>fps</c>. The render size is read via the
/// base <c>Txt2ImgParams.Dims</c> (aspect map). <c>steps</c>/<c>sampler</c>/<c>scheduler</c> are the base's
/// <c>required</c> members; <c>seed</c> the single-sourced seed.</summary>
public sealed record MiniMaxH3Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.AudioVae)] public required string AudioVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]
    [Range(H3.MinFrames, H3.MaxFrames)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
}
