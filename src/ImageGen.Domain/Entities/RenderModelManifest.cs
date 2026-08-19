using System.Text.Json.Serialization;

namespace ImageGen.Domain.Entities;

/// <summary>
/// The model files and loader settings resolved for one submitted render. This is a snapshot: a later change to a
/// machine''s model bindings must not rewrite which weights an existing image says it used.
/// </summary>
public sealed record RenderModelManifest
{
    /// <summary>Checkpoint/diffusion-model basename, without a machine-specific directory.</summary>
    [JsonPropertyName("checkpoint")] public string? Checkpoint { get; init; }
    /// <summary>The workflow loader mode (for example <c>checkpoint</c>, <c>unet</c>, or <c>unet_gguf</c>).</summary>
    [JsonPropertyName("loader")] public required string Loader { get; init; }
    /// <summary>The dtype requested from the loader; <c>default</c> means inspect/use the file''s native metadata.</summary>
    [JsonPropertyName("weightDtype")] public required string WeightDtype { get; init; }
    /// <summary>A conservative precision/quantization hint inferred from the checkpoint basename, or <c>unknown</c>.</summary>
    [JsonPropertyName("quantization")] public required string Quantization { get; init; }
    /// <summary>VAE basename, when the workflow loads one separately.</summary>
    [JsonPropertyName("vae")] public string? Vae { get; init; }
    /// <summary>Text-encoder basenames, in loader order.</summary>
    [JsonPropertyName("textEncoders")] public IReadOnlyList<string> TextEncoders { get; init; } = [];
}
