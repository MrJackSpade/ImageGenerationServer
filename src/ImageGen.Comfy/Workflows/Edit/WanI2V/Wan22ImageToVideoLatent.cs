using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.WanI2V;

/// <summary>Wan 2.2 TI2V i2v latent (ComfyUI core) — seeds the video latent from the VAE + start image. Output 0 = latent.</summary>
public sealed record Wan22ImageToVideoLatent : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Wan22ImageToVideoLatent;
    [JsonPropertyName("vae")]         public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("width")]       public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]      public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("length")]      public required int Length { get; init; }
    [JsonPropertyName("batch_size")]  public required int BatchSize { get; init; }
    [JsonPropertyName("start_image")] public required Output<Slot.Image> StartImage { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}
