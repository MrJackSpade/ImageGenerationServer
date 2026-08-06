using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>All-in-one checkpoint loader (model + CLIP + VAE). One typed record per ComfyUI class type; inputs are
/// declared in the exact order the old anonymous-object inputs were written, so the emitted graph is byte-identical.
/// Wired inputs are slot-typed <see cref="Output{TSlot}"/>; outputs are exposed as static slot accessors.</summary>
public sealed record CheckpointLoaderSimple : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CheckpointLoaderSimple;
    [JsonPropertyName("ckpt_name")] public required string CkptName { get; init; }
    public static Output<Slot.Model> ModelOut(string id) => new(id, 0);
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 1);
    public static Output<Slot.Vae> VaeOut(string id) => new(id, 2);
}

/// <summary>Diffusion-only UNet loader (safetensors).</summary>
public sealed record UNETLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.UNETLoader;
    [JsonPropertyName("unet_name")] public required string UnetName { get; init; }
    [JsonPropertyName("weight_dtype")] public required string WeightDtype { get; init; }
    public static Output<Slot.Model> ModelOut(string id) => new(id, 0);
}

/// <summary>Diffusion-only UNet loader (GGUF quant).</summary>
public sealed record UnetLoaderGGUF : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.UnetLoaderGGUF;
    [JsonPropertyName("unet_name")] public required string UnetName { get; init; }
    public static Output<Slot.Model> ModelOut(string id) => new(id, 0);
}

/// <summary>VAE loader.</summary>
public sealed record VAELoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.VAELoader;
    [JsonPropertyName("vae_name")] public required string VaeName { get; init; }
    public static Output<Slot.Vae> VaeOut(string id) => new(id, 0);
}

/// <summary>Single text-encoder loader.</summary>
public sealed record CLIPLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CLIPLoader;
    [JsonPropertyName("clip_name")] public required string ClipName { get; init; }
    [JsonPropertyName("type")] public required string? Type { get; init; }
    [JsonPropertyName("device")] public required string Device { get; init; }
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 0);
}

/// <summary>Single text-encoder loader (GGUF).</summary>
public sealed record CLIPLoaderGGUF : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CLIPLoaderGGUF;
    [JsonPropertyName("clip_name")] public required string ClipName { get; init; }
    [JsonPropertyName("type")] public required string? Type { get; init; }
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 0);
}

/// <summary>Two-encoder loader (SDXL/Flux/etc).</summary>
public sealed record DualCLIPLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.DualCLIPLoader;
    [JsonPropertyName("clip_name1")] public required string ClipName1 { get; init; }
    [JsonPropertyName("clip_name2")] public required string ClipName2 { get; init; }
    [JsonPropertyName("type")] public required string? Type { get; init; }
    [JsonPropertyName("device")] public required string Device { get; init; }
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 0);
}

/// <summary>Three-encoder loader (SD3.5).</summary>
public sealed record TripleCLIPLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.TripleCLIPLoader;
    [JsonPropertyName("clip_name1")] public required string ClipName1 { get; init; }
    [JsonPropertyName("clip_name2")] public required string ClipName2 { get; init; }
    [JsonPropertyName("clip_name3")] public required string ClipName3 { get; init; }
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 0);
}

/// <summary>Four-encoder loader.</summary>
public sealed record QuadrupleCLIPLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.QuadrupleCLIPLoader;
    [JsonPropertyName("clip_name1")] public required string ClipName1 { get; init; }
    [JsonPropertyName("clip_name2")] public required string ClipName2 { get; init; }
    [JsonPropertyName("clip_name3")] public required string ClipName3 { get; init; }
    [JsonPropertyName("clip_name4")] public required string ClipName4 { get; init; }
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 0);
}

/// <summary>Clip-skip (stop at a CLIP layer).</summary>
public sealed record CLIPSetLastLayer : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CLIPSetLastLayer;
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("stop_at_clip_layer")] public required int StopAtClipLayer { get; init; }
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 0);
}

/// <summary>Text → conditioning.</summary>
public sealed record CLIPTextEncode : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CLIPTextEncode;
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}

/// <summary>FLUX guidance embedding on a conditioning.</summary>
public sealed record FluxGuidance : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.FluxGuidance;
    [JsonPropertyName("conditioning")] public required Output<Slot.Conditioning> Conditioning { get; init; }
    [JsonPropertyName("guidance")] public required double Guidance { get; init; }
    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}

/// <summary>AuraFlow model-sampling shift.</summary>
public sealed record ModelSamplingAuraFlow : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ModelSamplingAuraFlow;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("shift")] public required double Shift { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Empty latent (class type varies by model family: EmptyLatentImage / EmptySD3LatentImage /
/// EmptyFlux2LatentImage / EmptyChromaRadianceLatentImage — all one input shape).</summary>
public sealed record EmptyLatent : ComfyNode
{
    private readonly string _classType;
    public EmptyLatent(string classType) => _classType = classType;
    internal override string ClassType => _classType;
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>The standard sampler.</summary>
public sealed record KSampler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.KSampler;
    [JsonPropertyName("seed")] public required long Seed { get; init; }
    [JsonPropertyName("steps")] public required int Steps { get; init; }
    [JsonPropertyName("cfg")] public required double Cfg { get; init; }
    [JsonPropertyName("sampler_name")] public required string SamplerName { get; init; }
    [JsonPropertyName("scheduler")] public required string Scheduler { get; init; }
    [JsonPropertyName("denoise")] public required double Denoise { get; init; }
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("latent_image")] public required Output<Slot.Latent> LatentImage { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Latent → image.</summary>
public sealed record VAEDecode : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.VAEDecode;
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Image → latent.</summary>
public sealed record VAEEncode : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.VAEEncode;
    [JsonPropertyName("pixels")] public required Output<Slot.Image> Pixels { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Save a still image.</summary>
public sealed record SaveImage : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SaveImage;
    [JsonPropertyName("images")] public required Output<Slot.Image> Images { get; init; }
    [JsonPropertyName("filename_prefix")] public required string FilenamePrefix { get; init; }
}

/// <summary>Load a source image from ComfyUI's input folder.</summary>
public sealed record LoadImage : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LoadImage;
    [JsonPropertyName("image")] public required string Image { get; init; }
    public static Output<Slot.Image> ImageOut(string id) => new(id, 0);
    public static Output<Slot.Mask> MaskOut(string id) => new(id, 1);
}

/// <summary>Model-only LoRA (preset LoRA on the base model).</summary>
public sealed record LoraLoaderModelOnly : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LoraLoaderModelOnly;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("lora_name")] public required string LoraName { get; init; }
    [JsonPropertyName("strength_model")] public required double StrengthModel { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Model + CLIP LoRA (user LoRA stack).</summary>
public sealed record LoraLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LoraLoader;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("clip")] public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("lora_name")] public required string LoraName { get; init; }
    [JsonPropertyName("strength_model")] public required double StrengthModel { get; init; }
    [JsonPropertyName("strength_clip")] public required double StrengthClip { get; init; }
    public static Output<Slot.Model> ModelOut(string id) => new(id, 0);
    public static Output<Slot.Clip> ClipOut(string id) => new(id, 1);
}
