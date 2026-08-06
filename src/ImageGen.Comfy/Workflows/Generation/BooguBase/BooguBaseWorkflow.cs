namespace ImageGen.Comfy.Generation.BooguBase;

/// <summary>
/// Boogu-Image-0.1-Base (text-to-image). A 10B unified DiT on the FLUX.1 VAE with a Qwen3-VL text encoder. It needs
/// nothing the generic txt2img topology doesn't already emit: UNETLoader + CLIPLoader (type "boogu") + VAELoader,
/// CLIPTextEncode x2, EmptySD3LatentImage (the FLUX VAE is 16-channel, so the SD3-style empty latent), then KSampler.
/// ComfyUI's Boogu model class bakes the flow-matching sampling shift (3.16) in at load, so — unlike Qwen-Image — no
/// ModelSamplingAuraFlow node is wired; its configuration simply leaves the "auraflow" param unset. JSON-only model
/// aside from this name binding.
/// </summary>
public sealed class BooguBaseWorkflow : Txt2ImgWorkflow<Txt2ImgParams>
{
    public override string Name => "boogu-base";
}
