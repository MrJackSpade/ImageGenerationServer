using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Krea 2's shared parameters: the standard txt2img knobs plus the per-layer conditioning rebalance (the
/// "uncensor" knob) — a global multiplier and the 12 per-layer gains for Krea 2's tapped Qwen3-VL layers. The
/// single-pass <see cref="Krea2Workflow"/> and the two-stage <see cref="Krea2RefineWorkflow"/> both read these.</summary>
public record Krea2Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.RebalanceMultiplier)]
    [Range(1.0, 8.0)] public required double Multiplier { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PerLayerWeights)] public required string PerLayerWeights { get; init; }
}
