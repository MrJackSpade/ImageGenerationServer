using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Ideogram4Refine;

/// <summary>
/// Ideogram 4's scheduler variant whose dimensions are wired from <see cref="GetImageSize"/> on the normalized edit
/// source. The generation graph uses literal composer dimensions; refine must follow the actual VAE input image.
/// </summary>
internal sealed record Ideogram4SchedulerFromSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Ideogram4Scheduler;
    [JsonPropertyName("steps")] public required int Steps { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("mu")] public required double Mu { get; init; }
    [JsonPropertyName("std")] public required double Std { get; init; }
}
