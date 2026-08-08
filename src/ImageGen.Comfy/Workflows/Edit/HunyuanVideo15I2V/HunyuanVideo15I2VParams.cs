using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.HunyuanVideo15I2V;

/// <summary>HunyuanVideo 1.5 image→video parameters shared by BOTH SR contracts — the shared loader-head knobs
/// (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings + flow
/// <c>shift</c>, the SigCLIP vision encoder (<c>clip_vision</c>, a resolved model ref), the clip <c>length</c> + playback
/// <c>fps</c>, and an optional preset LoRA. The super-resolution second pass is a CONTRACT, not a set of nullable knobs:
/// a config either asks for SR (<see cref="HunyuanVideo15I2VSrParams"/>, every <c>sr_*</c> required) or does not
/// (<see cref="HunyuanVideo15I2VNoSrParams"/>, none present); <see cref="HunyuanVideo15I2VParamsConverter"/> reads the
/// <c>sr</c> toggle and materializes the right one (audit #125 C). <c>cfg</c> is nullable-with-throw (mirrors the shared
/// txt2img contract; always present in the i2v configs) — a separate concern from SR.</summary>
public abstract record HunyuanVideo15I2VParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]
    [AllowNullable("null = the config didn't set CFG; RequiredCfg() refuses an absent value (this real-CFG guider always has it in the i2v configs), distinct from a real 0")] public double? Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]
    [Range(1.0, 12.0)] public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipVision)] public required string ClipVision { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    public required double Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)] public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] public long Seed { get; init; }

    /// <summary>CFG, required by this graph's real-CFG guider — the base's nullable <c>cfg</c>, or a refusal naming it
    /// (the typed form of <c>DblReq(cfg)</c>).</summary>
    public double RequiredCfg() => Cfg ?? throw new RenderValidationException(
        $"This configuration needs a value for '{WorkflowParamKeys.Cfg}' and none is set. It must supply one — there is no default.");
}
