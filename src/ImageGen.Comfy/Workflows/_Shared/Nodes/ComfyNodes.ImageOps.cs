using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Reads an image's pixel dimensions (outputs 0 = width, 1 = height as ints).</summary>
public sealed record GetImageSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.GetImageSize;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    public static Output<Slot.Int> WidthOut(string id) => new(id, 0);
    public static Output<Slot.Int> HeightOut(string id) => new(id, 1);
}

/// <summary>A solid-colour image (here a white background for alpha flattening). Width/height are wired from
/// <see cref="GetImageSize"/>.</summary>
public sealed record EmptyImage : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.EmptyImage;
    [JsonPropertyName("width")] public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")] public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("batch_size")] public required int BatchSize { get; init; }
    [JsonPropertyName("color")] public required int Color { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Inverts a mask.</summary>
public sealed record InvertMask : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.InvertMask;
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    public static Output<Slot.Mask> Out(string id) => new(id, 0);
}

/// <summary>Composite a source image onto a destination through a mask.</summary>
public sealed record ImageCompositeMasked : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageCompositeMasked;
    [JsonPropertyName("destination")] public required Output<Slot.Image> Destination { get; init; }
    [JsonPropertyName("source")] public required Output<Slot.Image> Source { get; init; }
    [JsonPropertyName("x")] public required int X { get; init; }
    [JsonPropertyName("y")] public required int Y { get; init; }
    [JsonPropertyName("resize_source")] public required bool ResizeSource { get; init; }
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Scale an image to a fixed width/height.</summary>
public sealed record ImageScale : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScale;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("crop")] public required string Crop { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
