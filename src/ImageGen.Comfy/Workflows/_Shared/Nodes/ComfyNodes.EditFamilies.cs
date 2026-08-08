using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Wraps a model + conditioning into a guider for the custom-sampler path (ComfyUI core) — the Flux.2 Klein
/// edit's <c>SamplerCustomAdvanced</c> takes a guider rather than the model/positive/negative <see cref="KSampler"/>
/// bundles. One typed record per ComfyUI class type; inputs are declared in the exact order the old anonymous-object
/// inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record BasicGuider : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.BasicGuider;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("conditioning")] public required Output<Slot.Conditioning> Conditioning { get; init; }
    public static Output<Slot.Guider> Out(string id) => new(id, 0);
}

/// <summary>The noise source for the custom-sampler path (ComfyUI core) — seeds <see cref="SamplerCustomAdvanced"/>.</summary>
public sealed record RandomNoise : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RandomNoise;
    [JsonPropertyName("noise_seed")] public required long NoiseSeed { get; init; }
    public static Output<Slot.Noise> Out(string id) => new(id, 0);
}

/// <summary>The advanced custom sampler (ComfyUI core) — takes an explicit noise source, guider, selected sampler and
/// sigma schedule rather than the name-and-scheduler pair <see cref="KSampler"/> bundles.</summary>
public sealed record SamplerCustomAdvanced : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SamplerCustomAdvanced;
    [JsonPropertyName("noise")] public required Output<Slot.Noise> Noise { get; init; }
    [JsonPropertyName("guider")] public required Output<Slot.Guider> Guider { get; init; }
    [JsonPropertyName("sampler")] public required Output<Slot.Sampler> Sampler { get; init; }
    [JsonPropertyName("sigmas")] public required Output<Slot.Sigmas> Sigmas { get; init; }
    [JsonPropertyName("latent_image")] public required Output<Slot.Latent> LatentImage { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>An empty Flux.2 latent whose width/height are WIRED from another node's int outputs (e.g.
/// <see cref="GetImageSize"/>), sized to the scaled source so the edit output matches its input's dimensions.</summary>
public sealed record EmptyFlux2LatentImage : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyFlux2LatentImage;
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>The Flux.2 sigma schedule (ComfyUI core) — a resolution-aware scheduler whose width/height are WIRED from
/// the scaled source's <see cref="GetImageSize"/> so the shift tracks the render size.</summary>
public sealed record Flux2Scheduler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Flux2Scheduler;
    [JsonPropertyName("steps")] public required int Steps { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    public static Output<Slot.Sigmas> Out(string id) => new(id, 0);
}

/// <summary>Mage-Flow-Edit's unified text encoder (<c>TextEncodeMageFlowEdit</c>): CLIP + instruction (+ optional
/// negative) + VAE + reference image(s), emitting positive (out 0), negative (out 1) and a zero latent sized to the
/// output (out 2). The images ride a single ComfyUI <b>autogrow</b> input group named <c>images</c>, so every image
/// socket is keyed <c>images.image_N</c> — NOT flat <c>image_N</c>. A flat key is silently dropped by ComfyUI's prompt
/// expansion (the node then sees zero images and produces a no-op edit — issue #216); the group prefix is mandatory.
/// The primary edited image is <c>images.image_1</c>; extra references are the DYNAMIC <c>images.image_2…N</c> slots,
/// so the fixed inputs are declared properties and the variable tail rides in an ordered overflow bag —
/// System.Text.Json emits <see cref="JsonExtensionData"/> AFTER the declared members in insertion order, reproducing the
/// exact <c>clip, prompt, negative_prompt, vae, width, height, batch_size, images.image_1, images.image_2…</c> order the
/// hand-built dictionary emitted.</summary>
public sealed record TextEncodeMageFlowEdit : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.TextEncodeMageFlowEdit;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("negative_prompt")] public required string NegativePrompt { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("images.image_1")] public required Output<Slot.Image> Image1 { get; init; }

    /// <summary>The dynamic reference tail, in emit order: each extra reference (<c>images.image_2</c>/<c>images.image_3</c>/…)
    /// wired to its scaled <see cref="Output{Slot.Image}"/>. Keys MUST carry the <c>images.</c> autogrow-group prefix.
    /// Null/empty when this edit takes no extra references.</summary>
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; init; }

    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}
