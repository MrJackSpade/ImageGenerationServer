using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SdxlAnimateDiff;

/// <summary>Repeats a latent into a batch (ComfyUI core <c>RepeatLatentBatch</c>) — SDXL AnimateDiff seeds every frame
/// from the img2img source latent. Output 0 is the batched LATENT.</summary>
public sealed record RepeatLatentBatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RepeatLatentBatch;
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("amount")]  public required int Amount { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}
