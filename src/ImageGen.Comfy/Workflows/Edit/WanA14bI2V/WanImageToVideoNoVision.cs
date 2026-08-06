using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.WanA14bI2V;

/// <summary>Wan i2v conditioning WITHOUT a clip-vision cue (ComfyUI core <c>WanImageToVideo</c>) — the Wan 2.2 A14B MoE
/// path bakes the start image into pos/neg conditioning and the video latent with no vision tower. Same ComfyUI class
/// type as <see cref="WanImageToVideo"/> but a distinct input shape (no <c>clip_vision_output</c>), so it is its own
/// record to keep the emitted graph byte-identical. Output 0 = positive, 1 = negative, 2 = latent.</summary>
public sealed record WanImageToVideoNoVision : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.WanImageToVideo;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("start_image")] public required Output<Slot.Image> StartImage { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}
