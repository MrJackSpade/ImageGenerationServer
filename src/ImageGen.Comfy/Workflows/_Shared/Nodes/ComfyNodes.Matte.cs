using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>BiRefNet background-removal matte (PixelHarness) — output 0 is the RGBA frame (source RGB + matte as alpha).
/// The node loads its own BiRefNet model, so no checkpoint is wired. (Typed node record — one per ComfyUI class type;
/// inputs are declared in the exact order the old anonymous-object inputs were written, so the emitted graph is
/// byte-identical.)</summary>
public sealed record BiRefNetMatte : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.BiRefNetMatte;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("threshold")] public required double Threshold { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
