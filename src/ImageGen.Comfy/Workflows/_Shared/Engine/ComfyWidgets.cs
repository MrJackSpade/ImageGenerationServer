namespace ImageGen.Comfy;

/// <summary>The single source of truth for every ComfyUI node WIDGET VALUE this app emits — the string a graph
/// assigns to a node's combo/toggle input (a scale method, a crop mode, a clip type, an enable/disable flag). Each
/// value is a token pinned by ComfyUI (or a custom-node pack) and re-used across many workflow graphs; writing it
/// once here keeps the schema declaration and every emit site on one spelling, exactly as <see cref="ComfyNodeTypes"/>
/// does for <c>class_type</c> and <see cref="WorkflowParamKeys"/> does for parameter keys. Grouped by the widget the
/// value belongs to, so a node input reads <c>UpscaleMethod = ComfyWidgets.Upscale.Lanczos</c>. These are wire
/// identifiers, never display text.</summary>
internal static class ComfyWidgets
{
    /// <summary>The <c>upscale_method</c> combo shared by ImageScale/ImageScaleBy/LatentUpscale/FluxKontextImageScale.</summary>
    internal static class Upscale
    {
        public const string Lanczos = "lanczos";
        public const string NearestExact = "nearest-exact";
        public const string Bilinear = "bilinear";
        public const string Bicubic = "bicubic";
        public const string Area = "area";
    }

    /// <summary>The <c>crop</c> combo on the scale nodes.</summary>
    internal static class Crop
    {
        public const string Center = "center";
        public const string Disabled = "disabled";
        public const string None = "none";
    }

    /// <summary>The <c>channel</c> combo (which colour plane a mask is read from) on ImageToMask.</summary>
    internal static class MaskChannel
    {
        public const string Red = "red";
    }

    /// <summary>The <c>blend_mode</c> combo on ImageBlend.</summary>
    internal static class Blend
    {
        public const string Multiply = "multiply";
    }

    /// <summary>The <c>operation</c> combo on MaskComposite.</summary>
    internal static class MaskOperation
    {
        public const string Add = "add";
    }

    /// <summary>An <c>enable</c>/<c>disable</c> toggle rendered as a combo (KSamplerAdvanced <c>add_noise</c> and
    /// <c>return_with_leftover_noise</c>).</summary>
    internal static class Toggle
    {
        public const string Enable = "enable";
        public const string Disable = "disable";
    }

    /// <summary>The <c>method</c> combo on SaveAnimatedWEBP (the webp encode quality method).</summary>
    internal static class WebpMethod
    {
        public const string Default = "default";
    }

    /// <summary>The <c>device</c> combo on CLIPLoader/CLIPLoaderGGUF.</summary>
    internal static class Device
    {
        public const string Default = "default";
    }

    /// <summary>The <c>direction</c> combo on ImageStitch.</summary>
    internal static class Stitch
    {
        public const string Right = "right";
    }

    /// <summary>The <c>spacing_color</c> combo on ImageStitch.</summary>
    internal static class Spacing
    {
        public const string White = "white";
    }

    /// <summary>The <c>type</c> combo on CLIPLoader/CLIPLoaderGGUF — the model family the text encoder is loaded for.</summary>
    internal static class ClipType
    {
        public const string Chroma = "chroma";
        public const string Wan = "wan";
        public const string HunyuanImage = "hunyuan_image";
        public const string HunyuanVideo = "hunyuan_video";
        public const string HunyuanVideo15 = "hunyuan_video_15";
        public const string Ideogram4 = "ideogram4";
        public const string Krea2 = "krea2";
        public const string Ltxv = "ltxv";
        public const string Mage = "mage";
        public const string Minimax = "minimax";
    }

    /// <summary>The <c>weight_dtype</c> combo on Step1X's text-encode load.</summary>
    internal static class WeightDtype
    {
        public const string BFloat16 = "bfloat16";
    }

    /// <summary>The <c>weight_type</c> combo on the IPAdapter apply node.</summary>
    internal static class IpAdapterWeight
    {
        public const string Standard = "standard";
    }

    /// <summary>The <c>format</c> container combo on SaveVideo.</summary>
    internal static class SaveFormat
    {
        public const string Auto = "auto";
    }

    /// <summary>The <c>codec</c> combo on SaveVideo.</summary>
    internal static class VideoCodec
    {
        public const string Auto = "auto";
    }

    /// <summary>The <c>ref_image_size</c> combo on the MiniMax-H3 reference node.</summary>
    internal static class RefImageSize
    {
        public const string Match = "match";
    }

    /// <summary>The <c>reference_latents_method</c> combo on <c>FluxKontextMultiReferenceLatentMethod</c>. Which values a
    /// model supports depends on its ComfyUI model class: <see cref="IndexTimestepZero"/> is handled by the Qwen-Image
    /// path (and global-modulation Flux like Krea2), but on a plain per-block-modulation Flux model — LongCat — it
    /// doubles the timestep batch without a compensating vec reshape and crashes in the modulation
    /// (<c>flux/layers.py</c> batch mismatch). LongCat's official blueprint uses <see cref="Index"/>.</summary>
    internal static class ReferenceLatents
    {
        public const string Offset = "offset";
        public const string Index = "index";
        public const string UxoUno = "uxo/uno";
        public const string IndexTimestepZero = "index_timestep_zero";
    }

    /// <summary>The per-step projection / final-render cell-method combo on the pixelize custom nodes
    /// (PixelManifoldProjection <c>method</c>, PixelQuantize <c>method</c>) — shared verbatim by every pixelizer.</summary>
    internal static class Pixelize
    {
        public const string Median = "median";
        public const string Mode = "mode";
        public const string Box = "box";
        public const string NearestPresent = "nearest_present";
        public const string MeanSrgb = "mean_srgb";
        public const string MeanLinear = "mean_linear";
        public const string MeanOklab = "mean_oklab";
        public const string Lanczos = "lanczos";
        public const string VarHybrid = "var_hybrid";
        public const string SupersampleMode = "supersample_mode";
    }

    /// <summary>The PixelQuantize <c>engine</c> selector — median (named-palette per-frame snap) vs fp
    /// (feature-preserving + one global palette).</summary>
    internal static class PixelEngine
    {
        public const string Median = "median";
        public const string Fp = "fp";
    }

    /// <summary>The AnimateDiff <c>beta_schedule</c> combo on ADE_UseEvolvedSampling — several values carry the module
    /// they belong to as prose inside the token itself, so the token is the value ComfyUI matches verbatim.</summary>
    internal static class BetaSchedule
    {
        public const string Autoselect = "autoselect";
        public const string UseExisting = "use existing";
        public const string SqrtLinearAnimateDiff = "sqrt_linear (AnimateDiff)";
        public const string LinearAnimateDiffSdxl = "linear (AnimateDiff-SDXL)";
        public const string LinearHotshotXlDefault = "linear (HotshotXL/default)";
        public const string AvgSqrtLinearLinear = "avg(sqrt_linear,linear)";
        public const string LcmAvgSqrtLinearLinear = "lcm avg(sqrt_linear,linear)";
        public const string Lcm = "lcm";
        public const string Lcm100Ots = "lcm[100_ots]";
        public const string LcmThenSqrtLinear = "lcm >> sqrt_linear";
        public const string Sqrt = "sqrt";
        public const string Cosine = "cosine";
        public const string SquaredcosCapV2 = "squaredcos_cap_v2";
    }

    /// <summary>The SeedVR2 <c>color_match</c> combo — how the upscaled frames are colour-matched to the source.</summary>
    internal static class ColorMatch
    {
        public const string Lab = "lab";
        public const string Wavelet = "wavelet";
        public const string WaveletAdaptive = "wavelet_adaptive";
        public const string Hsv = "hsv";
        public const string Adain = "adain";
        public const string None = "none";
    }
}

/// <summary>Dropdown vocabularies (a schema's <see cref="ImageGen.Comfy.ParamSpec.Choices"/>) built from the
/// <see cref="ComfyWidgets"/> consts — kept out of the pure holders so those stay const-only (IMGSTR003), mirroring how
/// <c>LoaderKindWire.Choices</c> sits beside <c>LoaderKinds</c>. One array per combo that more than one schema offers,
/// so every pixelizer's dropdown carries a single spelling of each token.</summary>
internal static class ComfyWidgetChoices
{
    /// <summary>The pixelize projection/cell-method vocabulary in dropdown order, shared by every pixelizer schema.</summary>
    public static readonly string[] PixelizeMethods =
    [
        ComfyWidgets.Pixelize.Median, ComfyWidgets.Pixelize.Mode, ComfyWidgets.Pixelize.Box,
        ComfyWidgets.Pixelize.NearestPresent, ComfyWidgets.Pixelize.MeanSrgb, ComfyWidgets.Pixelize.MeanLinear,
        ComfyWidgets.Pixelize.MeanOklab, ComfyWidgets.Pixelize.Lanczos, ComfyWidgets.Pixelize.VarHybrid,
        ComfyWidgets.Pixelize.SupersampleMode,
    ];

    /// <summary>The PixelQuantize engine-selector vocabulary in dropdown order.</summary>
    public static readonly string[] PixelEngines = [ComfyWidgets.PixelEngine.Median, ComfyWidgets.PixelEngine.Fp];
}

/// <summary>The output-image filename prefix this app writes on a Save node — its own identifier, not a ComfyUI token.
/// <see cref="Generate"/> for a from-scratch generation, <see cref="Edit"/> for an edit/animate output.</summary>
internal static class OutputPrefixes
{
    public const string Generate = "forgemcp";
    public const string Edit = "forgemcp_edit";
}