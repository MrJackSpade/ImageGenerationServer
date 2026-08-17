using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>
/// Applies the frozen, held-out-validated two-stage residual correction to the Ideogram 4 conditional model on
/// its first denoising pass. The custom node clones the in-memory model patcher; it never edits checkpoint files.
/// </summary>
public sealed record DebannerTwoStagePatch : ComfyNode
{
    public const double VALIDATED_STAGE1_STRENGTH = 0.4;
    public const double VALIDATED_STAGE2_STRENGTH = 0.6422342360019688;

    internal override string ClassType => ComfyNodeTypes.DebannerTwoStagePatch;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("stage1_strength")] public required double Stage1Strength { get; init; }
    [JsonPropertyName("stage2_strength")] public required double Stage2Strength { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
