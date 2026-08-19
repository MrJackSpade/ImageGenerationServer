using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Krea2Redraw;

/// <summary>Krea 2 redraw parameters: the shared edit loader-head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings and the polish <c>denoise</c> strength (all
/// <c>required</c>), Krea 2's per-layer conditioning rebalance (<c>rebalance_multiplier</c> + <c>per_layer_weights</c>),
/// the optional base-model <c>lora</c>, and the app's single-sourced <c>seed</c>.</summary>
public sealed record Krea2RedrawParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)] public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)] public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)] public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)] public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)] public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.0, 0.9)] public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RebalanceMultiplier)]
    [Range(1.0, 8.0)] public required double Multiplier { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PerLayerWeights)] public required string PerLayerWeights { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)] public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.NativePixels)]
    [Range(1, int.MaxValue)] public required int NativePixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaxDimension)]
    [Range(0, 4096)] public required int MaxDimension { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
