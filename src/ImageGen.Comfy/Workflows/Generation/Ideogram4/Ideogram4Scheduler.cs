using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Ideogram 4's own logit-normal sigma schedule (driven through <see cref="SamplerCustomAdvanced"/>).</summary>
public sealed record Ideogram4Scheduler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Ideogram4Scheduler;
    [JsonPropertyName("steps")] public required int Steps { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("mu")] public required double Mu { get; init; }
    [JsonPropertyName("std")] public required double Std { get; init; }
    public static Output<Slot.Sigmas> Out(string id) => new(id, 0);
}
