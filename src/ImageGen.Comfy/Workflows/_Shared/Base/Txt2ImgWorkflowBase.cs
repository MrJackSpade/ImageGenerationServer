namespace ImageGen.Comfy;

/// <summary>
/// The shared text-to-image parameter menu. Every generation model has its OWN workflow subclass (its own name and
/// VRAM band) deriving from <see cref="Txt2ImgWorkflow{TParams}"/>, but they all draw their exposed knobs from this
/// one <see cref="SharedSchema"/>.
/// </summary>
internal static class Txt2ImgWorkflowBase
{
    internal static readonly IReadOnlyList<ParamSpec> SharedSchema =
    [
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        // "pixel" = a pixel-space latent: (B,3,H,W) at spatial downscale 1, for models that diffuse
        // directly on RGB and have no VAE (PixelDiT, Chroma Radiance). Such a model is paired with the
        // identity "VAE" (pixel_space_vae.safetensors -> comfy's PixelspaceConversionVAE), so the VAEDecode
        // in the shared topology is a no-op passthrough and the graph stays byte-identical elsewhere.
        new() { Key = WorkflowParamKeys.Latent,    Type = ParamType.Enum,   Choices = [LatentKind.Std, LatentKind.Sd3, LatentKind.Flux2, LatentKind.Pixel] },
        new() { Key = WorkflowParamKeys.Auraflow,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.ClipSkip, Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = ParamBounds.CfgMin,   Max = ParamBounds.CfgMax,  Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String, Label = "Sampler" },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Width,     Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Height,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Aspect,    Type = ParamType.String },   // { square/landscape/portrait: [w,h] } dims map
        // Megapixels: the first-class render-SIZE control (#186). W/H supply the aspect RATIO; M scales that ratio to a
        // pixel budget (server-snapped to the resolution step + clamped to the envelope). Precise to two decimals, so a
        // 0.1 step couldn't reach a default like 0.92 — the step is 0.01. Present on the shared schema so a config that
        // exposes it renders a NUMERIC control; a config that omits it keeps today's aspect-map/flat-W/H sizing.
        new() { Key = WorkflowParamKeys.Megapixels, Type = ParamType.Double, Step = 0.01, Label = "Megapixels" },
        // Video shapes for the text-to-VIDEO generators (wan/hunyuan/minimax-h3): clip length (frames) and playback
        // fps. Present on the shared schema so a config that exposes `length` renders it as a NUMERIC control — the
        // control's type is read from here, and without an entry an exposed length falls back to a text box. Image
        // models simply never expose these. Mirrors EditWorkflowBase.
        new() { Key = WorkflowParamKeys.Length,    Type = ParamType.Int,    Label = "Frames", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Fps,       Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.RequiredPrefix,     Type = ParamType.String },
        new() { Key = WorkflowParamKeys.NegativeSupported,  Type = ParamType.Bool },
        // Optional LoRA on the base model — lets a config be a "base + LoRA" txt2img variant (e.g. a Z-Image LoRA).
        new() { Key = WorkflowParamKeys.Lora,          Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.LoraStrength, Type = ParamType.Double, Min = ParamBounds.GenLoraStrengthMin, Max = ParamBounds.GenLoraStrengthMax, Step = 0.01, Label = "LoRA strength" },
        .. CkAttention.Schema,
    ];

    /// <summary>The <c>latent</c> param's kind values — which empty-latent node the topology emits. Written once so
    /// the schema's choice list and <see cref="Txt2ImgWorkflow{TParams}"/>'s emit-time selection share one spelling.</summary>
    private static class LatentKind
    {
        public const string Std = "std";
        public const string Sd3 = "sd3";
        public const string Flux2 = "flux2";
        public const string Pixel = "pixel";
    }
}