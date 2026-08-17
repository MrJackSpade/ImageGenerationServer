using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>
/// Applies the frozen residual correction to the Ideogram 4 conditional model on its first denoising pass.
/// The custom node clones the in-memory model patcher; it never edits checkpoint files.
/// </summary>
public sealed record Ideogram4CorrectionPatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Ideogram4CorrectionPatch;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("strength")] public required double Strength { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
