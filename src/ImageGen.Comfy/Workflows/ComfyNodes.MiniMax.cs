using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Output-slot marker for a decoded AUDIO waveform (the <see cref="VAEDecodeAudio"/> → <see cref="CreateVideo"/>
/// <c>audio</c> edge). MiniMax-H3 is the only audio-carrying graph and <see cref="Slot"/> (in the shared
/// ComfyGraphTypes) declares no audio kind, so this phantom marker lives beside the only nodes that use it. Never
/// instantiated — it only types an <see cref="Output{TSlot}"/> edge so an audio wire cannot be plugged into an image
/// socket at compile time.</summary>
public sealed class AudioSlot { private AudioSlot() { } }

/// <summary>MiniMax-H3's conditioning+latent node in TEXT→video mode: the prompt is encoded by this node itself (no
/// separate CLIPTextEncode) and the clip size is LITERAL width/height from the aspect map. Same ComfyUI class type as
/// the i2v variant (<see cref="MiniMaxH3ImageToVideoI2V"/>) but a distinct record — t2v emits width/height as JSON
/// numbers with no frame inputs, i2v emits them as <c>["nodeId", idx]</c> edges plus first/last frame — so the emitted
/// graph stays byte-identical to the old hand-built dictionary. Output 0 = positive conditioning, 1 = video latent.</summary>
public sealed record MiniMaxH3ImageToVideoT2V : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MiniMaxH3ImageToVideo;
    [JsonPropertyName("clip")]   public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("vae")]    public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("width")]  public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}

/// <summary>MiniMax-H3's conditioning+latent node in IMAGE→video mode: the source is the first frame and the clip size
/// derives from it (wired width/height from a <see cref="GetImageSize"/> on the scaled source). An optional
/// <c>last_frame</c> pins the ending; it is omitted from the emitted inputs when absent (WhenWritingNull) so the graph
/// stays byte-identical to the old dictionary that only added the key when an end frame was supplied. Output 0 =
/// positive conditioning, 1 = video latent.</summary>
public sealed record MiniMaxH3ImageToVideoI2V : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MiniMaxH3ImageToVideo;
    [JsonPropertyName("clip")]        public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("vae")]         public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("prompt")]      public required string Prompt { get; init; }
    [JsonPropertyName("length")]      public required int Length { get; init; }
    [JsonPropertyName("width")]       public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]      public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("first_frame")] public required Output<Slot.Image> FirstFrame { get; init; }
    [JsonPropertyName("last_frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Output<Slot.Image>? LastFrame { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}

/// <summary>Decodes the SAME video latent to the native stereo audio track through the audio VAE (ComfyUI core). Output
/// 0 = the audio waveform, muxed with the frames by <see cref="CreateVideo"/>.</summary>
public sealed record VAEDecodeAudio : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.VAEDecodeAudio;
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("vae")]     public required Output<Slot.Vae> Vae { get; init; }
    public static Output<AudioSlot> Out(string id) => new(id, 0);
}

/// <summary>Muxes decoded frames + an audio track into a video at a literal fps (ComfyUI core). Output 0 = the video.</summary>
public sealed record CreateVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.CreateVideo;
    [JsonPropertyName("images")] public required Output<Slot.Image> Images { get; init; }
    [JsonPropertyName("fps")]    public required double Fps { get; init; }
    [JsonPropertyName("audio")]  public required Output<AudioSlot> Audio { get; init; }
    public static Output<Slot.Video> Out(string id) => new(id, 0);
}

/// <summary>Writes a real mp4 with a baked-in audio track (ComfyUI core) — H3's terminal node, in place of the silent
/// <c>SaveAnimatedWEBP</c>. <c>format</c>/<c>codec</c> "auto" resolve to mp4/h264+aac.</summary>
public sealed record SaveVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SaveVideo;
    [JsonPropertyName("video")]           public required Output<Slot.Video> Video { get; init; }
    [JsonPropertyName("filename_prefix")] public required string FilenamePrefix { get; init; }
    [JsonPropertyName("format")]          public required string Format { get; init; }
    [JsonPropertyName("codec")]           public required string Codec { get; init; }
}
