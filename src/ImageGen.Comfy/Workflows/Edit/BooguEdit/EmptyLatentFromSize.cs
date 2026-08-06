using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.BooguEdit;

/// <summary>An empty latent whose width/height are WIRED from another node's int outputs (e.g. <see cref="GetImageSize"/>).
/// Same ComfyUI class type as <see cref="EmptyLatent"/>, but its dimensions are edges rather than literals, so it is a
/// distinct record — they serialize as <c>["nodeId", idx]</c> to keep the emitted graph byte-identical.</summary>
public sealed record EmptyLatentFromSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyLatentImage;
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}
