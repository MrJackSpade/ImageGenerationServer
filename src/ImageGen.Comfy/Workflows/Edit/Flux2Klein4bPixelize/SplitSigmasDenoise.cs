using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Flux2Klein4bPixelize;

/// <summary>Splits a sigma schedule at a denoise fraction (ComfyUI core) — the img2img tail of the Flux.2 Klein
/// pixelizer's custom-sampler path. Output 0 is the high-sigma head, output 1 the low-sigma tail the img2img runs.
/// One typed record per ComfyUI class type; inputs are declared in the exact order the old anonymous-object inputs
/// were written, so the emitted graph is byte-identical.</summary>
public sealed record SplitSigmasDenoise : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SplitSigmasDenoise;
    [JsonPropertyName("sigmas")]  public required Output<Slot.Sigmas> Sigmas { get; init; }
    [JsonPropertyName("denoise")] public required double Denoise { get; init; }
    public static Output<Slot.Sigmas> HighOut(string id) => new(id, 0);
    public static Output<Slot.Sigmas> LowOut(string id) => new(id, 1);
}
