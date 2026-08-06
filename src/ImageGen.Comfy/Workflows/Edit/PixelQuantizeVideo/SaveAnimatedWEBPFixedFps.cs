using ImageGen.Comfy;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.PixelQuantizeVideo;

/// <summary>Saves frames as an animated WEBP with a LITERAL frame rate (the video quantizer's explicit-fps branch).
/// The same class type as <see cref="SaveAnimatedWEBP"/>, but its <c>fps</c> is a constant double rather than a wired
/// <see cref="Slot.Float"/> edge, so it is a distinct record; inputs are declared in the exact order the old anonymous
/// object wrote them, so the emitted graph is byte-identical.</summary>
public sealed record SaveAnimatedWEBPFixedFps : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SaveAnimatedWEBP;
    [JsonPropertyName("images")]          public required Output<Slot.Image> Images { get; init; }
    [JsonPropertyName("filename_prefix")] public required string FilenamePrefix { get; init; }
    [JsonPropertyName("fps")]             public required double Fps { get; init; }
    [JsonPropertyName("lossless")]        public required bool Lossless { get; init; }
    [JsonPropertyName("quality")]         public required int Quality { get; init; }
    [JsonPropertyName("method")]          public required string Method { get; init; }
}
