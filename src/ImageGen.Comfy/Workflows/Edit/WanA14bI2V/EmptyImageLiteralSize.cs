using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.WanA14bI2V;

/// <summary>A solid-colour image with LITERAL width/height (ComfyUI core <c>EmptyImage</c>) — the white pad canvas the
/// Wan i2v padding compositing draws onto. Same ComfyUI class type as <see cref="EmptyImage"/> but its dimensions are
/// fixed numbers (the computed pad geometry) rather than wired <see cref="Output{TSlot}"/> ints, so it is a distinct
/// record to keep the emitted graph byte-identical.</summary>
public sealed record EmptyImageLiteralSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyImage;
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("color")] public required int Color { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
