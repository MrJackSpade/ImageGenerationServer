using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelQuantize;

/// <summary>Pixel-quantizer parameters shared by BOTH engine contracts — the grid/virtual-resolution snap and the
/// key-background matte. <c>engine</c> is a CONTRACT DISCRIMINATOR, not a field: a config is either the feature-preserving
/// engine (<see cref="PixelQuantizeFpParams"/>) or the median named-palette engine (<see cref="PixelQuantizeMedianParams"/>),
/// and the deserializer materializes the one the <c>engine</c> key names — each carrying only ITS engine's knobs, all
/// <c>required</c> (audit #125 C). The two are distinct shapes; neither is the other with its fields nulled out.
/// <para>(<c>matte_threshold</c> is still nullable — it is a SEPARATE optional gated by <c>key_background</c>, a keyed/
/// un-keyed distinction orthogonal to the engine; splitting that is its own contract question.)</para></summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = WorkflowParamKeys.Engine)]
[JsonDerivedType(typeof(PixelQuantizeFpParams), ComfyWidgets.PixelEngine.Fp)]
[JsonDerivedType(typeof(PixelQuantizeMedianParams), ComfyWidgets.PixelEngine.Median)]
public abstract record PixelQuantizeParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)]                                        public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)]                                        public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)]                                        public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.KeyBackground)]     public bool KeyBackground { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MatteThreshold)]
    [AllowNullable("null = the config didn't set the matte cutoff; the BiRefNet node input is emitted only when key_background is on, distinct from a real 0 (soft matte)")]
    [Range(0.0, 1.0)]                                       public double? MatteThreshold { get; init; }
}
