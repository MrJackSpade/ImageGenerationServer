using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Raises a model's classifier-free-guidance scale over a late slice of the schedule (Ideogram 4's asymmetric
/// CFG). One typed record per ComfyUI class type; inputs are declared in the exact order the old anonymous-object
/// inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record CFGOverride : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CFGOverride;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("cfg")] public required double Cfg { get; init; }
    [JsonPropertyName("start_percent")] public required double StartPercent { get; init; }
    [JsonPropertyName("end_percent")] public required double EndPercent { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
