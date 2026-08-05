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

    /// <summary>The <c>reference_latents_method</c> combo on Qwen-Edit's reference-latent node.</summary>
    internal static class ReferenceLatents
    {
        public const string IndexTimestepZero = "index_timestep_zero";
    }
}

/// <summary>The output-image filename prefix this app writes on a Save node — its own identifier, not a ComfyUI token.
/// <see cref="Generate"/> for a from-scratch generation, <see cref="Edit"/> for an edit/animate output.</summary>
internal static class OutputPrefixes
{
    public const string Generate = "forgemcp";
    public const string Edit = "forgemcp_edit";
}
