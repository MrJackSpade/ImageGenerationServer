using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.LtxV2T2V;

/// <summary>LTX-2 / 2.3 / 2.5 text→video parameters: the shared txt2img knobs plus clip <c>length</c> (frames) and
/// playback <c>fps</c>. CFG is the base's nullable value, read through <c>RequiredCfg()</c> by the SamplerCustom chain;
/// LTX runs its own <c>LTXVScheduler</c>, so the base <c>scheduler</c>/<c>latent</c> knobs are unused here.</summary>
public sealed record LtxV2T2VParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
}
