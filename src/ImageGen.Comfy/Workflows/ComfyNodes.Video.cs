using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>SD3-style flow-shift on a model (ComfyUI core) — the Wan/ChronoEdit sampling-shift node. Typed node records
/// for the self-contained VIDEO edit workflows; one record per ComfyUI class type, inputs declared in the exact order
/// the old anonymous-object inputs were written so the emitted graph is byte-identical.</summary>
public sealed record ModelSamplingSD3 : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ModelSamplingSD3;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("shift")] public required double Shift { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Rescales a model's rotary position embedding along x/y/t (ComfyUI core) — ChronoEdit's Wan RoPE fix-up.</summary>
public sealed record ScaleROPE : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ScaleROPE;
    [JsonPropertyName("model")]   public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("scale_x")] public required double ScaleX { get; init; }
    [JsonPropertyName("shift_x")] public required double ShiftX { get; init; }
    [JsonPropertyName("scale_y")] public required double ScaleY { get; init; }
    [JsonPropertyName("shift_y")] public required double ShiftY { get; init; }
    [JsonPropertyName("scale_t")] public required double ScaleT { get; init; }
    [JsonPropertyName("shift_t")] public required double ShiftT { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Clip-vision encoder loader (ComfyUI core) — the i2v vision tower (CLIP-ViT-H for Wan/ChronoEdit).</summary>
public sealed record CLIPVisionLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CLIPVisionLoader;
    [JsonPropertyName("clip_name")] public required string ClipName { get; init; }
    public static Output<Slot.ClipVision> Out(string id) => new(id, 0);
}

/// <summary>Encodes an image with a clip-vision tower (ComfyUI core). Its <c>CLIP_VISION_OUTPUT</c> is carried on the
/// same <see cref="Slot.ClipVision"/> marker as the loader's output (the graph only emits the <c>[id, idx]</c> edge).</summary>
public sealed record CLIPVisionEncode : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CLIPVisionEncode;
    [JsonPropertyName("clip_vision")] public required Output<Slot.ClipVision> ClipVision { get; init; }
    [JsonPropertyName("image")]       public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("crop")]        public required string Crop { get; init; }
    public static Output<Slot.ClipVision> Out(string id) => new(id, 0);
}

/// <summary>Wan 2.1 i2v conditioning (ComfyUI core) — bakes the start image + clip-vision into pos/neg conditioning and
/// the video latent. Output 0 = positive, 1 = negative, 2 = latent.</summary>
public sealed record WanImageToVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.WanImageToVideo;
    [JsonPropertyName("positive")]           public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")]           public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("vae")]                public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("clip_vision_output")] public required Output<Slot.ClipVision> ClipVisionOutput { get; init; }
    [JsonPropertyName("width")]              public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]             public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("length")]             public required int Length { get; init; }
    [JsonPropertyName("batch_size")]         public required int BatchSize { get; init; }
    [JsonPropertyName("start_image")]        public required Output<Slot.Image> StartImage { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

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

/// <summary>Picks a single frame out of an image batch (ComfyUI core) — ChronoEdit keeps the LAST trajectory frame.</summary>
public sealed record ImageFromBatch : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageFromBatch;
    [JsonPropertyName("image")]       public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("batch_index")] public required int BatchIndex { get; init; }
    [JsonPropertyName("length")]      public required int Length { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Saves frames as an animated WEBP with a LITERAL playback rate. Same ComfyUI class type as
/// <see cref="SaveAnimatedWEBP"/>, but <c>fps</c> is a fixed number (the config's frame rate) rather than a wired
/// <see cref="Output{TSlot}"/> stream rate, so it is a distinct record — they serialize <c>fps</c> as a JSON number vs
/// an <c>["nodeId", idx]</c> edge, and the emitted graph stays byte-identical to the old anonymous node.</summary>
public sealed record SaveAnimatedWEBPLiteralFps : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SaveAnimatedWEBP;
    [JsonPropertyName("images")]          public required Output<Slot.Image> Images { get; init; }
    [JsonPropertyName("filename_prefix")] public required string FilenamePrefix { get; init; }
    [JsonPropertyName("fps")]             public required double Fps { get; init; }
    [JsonPropertyName("lossless")]        public required bool Lossless { get; init; }
    [JsonPropertyName("quality")]         public required int Quality { get; init; }
    [JsonPropertyName("method")]          public required string Method { get; init; }
}

/// <summary>LTX-Video i2v conditioning (ComfyUI core) — bakes the start image into pos/neg conditioning and the video
/// latent. Output 0 = positive, 1 = negative, 2 = latent.</summary>
public sealed record LTXVImgToVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LTXVImgToVideo;
    [JsonPropertyName("positive")]   public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")]   public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("vae")]        public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("image")]      public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("width")]      public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]     public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("length")]     public required int Length { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("strength")]   public required double Strength { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

/// <summary>Applies the LTX frame-rate to a pos/neg conditioning pair (ComfyUI core). Output 0 = positive, 1 = negative.</summary>
public sealed record LTXVConditioning : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LTXVConditioning;
    [JsonPropertyName("positive")]   public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")]   public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("frame_rate")] public required double FrameRate { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
}

/// <summary>Builds the LTX sigma schedule (ComfyUI core) for the <c>SamplerCustom</c> path. Output 0 = sigmas.</summary>
public sealed record LTXVScheduler : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LTXVScheduler;
    [JsonPropertyName("steps")]      public required int Steps { get; init; }
    [JsonPropertyName("max_shift")]  public required double MaxShift { get; init; }
    [JsonPropertyName("base_shift")] public required double BaseShift { get; init; }
    [JsonPropertyName("stretch")]    public required bool Stretch { get; init; }
    [JsonPropertyName("terminal")]   public required double Terminal { get; init; }
    [JsonPropertyName("latent")]     public required Output<Slot.Latent> Latent { get; init; }
    public static Output<Slot.Sigmas> Out(string id) => new(id, 0);
}
