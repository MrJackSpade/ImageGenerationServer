using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Empty latent whose width/height are WIRED from another node (here <see cref="GetImageSize"/>) rather than
/// literal ints. Same ComfyUI class type as <see cref="EmptyLatent"/>, but its <c>width</c>/<c>height</c> are
/// <see cref="Output{TSlot}"/> edges, so it is a distinct record — they serialize as <c>["nodeId", idx]</c> arrays vs
/// the literal record's JSON numbers. Typed node records for the self-contained AnimateDiff i2v workflows; one record
/// per ComfyUI class type, inputs declared in the exact order the old anonymous-object inputs were written so the
/// emitted graph is byte-identical.</summary>
public sealed record EmptyLatentImageSized : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyLatentImage;
    [JsonPropertyName("width")]      public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]     public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>AnimateDiff-Evolved motion-module loader (custom node <c>ADE_LoadAnimateDiffModel</c>) — loads the named
/// motion module. Its handle rides on the <see cref="Slot.Model"/> marker (the graph only emits the edge).</summary>
public sealed record ADE_LoadAnimateDiffModel : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ADE_LoadAnimateDiffModel;
    [JsonPropertyName("model_name")] public required string ModelName { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Wraps a loaded motion module into an <c>M_MODELS</c> handle (custom node
/// <c>ADE_ApplyAnimateDiffModelSimple</c>). The handle rides on the <see cref="Slot.Model"/> marker.</summary>
public sealed record ADE_ApplyAnimateDiffModelSimple : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ADE_ApplyAnimateDiffModelSimple;
    [JsonPropertyName("motion_model")] public required Output<Slot.Model> MotionModel { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Injects the motion module + beta schedule into the base model for AnimateDiff sampling (custom node
/// <c>ADE_UseEvolvedSampling</c>). Output 0 is the patched MODEL.</summary>
public sealed record ADE_UseEvolvedSampling : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ADE_UseEvolvedSampling;
    [JsonPropertyName("model")]         public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("beta_schedule")] public required string BetaSchedule { get; init; }
    [JsonPropertyName("m_models")]      public required Output<Slot.Model> MModels { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>IP-Adapter unified loader (custom node <c>IPAdapterUnifiedLoader</c>) — auto-resolves the IP-Adapter model +
/// CLIP-ViT-H from a preset. Output 0 = MODEL, output 1 = IPADAPTER (carried on the <see cref="Slot.ClipVision"/>
/// marker; only the <c>[id, idx]</c> edge is emitted).</summary>
public sealed record IPAdapterUnifiedLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.IPAdapterUnifiedLoader;
    [JsonPropertyName("model")]  public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("preset")] public required string Preset { get; init; }
    public static Output<Slot.Model> ModelOut(string id) => new(id, 0);
    public static Output<Slot.ClipVision> IpadapterOut(string id) => new(id, 1);
}

/// <summary>Applies an IP-Adapter to a model from a reference image (custom node <c>IPAdapter</c>) — locks the
/// subject's identity across frames. Output 0 is the patched MODEL.</summary>
public sealed record IPAdapter : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.IPAdapter;
    [JsonPropertyName("model")]       public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("ipadapter")]   public required Output<Slot.ClipVision> Ipadapter { get; init; }
    [JsonPropertyName("image")]       public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("weight")]      public required double Weight { get; init; }
    [JsonPropertyName("start_at")]    public required double StartAt { get; init; }
    [JsonPropertyName("end_at")]      public required double EndAt { get; init; }
    [JsonPropertyName("weight_type")] public required string WeightType { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Advanced-ControlNet SparseCtrl loader (custom node <c>ACN_SparseCtrlLoaderAdvanced</c>) — loads the
/// SparseCtrl-RGB controlnet. Output 0 rides on the <see cref="Slot.ControlNet"/> marker.</summary>
public sealed record ACN_SparseCtrlLoaderAdvanced : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ACN_SparseCtrlLoaderAdvanced;
    [JsonPropertyName("sparsectrl_name")] public required string SparsectrlName { get; init; }
    [JsonPropertyName("use_motion")]      public required bool UseMotion { get; init; }
    [JsonPropertyName("motion_strength")] public required double MotionStrength { get; init; }
    [JsonPropertyName("motion_scale")]    public required double MotionScale { get; init; }
    public static Output<Slot.ControlNet> Out(string id) => new(id, 0);
}

/// <summary>SparseCtrl RGB preprocessor (custom node <c>ACN_SparseCtrlRGBPreprocessor</c>) — turns the source image
/// into the SparseCtrl frame-0 conditioning image. Output 0 is an IMAGE.</summary>
public sealed record ACN_SparseCtrlRGBPreprocessor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ACN_SparseCtrlRGBPreprocessor;
    [JsonPropertyName("image")]       public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("vae")]         public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("latent_size")] public required Output<Slot.Latent> LatentSize { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Applies a controlnet to a pos/neg conditioning pair with start/end percents (ComfyUI core
/// <c>ControlNetApplyAdvanced</c>). Output 0 = positive, 1 = negative.</summary>
public sealed record ControlNetApplyAdvanced : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ControlNetApplyAdvanced;
    [JsonPropertyName("positive")]      public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")]      public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("control_net")]   public required Output<Slot.ControlNet> ControlNet { get; init; }
    [JsonPropertyName("image")]         public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("strength")]      public required double Strength { get; init; }
    [JsonPropertyName("start_percent")] public required double StartPercent { get; init; }
    [JsonPropertyName("end_percent")]   public required double EndPercent { get; init; }
    [JsonPropertyName("vae")]           public required Output<Slot.Vae> Vae { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
}

/// <summary>Repeats a latent into a batch (ComfyUI core <c>RepeatLatentBatch</c>) — SDXL AnimateDiff seeds every frame
/// from the img2img source latent. Output 0 is the batched LATENT.</summary>
public sealed record RepeatLatentBatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RepeatLatentBatch;
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("amount")]  public required int Amount { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}
