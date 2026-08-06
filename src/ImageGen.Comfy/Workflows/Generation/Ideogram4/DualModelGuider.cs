using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Fuses a conditional and a separate unconditional model into a single guider at a base CFG (Ideogram 4's
/// dual-model classifier-free guidance).</summary>
public sealed record DualModelGuider : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.DualModelGuider;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("model_negative")] public required Output<Slot.Model> ModelNegative { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("cfg")] public required double Cfg { get; init; }
    public static Output<Slot.Guider> Out(string id) => new(id, 0);
}
