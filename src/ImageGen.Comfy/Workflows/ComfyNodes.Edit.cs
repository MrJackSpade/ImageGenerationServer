using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>The self-contained DreamOmni2 pipeline node (loads the int8 Kontext base + Qwen2.5-VL VLM internally). It
/// takes no inputs; its single output is the opaque pipeline handle the editor consumes (typed as a model handle, the
/// same way SeedVR2's DiT handle is). One typed record per ComfyUI class type; inputs are declared in the exact order
/// the old anonymous-object inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record RunningHubDreamOmni2EditPipeline : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RunningHubDreamOmni2EditPipeline;
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>The DreamOmni2 editor: drives the pipeline over a source + reference image and an instruction.</summary>
public sealed record RunningHubDreamOmni2Editor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RunningHubDreamOmni2Editor;
    [JsonPropertyName("pipeline")]            public required Output<Slot.Model> Pipeline { get; init; }
    [JsonPropertyName("src_image")]           public required Output<Slot.Image> SrcImage { get; init; }
    [JsonPropertyName("ref_image")]           public required Output<Slot.Image> RefImage { get; init; }
    [JsonPropertyName("prompt")]              public required string Prompt { get; init; }
    [JsonPropertyName("num_inference_steps")] public required int NumInferenceSteps { get; init; }
    [JsonPropertyName("guidance_scale")]      public required double GuidanceScale { get; init; }
    [JsonPropertyName("seed")]                public required long Seed { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Step1X-Edit's self-contained loader (DiT fp8 + Flux AE + Qwen2.5-VL, int8-quantized + offloaded). The text
/// encoder is a Hugging Face folder name the node loads from its own directory, so it is a literal, not a bound file.</summary>
public sealed record Step1XEditModelLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Step1XEditModelLoader;
    [JsonPropertyName("diffusion_model")] public required string DiffusionModel { get; init; }
    [JsonPropertyName("vae")]             public required string Vae { get; init; }
    [JsonPropertyName("text_encoder")]    public required string TextEncoder { get; init; }
    [JsonPropertyName("dtype")]           public required string Dtype { get; init; }
    [JsonPropertyName("quantized")]       public required bool Quantized { get; init; }
    [JsonPropertyName("offload")]         public required bool Offload { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Step1X-Edit generation node — instruction edit over the input image at a target size level.</summary>
public sealed record Step1XEditGenerate : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Step1XEditGenerate;
    [JsonPropertyName("model")]           public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("input_image")]     public required Output<Slot.Image> InputImage { get; init; }
    [JsonPropertyName("prompt")]          public required string Prompt { get; init; }
    [JsonPropertyName("negative_prompt")] public required string NegativePrompt { get; init; }
    [JsonPropertyName("num_steps")]       public required int NumSteps { get; init; }
    [JsonPropertyName("cfg_guidance")]    public required double CfgGuidance { get; init; }
    [JsonPropertyName("seed")]            public required long Seed { get; init; }
    [JsonPropertyName("size_level")]      public required int SizeLevel { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Scale an image to a total pixel budget on a stride grid (ComfyUI core) — Boogu's ~1 MP reference resize.</summary>
public sealed record ImageScaleToTotalPixels : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScaleToTotalPixels;
    [JsonPropertyName("image")]            public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")]   public required string UpscaleMethod { get; init; }
    [JsonPropertyName("megapixels")]       public required double Megapixels { get; init; }
    [JsonPropertyName("resolution_steps")] public required int ResolutionSteps { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Boogu's one-node edit conditioning (ComfyUI core <c>TextEncodeBooguEdit</c>): caps the reference to the
/// VLM's 384px input, VAE-encodes a reference latent, and emits positive (out 0) + negative (out 1) with the reference
/// latent on both. The reference is the node's Autogrow input, keyed by its finalized dotted path
/// <c>images.image_1</c> — a bare <c>image_1</c> is rejected, so the JSON name keeps the dot verbatim.</summary>
public sealed record TextEncodeBooguEdit : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.TextEncodeBooguEdit;
    [JsonPropertyName("clip")]            public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("prompt")]          public required string Prompt { get; init; }
    [JsonPropertyName("negative_prompt")] public required string NegativePrompt { get; init; }
    [JsonPropertyName("vae")]             public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("images.image_1")]  public required Output<Slot.Image> ImagesImage1 { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
}

/// <summary>An empty latent whose width/height are WIRED from another node's int outputs (e.g. <see cref="GetImageSize"/>).
/// Same ComfyUI class type as <see cref="EmptyLatent"/>, but its dimensions are edges rather than literals, so it is a
/// distinct record — they serialize as <c>["nodeId", idx]</c> to keep the emitted graph byte-identical.</summary>
public sealed record EmptyLatentFromSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyLatentImage;
    [JsonPropertyName("width")]      public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]     public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Selects a sampler by name (ComfyUI core) for the <c>SamplerCustom</c> path.</summary>
public sealed record KSamplerSelect : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.KSamplerSelect;
    [JsonPropertyName("sampler_name")] public required string SamplerName { get; init; }
    public static Output<Slot.Sampler> Out(string id) => new(id, 0);
}

/// <summary>Builds the sigma schedule from a model + scheduler (ComfyUI core) for the <c>SamplerCustom</c> path.</summary>
public sealed record BasicScheduler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.BasicScheduler;
    [JsonPropertyName("model")]     public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("scheduler")] public required string Scheduler { get; init; }
    [JsonPropertyName("steps")]     public required int Steps { get; init; }
    [JsonPropertyName("denoise")]   public required double Denoise { get; init; }
    public static Output<Slot.Sigmas> Out(string id) => new(id, 0);
}

/// <summary>The explicit-schedule sampler (ComfyUI core) — takes a selected sampler + sigma schedule rather than the
/// name-and-scheduler pair <see cref="KSampler"/> bundles.</summary>
public sealed record SamplerCustom : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SamplerCustom;
    [JsonPropertyName("model")]        public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("add_noise")]    public required bool AddNoise { get; init; }
    [JsonPropertyName("noise_seed")]   public required long NoiseSeed { get; init; }
    [JsonPropertyName("cfg")]          public required double Cfg { get; init; }
    [JsonPropertyName("positive")]     public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")]     public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("sampler")]      public required Output<Slot.Sampler> Sampler { get; init; }
    [JsonPropertyName("sigmas")]       public required Output<Slot.Sigmas> Sigmas { get; init; }
    [JsonPropertyName("latent_image")] public required Output<Slot.Latent> LatentImage { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Snaps an image to Flux.1 Kontext's supported edit resolution (ComfyUI core).</summary>
public sealed record FluxKontextImageScale : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.FluxKontextImageScale;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Stitches two images side by side (ComfyUI core) — the verified Kontext multi-reference method.</summary>
public sealed record ImageStitch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageStitch;
    [JsonPropertyName("image1")]           public required Output<Slot.Image> Image1 { get; init; }
    [JsonPropertyName("image2")]           public required Output<Slot.Image> Image2 { get; init; }
    [JsonPropertyName("direction")]        public required string Direction { get; init; }
    [JsonPropertyName("match_image_size")] public required bool MatchImageSize { get; init; }
    [JsonPropertyName("spacing_width")]    public required int SpacingWidth { get; init; }
    [JsonPropertyName("spacing_color")]    public required string SpacingColor { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Attaches a reference latent to a conditioning (ComfyUI core) — Kontext's identity anchor.</summary>
public sealed record ReferenceLatent : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ReferenceLatent;
    [JsonPropertyName("conditioning")] public required Output<Slot.Conditioning> Conditioning { get; init; }
    [JsonPropertyName("latent")]       public required Output<Slot.Latent> Latent { get; init; }
    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}

/// <summary>Zeroes out a conditioning (ComfyUI core) — the empty negative for Kontext's distilled guidance.</summary>
public sealed record ConditioningZeroOut : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ConditioningZeroOut;
    [JsonPropertyName("conditioning")] public required Output<Slot.Conditioning> Conditioning { get; init; }
    public static Output<Slot.Conditioning> Out(string id) => new(id, 0);
}
