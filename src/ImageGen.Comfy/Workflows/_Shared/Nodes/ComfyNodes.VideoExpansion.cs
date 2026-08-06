using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>The advanced KSampler (ComfyUI core) — the two-stage MoE sampler node for the Wan 2.2 A14B experts. Takes an
/// explicit add-noise flag, step window (<c>start_at_step</c>/<c>end_at_step</c>) and leftover-noise flag rather than
/// the single denoise fraction <see cref="KSampler"/> bundles. One typed node record per ComfyUI class type; inputs are
/// declared in the exact order the old anonymous-object inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record KSamplerAdvanced : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.KSamplerAdvanced;
    [JsonPropertyName("add_noise")] public required string AddNoise { get; init; }
    [JsonPropertyName("noise_seed")] public required long NoiseSeed { get; init; }
    [JsonPropertyName("steps")] public required int Steps { get; init; }
    [JsonPropertyName("cfg")] public required double Cfg { get; init; }
    [JsonPropertyName("sampler_name")] public required string SamplerName { get; init; }
    [JsonPropertyName("scheduler")] public required string Scheduler { get; init; }
    [JsonPropertyName("start_at_step")] public required int StartAtStep { get; init; }
    [JsonPropertyName("end_at_step")] public required int EndAtStep { get; init; }
    [JsonPropertyName("return_with_leftover_noise")] public required string ReturnWithLeftoverNoise { get; init; }
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("latent_image")] public required Output<Slot.Latent> LatentImage { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Wan first/last-frame conditioning (ComfyUI core) — pins BOTH ends of the clip (the source is the first frame,
/// the supplied end frame the last) and emits the (positive, negative, latent) triple. Output 0 = positive, 1 = negative,
/// 2 = latent.</summary>
public sealed record WanFirstLastFrameToVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.WanFirstLastFrameToVideo;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("start_image")] public required Output<Slot.Image> StartImage { get; init; }
    [JsonPropertyName("end_image")] public required Output<Slot.Image> EndImage { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

/// <summary>HunyuanVideo 1.5 i2v conditioning (ComfyUI core) — bakes the start image + clip-vision cue into pos/neg
/// conditioning and the video latent. Output 0 = positive, 1 = negative, 2 = latent.</summary>
public sealed record HunyuanVideo15ImageToVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.HunyuanVideo15ImageToVideo;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("start_image")] public required Output<Slot.Image> StartImage { get; init; }
    [JsonPropertyName("clip_vision_output")] public required Output<Slot.ClipVision> ClipVisionOutput { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

/// <summary>An empty HunyuanVideo (original 13B) video latent (ComfyUI core) — seeds the text-to-video clip. Its
/// width/height/length are literal render dimensions. Output 0 = latent.</summary>
public sealed record EmptyHunyuanLatentVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyHunyuanLatentVideo;
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Tiled latent→image decode (ComfyUI core) — the memory-bounded VAE decode the video graphs use for large
/// clips (and the SR second pass). Output 0 = image.</summary>
public sealed record VAEDecodeTiled : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.VAEDecodeTiled;
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("tile_size")] public required int TileSize { get; init; }
    [JsonPropertyName("overlap")] public required int Overlap { get; init; }
    [JsonPropertyName("temporal_size")] public required int TemporalSize { get; init; }
    [JsonPropertyName("temporal_overlap")] public required int TemporalOverlap { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Wraps a model + real-CFG positive/negative conditioning into a guider for the custom-sampler path (ComfyUI
/// core) — the negative-aware sibling of <see cref="BasicGuider"/>. Output 0 = guider.</summary>
public sealed record CFGGuider : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CFGGuider;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("cfg")] public required double Cfg { get; init; }
    public static Output<Slot.Guider> Out(string id) => new(id, 0);
}

/// <summary>Loads a latent-space upscale model (ComfyUI core) for the HunyuanVideo 1.5 SR pass. Output 0 = the upscale
/// model handle.</summary>
public sealed record LatentUpscaleModelLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LatentUpscaleModelLoader;
    [JsonPropertyName("model_name")] public required string ModelName { get; init; }
    public static Output<Slot.UpscaleModel> Out(string id) => new(id, 0);
}

/// <summary>Rescales a HunyuanVideo 1.5 latent sequence with a latent upsampler model (ComfyUI core) — the SR pass's
/// latent-space upscale to the 1080p target. Output 0 = latent.</summary>
public sealed record HunyuanVideo15LatentUpscaleWithModel : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.HunyuanVideo15LatentUpscaleWithModel;
    [JsonPropertyName("model")] public required Output<Slot.UpscaleModel> Model { get; init; }
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("crop")] public required string Crop { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>The HunyuanVideo 1.5 SR conditioning node in either input shape — <see cref="HunyuanVideo15SuperResolutionI2V"/>
/// (start image + clip-vision cue) or <see cref="HunyuanVideo15SuperResolutionT2V"/> (neither). Same ComfyUI class type and
/// the same (positive, negative, latent) output triple; the two paths are DISTINCT records rather than one with conditional
/// nullable inputs (audit #125 A′). The shared output-slot accessors live here so both shapes expose one output contract.</summary>
public interface IHunyuanVideo15SuperResolution
{
    static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

/// <summary>The i2v shape of the HunyuanVideo 1.5 SR node: re-emits the (positive, negative, latent) triple for the SR model
/// with the source image + clip-vision cue baked in as the consistency signal. Output 0 = positive, 1 = negative, 2 = latent.</summary>
public sealed record HunyuanVideo15SuperResolutionI2V : ComfyNode, IHunyuanVideo15SuperResolution
{
    internal override string ClassType => ComfyNodeTypes.HunyuanVideo15SuperResolution;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("latent")] public required Output<Slot.Latent> Latent { get; init; }
    [JsonPropertyName("noise_augmentation")] public required double NoiseAugmentation { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    /// <summary>The i2v start image (the SR consistency cue).</summary>
    [JsonPropertyName("start_image")] public required Output<Slot.Image> StartImage { get; init; }
    /// <summary>The i2v clip-vision cue.</summary>
    [JsonPropertyName("clip_vision_output")] public required Output<Slot.ClipVision> ClipVisionOutput { get; init; }
}

/// <summary>The t2v shape of the HunyuanVideo 1.5 SR node: the (positive, negative, latent) triple with NO source frame — no
/// start image, no clip-vision cue. Byte-identical to the i2v node with those two inputs absent. Output 0 = positive, 1 =
/// negative, 2 = latent.</summary>
public sealed record HunyuanVideo15SuperResolutionT2V : ComfyNode, IHunyuanVideo15SuperResolution
{
    internal override string ClassType => ComfyNodeTypes.HunyuanVideo15SuperResolution;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("latent")] public required Output<Slot.Latent> Latent { get; init; }
    [JsonPropertyName("noise_augmentation")] public required double NoiseAugmentation { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
}
