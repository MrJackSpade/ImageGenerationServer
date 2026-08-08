using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.LtxV2I2V;

/// <summary>LTX-2 i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for
/// the typed <c>LoadModel</c>, driven by loader=unet_gguf + dual=true), the <c>SamplerCustom</c> settings, the clip
/// length + playback fps, and the optional preset LoRA. LTX runs its own LTXVScheduler, so no <c>scheduler</c> param is
/// read. The <c>*Req</c> reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings;
/// <c>lora</c> is a nullable model-ref and <c>lora_strength</c> a defaulted double (only read when a LoRA is set);
/// <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record LtxV2I2VParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }
}
