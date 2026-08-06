using System.Text.Json.Serialization;
using ImageGen.Domain.CodeAnalysis;

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
/// derives from it (wired width/height from a <see cref="GetImageSize"/> on the scaled source), with NO end frame. The
/// first/last-frame loop is its own record (<see cref="MiniMaxH3FirstLastFrameToVideo"/>) rather than an optional
/// nullable <c>last_frame</c> on this one (audit #125 A′). Output 0 = positive conditioning, 1 = video latent.</summary>
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
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}

/// <summary>MiniMax-H3's conditioning+latent node for a FIRST/LAST-frame loop: the same i2v node as
/// <see cref="MiniMaxH3ImageToVideoI2V"/> plus a REQUIRED <c>last_frame</c> pinning the ending. Same ComfyUI class type;
/// a distinct record so the end frame is never a conditional-nullable input. Output 0 = positive, 1 = video latent.</summary>
public sealed record MiniMaxH3FirstLastFrameToVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MiniMaxH3ImageToVideo;
    [JsonPropertyName("clip")]        public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("vae")]         public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("prompt")]      public required string Prompt { get; init; }
    [JsonPropertyName("length")]      public required int Length { get; init; }
    [JsonPropertyName("width")]       public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]      public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("first_frame")] public required Output<Slot.Image> FirstFrame { get; init; }
    [JsonPropertyName("last_frame")]  public required Output<Slot.Image> LastFrame { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 1);
}

/// <summary>MiniMax-H3's conditioning+latent node in REFERENCE→video mode (ref2va): the prompt plus one or more
/// reference images that condition the SUBJECT/IDENTITY (not a first frame), emitting the same (positive, joint
/// video+audio latent) as the i2v/t2v node. The node resizes each reference internally (down only, per
/// <c>ref_image_size</c>) and injects them as reference conditioning that rides every sampling step.
///
/// <para>The references are the node's <c>COMFY_AUTOGROW_V3</c> input: on the wire each one is a FLAT, DOTTED key
/// <c>ref_images.ref_image_{i}</c> at the top of the node's <c>inputs</c> (ComfyUI re-nests them into the
/// <c>ref_images</c> dict server-side via <c>build_nested_inputs</c>), so they are emitted through
/// <see cref="JsonExtensionDataAttribute"/> rather than a fixed property per slot. Unlike the i2v/t2v node this one
/// also takes the audio VAE directly. Output 0 = positive conditioning, 1 = video+audio latent.</para></summary>
public sealed record MiniMaxH3ReferenceToVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MiniMaxH3ReferenceToVideo;
    [JsonPropertyName("clip")]           public required Output<Slot.Clip> Clip { get; init; }
    [JsonPropertyName("vae")]            public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("audio_vae")]      public required Output<Slot.Vae> AudioVae { get; init; }
    [JsonPropertyName("prompt")]         public required string Prompt { get; init; }
    [JsonPropertyName("length")]         public required int Length { get; init; }
    [JsonPropertyName("width")]          public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]         public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("ref_image_size")] public required string RefImageSize { get; init; }

    /// <summary>The autogrow reference images, keyed by the exact flat dotted wire key <c>ref_images.ref_image_{i}</c>
    /// and serialized as sibling inputs via STJ extension data. Populated by <see cref="Refs"/>.</summary>
    [JsonExtensionData] public Dictionary<string, object> RefImages { get; init; } = new();

    /// <summary>Build the <c>ref_images.ref_image_{i}</c> extension-data map from the ordered reference outputs. Each
    /// value is the two-element <c>[nodeId, index]</c> edge ComfyUI expects — byte-identical to an
    /// <see cref="Output{TSlot}"/> edge, but keyed dynamically since the count is not known at compile time.</summary>
    public static Dictionary<string, object> Refs(IReadOnlyList<Output<Slot.Image>> images)
    {
        Dictionary<string, object> map = new(images.Count);
        for (int i = 0; i < images.Count; i++)
            map[$"ref_images.ref_image_{i}"] = new object[] { images[i].NodeId, images[i].Index };
        return map;
    }

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
