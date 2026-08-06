using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Grows (dilates) a mask by <c>expand</c> pixels (ComfyUI core). One typed record per ComfyUI class type;
/// inputs are declared in the exact order the old anonymous-object inputs were written, so the emitted graph is
/// byte-identical.</summary>
public sealed record GrowMask : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.GrowMask;
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    [JsonPropertyName("expand")] public required int Expand { get; init; }
    [JsonPropertyName("tapered_corners")] public required bool TaperedCorners { get; init; }
    public static Output<Slot.Mask> Out(string id) => new(id, 0);
}

/// <summary>Renders a mask to a greyscale image (ComfyUI core) — the first leg of the IMAGE round-trip that blurs a
/// mask's own boundary (no MASK-space node does).</summary>
public sealed record MaskToImage : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MaskToImage;
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Gaussian image blur (ComfyUI core) — used to soften a mask that has been round-tripped through IMAGE.</summary>
public sealed record ImageBlur : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageBlur;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("blur_radius")] public required int BlurRadius { get; init; }
    [JsonPropertyName("sigma")] public required double Sigma { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Reads one channel of an image back into a mask (ComfyUI core) — the return leg of the IMAGE round-trip.</summary>
public sealed record ImageToMask : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageToMask;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("channel")] public required string Channel { get; init; }
    public static Output<Slot.Mask> Out(string id) => new(id, 0);
}

/// <summary>Composites one mask onto another with an operation (ComfyUI core). The <c>add</c> operation plus the node's
/// 0..1 clamp restores a hard 1 over the raw fill region, making a blurred ramp one-sided.</summary>
public sealed record MaskComposite : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.MaskComposite;
    [JsonPropertyName("destination")] public required Output<Slot.Mask> Destination { get; init; }
    [JsonPropertyName("source")] public required Output<Slot.Mask> Source { get; init; }
    [JsonPropertyName("x")] public required int X { get; init; }
    [JsonPropertyName("y")] public required int Y { get; init; }
    [JsonPropertyName("operation")] public required string Operation { get; init; }
    public static Output<Slot.Mask> Out(string id) => new(id, 0);
}

/// <summary>Loads a mask from an image file's channel (ComfyUI core) — the white-on-black region painted in the edit
/// UI. Its single output (slot 0) is the MASK.</summary>
public sealed record LoadImageMask : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.LoadImageMask;
    [JsonPropertyName("image")] public required string Image { get; init; }
    [JsonPropertyName("channel")] public required string Channel { get; init; }
    public static Output<Slot.Mask> Out(string id) => new(id, 0);
}

/// <summary>Pads an image for outpainting (ComfyUI core): returns the enlarged canvas (out 0, IMAGE) and a mask marking
/// the added border (out 1, MASK).</summary>
public sealed record ImagePadForOutpaint : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImagePadForOutpaint;
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("left")] public required int Left { get; init; }
    [JsonPropertyName("top")] public required int Top { get; init; }
    [JsonPropertyName("right")] public required int Right { get; init; }
    [JsonPropertyName("bottom")] public required int Bottom { get; init; }
    [JsonPropertyName("feathering")] public required int Feathering { get; init; }
    public static Output<Slot.Image> ImageOut(string id) => new(id, 0);
    public static Output<Slot.Mask> MaskOut(string id) => new(id, 1);
}

/// <summary>FLUX Fill's native fill conditioning (ComfyUI core): blanks the masked region to grey, VAE-encodes it as
/// the concat latent, and returns the adjusted positive (out 0), negative (out 1) and the sampler's latent (out 2).</summary>
public sealed record InpaintModelConditioning : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.InpaintModelConditioning;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("pixels")] public required Output<Slot.Image> Pixels { get; init; }
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    [JsonPropertyName("noise_mask")] public required bool NoiseMask { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
    public static Output<Slot.Latent> LatentOut(string id) => new(id, 2);
}

/// <summary>Turns a soft mask into a per-pixel denoise schedule (ComfyUI core) — the seam mechanism on the FLUX Fill
/// path; harmonizes the transition band across steps instead of cross-fading two finished images.</summary>
public sealed record DifferentialDiffusion : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.DifferentialDiffusion;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}

/// <summary>Our fork's paste-back node (adapted from SwarmUI, MIT): composites a source onto a destination through a
/// mask, fitting and inverting the decode's tint on the outside-mask pixels first (<c>correction_method</c>).</summary>
public sealed record ImageCompositeMaskedColorCorrected : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageCompositeMaskedColorCorrected;
    [JsonPropertyName("destination")] public required Output<Slot.Image> Destination { get; init; }
    [JsonPropertyName("source")] public required Output<Slot.Image> Source { get; init; }
    [JsonPropertyName("x")] public required int X { get; init; }
    [JsonPropertyName("y")] public required int Y { get; init; }
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    [JsonPropertyName("correction_method")] public required string CorrectionMethod { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Composites a source onto a destination WITHOUT a mask (ComfyUI core <c>ImageCompositeMasked</c> with the
/// optional <c>mask</c> input omitted) — the outpaint pre-fill paste of the original over the blurred scaffold. A
/// distinct record from <see cref="ImageCompositeMasked"/> because it emits five inputs (no <c>mask</c> key), so the
/// graph stays byte-identical to the hand-built dictionary that left the key out.</summary>
public sealed record ImageCompositeMaskedNoMask : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ImageCompositeMasked;
    [JsonPropertyName("destination")] public required Output<Slot.Image> Destination { get; init; }
    [JsonPropertyName("source")] public required Output<Slot.Image> Source { get; init; }
    [JsonPropertyName("x")] public required int X { get; init; }
    [JsonPropertyName("y")] public required int Y { get; init; }
    [JsonPropertyName("resize_source")] public required bool ResizeSource { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}

/// <summary>Loads an inpainting ControlNet (ComfyUI core). Its single output (slot 0) is the CONTROL_NET.</summary>
public sealed record ControlNetLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ControlNetLoader;
    [JsonPropertyName("control_net_name")] public required string ControlNetName { get; init; }
    public static Output<Slot.ControlNet> Out(string id) => new(id, 0);
}

/// <summary>Applies the AliMama-style inpainting ControlNet (ComfyUI core, reused for Qwen-Image InstantX): it inverts
/// the mask, zeroes the RGB inside it, and returns the conditioned positive (out 0) and negative (out 1).</summary>
public sealed record ControlNetInpaintingAliMamaApply : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.ControlNetInpaintingAliMamaApply;
    [JsonPropertyName("positive")] public required Output<Slot.Conditioning> Positive { get; init; }
    [JsonPropertyName("negative")] public required Output<Slot.Conditioning> Negative { get; init; }
    [JsonPropertyName("control_net")] public required Output<Slot.ControlNet> ControlNet { get; init; }
    [JsonPropertyName("vae")] public required Output<Slot.Vae> Vae { get; init; }
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    [JsonPropertyName("strength")] public required double Strength { get; init; }
    [JsonPropertyName("start_percent")] public required double StartPercent { get; init; }
    [JsonPropertyName("end_percent")] public required double EndPercent { get; init; }
    public static Output<Slot.Conditioning> PositiveOut(string id) => new(id, 0);
    public static Output<Slot.Conditioning> NegativeOut(string id) => new(id, 1);
}

/// <summary>Confines denoising to a mask by re-injecting the noised original latents outside it each step (ComfyUI
/// core) — the exposure anchor.</summary>
public sealed record SetLatentNoiseMask : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.SetLatentNoiseMask;
    [JsonPropertyName("samples")] public required Output<Slot.Latent> Samples { get; init; }
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    public static Output<Slot.Latent> Out(string id) => new(id, 0);
}

/// <summary>Patches an Anima model with the 4-channel inpainting ControlNet-LLLite (ComfyUI-Anima-LLLite custom node):
/// fed the padded RGB + border mask, it conditions the fill on the known pixels. Its single output (slot 0) is MODEL.</summary>
public sealed record AnimaLLLiteApply : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.AnimaLLLiteApply;
    [JsonPropertyName("model")] public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("lllite_name")] public required string LlliteName { get; init; }
    [JsonPropertyName("image")] public required Output<Slot.Image> Image { get; init; }
    [JsonPropertyName("mask")] public required Output<Slot.Mask> Mask { get; init; }
    [JsonPropertyName("strength")] public required double Strength { get; init; }
    [JsonPropertyName("start_percent")] public required double StartPercent { get; init; }
    [JsonPropertyName("end_percent")] public required double EndPercent { get; init; }
    [JsonPropertyName("preserve_wrapper")] public required bool PreserveWrapper { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
