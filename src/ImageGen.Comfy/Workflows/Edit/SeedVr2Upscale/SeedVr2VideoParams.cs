using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>SeedVR2 video restoration parameters. Video dimensions are decoded inside ComfyUI, so the target is the
/// model's native short-edge resolution rather than the still editor's source-dimension-derived scale multiplier.</summary>
public sealed record SeedVr2VideoParams
{
    [JsonPropertyName(WorkflowParamKeys.DitModel)] public required string DitModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeModel)] public required string VaeModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resolution)]
    [Range(16, 16384)] public required int Resolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaxResolution)]
    [Range(0, 16384)] public required int MaxResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BatchSize)]
    [Range(1, 16384)] public required int BatchSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.UniformBatchSize)] public bool UniformBatchSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.TemporalOverlap)]
    [Range(0, 16)] public required int TemporalOverlap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PrependFrames)]
    [Range(0, 32)] public required int PrependFrames { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ColorCorrection)] public required string ColorCorrection { get; init; }
    [JsonPropertyName(WorkflowParamKeys.InputNoiseScale)]
    [Range(0, 1)] public required double InputNoiseScale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LatentNoiseScale)]
    [Range(0, 1)] public required double LatentNoiseScale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Device)] public required string Device { get; init; }
    [JsonPropertyName(WorkflowParamKeys.OffloadDevice)] public required string OffloadDevice { get; init; }
    [JsonPropertyName(WorkflowParamKeys.AttentionMode)] public required string AttentionMode { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BlocksToSwap)]
    [Range(0, 36)] public required int BlocksToSwap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SwapIoComponents)] public bool SwapIoComponents { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CacheModel)] public bool CacheModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTiled)] public bool VaeTiled { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTileSize)] public required int VaeTileSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTileOverlap)] public required int VaeTileOverlap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EnableDebug)] public bool EnableDebug { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)] [SeedRange] public long Seed { get; init; }
}
