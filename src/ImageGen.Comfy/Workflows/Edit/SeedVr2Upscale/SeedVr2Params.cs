using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>SeedVR2 restore/upscale parameters — the DiT/VAE model refs, sizing, colour match, and memory-fit knobs.
/// The <c>*Req</c>-read values are <c>required</c>; the three boolean toggles keep their false default; <c>seed</c> is
/// the app's single-sourced generation seed (folded into the node's uint32 range in Build).</summary>
public sealed record SeedVr2Params
{
    [JsonPropertyName(WorkflowParamKeys.DitModel)]        public required string DitModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeModel)]        public required string VaeModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scale)]
    [Range(1, 4)]                                         public required int Scale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaxResolution)]   public required int MaxResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ColorCorrection)] public required string ColorCorrection { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Device)]          public required string Device { get; init; }
    [JsonPropertyName(WorkflowParamKeys.OffloadDevice)]   public required string OffloadDevice { get; init; }
    [JsonPropertyName(WorkflowParamKeys.AttentionMode)]   public required string AttentionMode { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BlocksToSwap)]
    [Range(0, 36)]                                        public required int BlocksToSwap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SwapIoComponents)] public bool SwapIoComponents { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CacheModel)]      public bool CacheModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTiled)]        public bool VaeTiled { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTileSize)]     public required int VaeTileSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTileOverlap)]  public required int VaeTileOverlap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BatchSize)]       public required int BatchSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]            public long Seed { get; init; }
}
