using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>ComfyUI's built-in per-model attention-backend selector (a MODEL patch, category model/patch) — splices
/// between the loader/LoRA head and the sampler to run that model's attention on a chosen kernel. Requires
/// ComfyUI ≥ 62b3c94b (the comfy-kitchen attention commit, past v0.31.1).</summary>
public sealed record ModelAttentionBackend : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ModelAttentionBackend;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("attention")] public required string Attention { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
