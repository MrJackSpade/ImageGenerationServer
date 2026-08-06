using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Loads a source clip from ComfyUI's input folder. (Typed node record — one per ComfyUI class type; inputs are
/// declared in the exact order the old anonymous-object inputs were written, so the emitted graph is byte-identical.)</summary>
public sealed record LoadVideo : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LoadVideo;
    [JsonPropertyName("file")] public required string File { get; init; }
    public static Output<Slot.Video> VideoOut(string id) => new(id, 0);
}

/// <summary>Splits a clip into its component streams — output 0 the image frames, output 1 the audio track, output 2
/// the frame rate.</summary>
public sealed record GetVideoComponents : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.GetVideoComponents;
    [JsonPropertyName("video")] public required Output<Slot.Video> Video { get; init; }
    public static Output<Slot.Image> ImagesOut(string id) => new(id, 0);
    public static Output<AudioSlot> AudioOut(string id) => new(id, 1);
    public static Output<Slot.Float> FpsOut(string id) => new(id, 2);
}

/// <summary>Loads an audio clip from ComfyUI's input folder (core ComfyUI). Output 0 is the AUDIO waveform.</summary>
public sealed record LoadAudio : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LoadAudio;
    [JsonPropertyName("audio")] public required string Audio { get; init; }
    public static Output<AudioSlot> AudioOut(string id) => new(id, 0);
}

/// <summary>Automatic flicker/wash correction over a clip (PixelHarness).</summary>
public sealed record DeflickerAuto : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.DeflickerAuto;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("mad_k")] public required double MadK { get; init; }
    [JsonPropertyName("min_dev")] public required double MinDev { get; init; }
    [JsonPropertyName("alpha_cut")] public required double AlphaCut { get; init; }
    [JsonPropertyName("time_sigma")] public required double TimeSigma { get; init; }
    public static Output<Slot.Image> ImageOut(string id) => new(id, 0);
}

/// <summary>Saves frames as an animated WEBP.</summary>
public sealed record SaveAnimatedWEBP : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SaveAnimatedWEBP;
    [JsonPropertyName("images")] public required Output<Slot.Image> Images { get; init; }
    [JsonPropertyName("filename_prefix")] public required string FilenamePrefix { get; init; }
    [JsonPropertyName("fps")] public required Output<Slot.Float> Fps { get; init; }
    [JsonPropertyName("lossless")] public required bool Lossless { get; init; }
    [JsonPropertyName("quality")] public required int Quality { get; init; }
    [JsonPropertyName("method")] public required string Method { get; init; }
}
