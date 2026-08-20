namespace ImageGen.Comfy.Generation.QwenImage;

/// <summary>
/// 24GB-tier generation models whose graph is the plain txt2img topology (single CLIPLoader). Qwen-Image base
/// (CLIPLoader type "qwen_image" + EmptySD3LatentImage + ModelSamplingAuraFlow via the auraflow param) and FLUX.2-dev
/// (CLIPLoader type "flux2" + EmptyFlux2LatentImage + FluxGuidance via the guidance param) both fit the base unchanged;
/// their configs are intended for 24 GB renderers; catalogue eligibility itself is requirement-presence only. The
/// HiDream / SD3.5-triple-CLIP / Chroma topologies need
/// their own graphs — see HighVramWorkflows.cs.
/// </summary>
public sealed class QwenImageWorkflow : Txt2ImgWorkflow<Txt2ImgParams> { public override string Name => "qwen-image"; }
