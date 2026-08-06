using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.ChronoEdit;

/// <summary>Rescales a model's rotary position embedding along x/y/t (ComfyUI core) — ChronoEdit's Wan RoPE fix-up.</summary>
public sealed record ScaleROPE : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ScaleROPE;
    [JsonPropertyName("model")]   public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("scale_x")] public required double ScaleX { get; init; }
    [JsonPropertyName("shift_x")] public required double ShiftX { get; init; }
    [JsonPropertyName("scale_y")] public required double ScaleY { get; init; }
    [JsonPropertyName("shift_y")] public required double ShiftY { get; init; }
    [JsonPropertyName("scale_t")] public required double ScaleT { get; init; }
    [JsonPropertyName("shift_t")] public required double ShiftT { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
