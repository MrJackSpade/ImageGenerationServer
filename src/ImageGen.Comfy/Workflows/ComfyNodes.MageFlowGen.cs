using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>MageFlow's unified text-encode + zero-latent node in TEXT-ONLY (t2i) mode: no reference images and no vae,
/// so it emits just (positive conditioning, negative conditioning, zero latent). Distinct input shape from the
/// edit-form <c>TextEncodeMageFlowEdit</c> record; inputs in the old anonymous-object order.</summary>
public sealed record TextEncodeMageFlowGen : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.TextEncodeMageFlowEdit;
    [JsonPropertyName("clip")]            public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("prompt")]          public required string Prompt { get; init; }
    [JsonPropertyName("negative_prompt")] public required string NegativePrompt { get; init; }
    [JsonPropertyName("width")]           public required int Width { get; init; }
    [JsonPropertyName("height")]          public required int Height { get; init; }
    [JsonPropertyName("batch_size")]      public required int BatchSize { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}
