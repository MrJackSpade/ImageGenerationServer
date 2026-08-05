using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Grayscale morphological line thickener (ComfyUI-PixelHarness) — grows dark pixels by <c>thickness</c>
/// iterations of a 3×3 minimum filter. One typed record per ComfyUI class type; inputs are declared in the exact order
/// the old anonymous-object inputs were written, so the emitted graph is byte-identical.</summary>
public sealed record LineThicken : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LineThicken;
    [JsonPropertyName("image")]     public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("thickness")] public required int Thickness { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>sketchKeras line extractor (ComfyUI-PixelHarness) — dark-on-white line art at the input size.</summary>
public sealed record SketchKerasLines : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SketchKerasLines;
    [JsonPropertyName("image")]     public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("threshold")] public required double Threshold { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Anime line-art network (comfyui_controlnet_aux) — white-on-black line art at the detector resolution.</summary>
public sealed record AnimeLineArtPreprocessor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.AnimeLineArtPreprocessor;
    [JsonPropertyName("image")]      public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("resolution")] public required int Resolution { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Inverts an image (ComfyUI core) — here white-on-black line art to dark-lines-on-white.</summary>
public sealed record ImageInvert : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageInvert;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Scale an image to width/height WIRED from another node's outputs (e.g. <see cref="GetImageSize"/>). Same
/// ComfyUI class type as <see cref="ImageScale"/>, but its width/height are edges rather than literals, so it is a
/// distinct record — the dimensions serialize as <c>["nodeId", idx]</c> to keep the emitted graph byte-identical.</summary>
public sealed record ImageScaleToImageSize : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageScale;
    [JsonPropertyName("image")]          public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("upscale_method")] public required string UpscaleMethod { get; init; }
    [JsonPropertyName("width")]          public required Output<Slot.Int> Width { get; init; }
    [JsonPropertyName("height")]         public required Output<Slot.Int> Height { get; init; }
    [JsonPropertyName("crop")]           public required string Crop { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>XDoG (extended difference-of-Gaussians) outline extractor (ComfyUI-PixelHarness) — pulls existing outlines
/// out as dark-lines-on-white.</summary>
public sealed record XDoGLines : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.XDoGLines;
    [JsonPropertyName("image")]   public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("sigma")]   public required double Sigma { get; init; }
    [JsonPropertyName("k")]       public required double K { get; init; }
    [JsonPropertyName("tau")]     public required double Tau { get; init; }
    [JsonPropertyName("epsilon")] public required double Epsilon { get; init; }
    [JsonPropertyName("phi")]     public required double Phi { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Blend two images by a factor and mode (ComfyUI core) — here a multiply composite of a bolded line layer
/// over the source.</summary>
public sealed record ImageBlend : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageBlend;
    [JsonPropertyName("image1")]       public required Output<Slot.Image> Image1 { get; init; }
    [JsonPropertyName("image2")]       public required Output<Slot.Image> Image2 { get; init; }
    [JsonPropertyName("blend_factor")] public required double BlendFactor { get; init; }
    [JsonPropertyName("blend_mode")]   public required string BlendMode { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
