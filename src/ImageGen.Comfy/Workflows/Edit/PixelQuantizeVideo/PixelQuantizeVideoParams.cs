using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelQuantizeVideo;

/// <summary>Video pixel-quantizer parameters shared by BOTH engine contracts — the grid/virtual-resolution snap, the
/// output frame rate, and the key-background matte. <c>engine</c> is a CONTRACT DISCRIMINATOR: a config is either the
/// feature-preserving engine (<see cref="PixelQuantizeVideoFpParams"/>) or the median named-palette engine
/// (<see cref="PixelQuantizeVideoMedianParams"/>), each carrying only ITS engine's knobs, all <c>required</c> (audit
/// #125 C). (<c>matte_threshold</c> stays nullable — a separate optional gated by <c>key_background</c>, orthogonal to
/// the engine.)</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = WorkflowParamKeys.Engine)]
[JsonDerivedType(typeof(PixelQuantizeVideoFpParams), ComfyWidgets.PixelEngine.Fp)]
[JsonDerivedType(typeof(PixelQuantizeVideoMedianParams), ComfyWidgets.PixelEngine.Median)]
public abstract record PixelQuantizeVideoParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)]                                        public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)]                                        public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)]                                        public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]
    [Range(0.0, 60.0)]                                      public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.KeyBackground)]     public bool KeyBackground { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MatteThreshold)]
    [AllowNullable("null = the config didn't set the matte cutoff; the BiRefNet node input is emitted only when key_background is on, distinct from a real 0 (soft matte)")]
    [Range(0.0, 1.0)]                                       public double? MatteThreshold { get; init; }
}
