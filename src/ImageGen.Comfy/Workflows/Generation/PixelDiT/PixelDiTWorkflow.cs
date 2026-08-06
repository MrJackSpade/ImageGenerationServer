using ImageGen.Comfy;

namespace ImageGen.Comfy.Generation.PixelDiT;

/// <summary>NVIDIA PixelDiT-1300M — diffuses directly in pixel space, so it has no VAE: the shared txt2img
/// topology is reused with latent="pixel" (EmptyChromaRadianceLatentImage) and the identity pixel-space VAE,
/// leaving VAEDecode a passthrough. Gemma-2-2b-it text encoder via CLIPLoader type "pixeldit".</summary>
public sealed class PixelDiTWorkflow : Txt2ImgWorkflow<Txt2ImgParams> { public override string Name => "pixeldit"; }
