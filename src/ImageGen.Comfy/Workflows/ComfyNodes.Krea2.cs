using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Krea 2's per-layer conditioning rebalance node (the "uncensor" splice between the positive text-encode and
/// the sampler). Inputs in the old anonymous-object order.</summary>
public sealed record ConditioningKrea2Rebalance : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ConditioningKrea2Rebalance;
    [JsonPropertyName("conditioning")]      public required Output<Slot.Conditioning> Conditioning { get; init; }
    [JsonPropertyName("multiplier")]        public required double Multiplier { get; init; }
    [JsonPropertyName("per_layer_weights")] public required string PerLayerWeights { get; init; }
    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}
