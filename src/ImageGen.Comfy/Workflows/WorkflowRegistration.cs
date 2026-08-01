using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.Comfy;

/// <summary>
/// Explicit DI registration of every workflow (one per model) + the <see cref="WorkflowRegistry"/>. Adding a model
/// is: register each of its files (diffusion model + any new vae/text-encoder/lora) in requirements.json, add its
/// configuration to workflows.json (soft-linking those requirement ids), and — only if its graph topology is new —
/// write a workflow class and add one line here. Most models reuse an existing class (the generic Txt2Img/Edit
/// bases cover checkpoint/unet/unet_gguf + single/dual CLIP), so they are JSON-only. No reflection scan — the set of
/// runnable workflows is visible in one place. See ARCHITECTURE.md §7.6 "Adding or upgrading a model".
/// </summary>
public static class WorkflowRegistration
{
    public static IServiceCollection AddWorkflows(this IServiceCollection services)
    {
        // Generation (text → image)
        services.AddSingleton<IWorkflow, Sd15Workflow>();
        services.AddSingleton<IWorkflow, Sd21Workflow>();
        services.AddSingleton<IWorkflow, SdxlWorkflow>();
        services.AddSingleton<IWorkflow, PonyV6Workflow>();
        services.AddSingleton<IWorkflow, AutismMixWorkflow>();
        services.AddSingleton<IWorkflow, Lumina2Workflow>();
        services.AddSingleton<IWorkflow, AnimaWorkflow>();
        services.AddSingleton<IWorkflow, PixelAnimaWorkflow>();     // Anima txt2img under the per-step pixel-manifold projection + final PixelQuantize
        services.AddSingleton<IWorkflow, PhotAnimaWorkflow>();
        services.AddSingleton<IWorkflow, ZImageTurboWorkflow>();
        services.AddSingleton<IWorkflow, ZImageWorkflow>();
        services.AddSingleton<IWorkflow, Flux1DevWorkflow>();
        services.AddSingleton<IWorkflow, Flux1SchnellWorkflow>();
        services.AddSingleton<IWorkflow, Flux2Klein4bWorkflow>();
        services.AddSingleton<IWorkflow, Flux2Klein9bWorkflow>();
        services.AddSingleton<IWorkflow, Wan22T2vWorkflow>();
        services.AddSingleton<IWorkflow, Sd35MediumWorkflow>();
        services.AddSingleton<IWorkflow, PixelDiTWorkflow>();
        // 24GB-tier generation models
        services.AddSingleton<IWorkflow, QwenImageWorkflow>();
        services.AddSingleton<IWorkflow, Flux2DevWorkflow>();
        services.AddSingleton<IWorkflow, HiDreamWorkflow>();
        services.AddSingleton<IWorkflow, Sd35TripleClipWorkflow>();
        services.AddSingleton<IWorkflow, ChromaWorkflow>();
        services.AddSingleton<IWorkflow, HunyuanImage21Workflow>();
        services.AddSingleton<IWorkflow, BooguBaseWorkflow>();      // Boogu-Image-0.1-Base text-to-image (reuses txt2img topology)
        services.AddSingleton<IWorkflow, Krea2Workflow>();          // Krea 2 RAW text-to-image (reuses txt2img topology)
        services.AddSingleton<IWorkflow, Krea2RefineWorkflow>();    // Krea 2 base → Turbo polish (two-stage latent refiner)
        services.AddSingleton<IWorkflow, Ideogram4Workflow>();      // Ideogram 4 text-to-image (custom dual-model guider graph)

        // Edit (image + instruction)
        services.AddSingleton<IWorkflow, QwenImageEditWorkflow>();
        services.AddSingleton<IWorkflow, QwenRapidAioWorkflow>();
        services.AddSingleton<IWorkflow, AnimaInpaintWorkflow>();   // masked img2img inpaint on the Anima checkpoint
        services.AddSingleton<IWorkflow, AnimaOutpaintWorkflow>();  // ImagePadForOutpaint + masked img2img on the Anima checkpoint
        services.AddSingleton<IWorkflow, QwenImageInpaintWorkflow>();   // base Qwen-Image + InstantX inpainting ControlNet (NOT the Edit fine-tune)
        services.AddSingleton<IWorkflow, QwenImageOutpaintWorkflow>();  // same ControlNet, canvas extended by ImagePadForOutpaint
        services.AddSingleton<IWorkflow, FluxFillInpaintWorkflow>();    // FLUX.1 Fill [dev] — mask is a NATIVE model input, not a ControlNet
        services.AddSingleton<IWorkflow, FluxFillOutpaintWorkflow>();   // same model, canvas extended by ImagePadForOutpaint
        services.AddSingleton<IWorkflow, Img2ImgRedrawWorkflow>();  // whole-image img2img redraw on any gen checkpoint (anima / photanima)
        services.AddSingleton<IWorkflow, Krea2RedrawWorkflow>();    // whole-image partial-denoise polish on Krea 2 Turbo
        services.AddSingleton<IWorkflow, UpscaleWorkflow>();        // feed-forward ESRGAN-family upscale (anime PLKSR / photo DAT2)
        services.AddSingleton<IWorkflow, SeedVr2UpscaleWorkflow>(); // one-step diffusion upscale/restore (SeedVR2 3B)
        services.AddSingleton<IWorkflow, WanI2VWorkflow>();
        services.AddSingleton<IWorkflow, AnimateDiffSd15Workflow>();
        services.AddSingleton<IWorkflow, SdxlAnimateDiffWorkflow>();
        services.AddSingleton<IWorkflow, LtxvI2VWorkflow>();
        services.AddSingleton<IWorkflow, LtxV2I2VWorkflow>();
        services.AddSingleton<IWorkflow, HunyuanVideo15I2VWorkflow>();
        services.AddSingleton<IWorkflow, AnimateDiffLightningI2VWorkflow>();
        services.AddSingleton<IWorkflow, AnimateLcmI2VWorkflow>();
        services.AddSingleton<IWorkflow, FluxKontextEditWorkflow>();
        services.AddSingleton<IWorkflow, Flux2Klein4bEditWorkflow>();
        services.AddSingleton<IWorkflow, Flux2Klein9bEditWorkflow>();
        services.AddSingleton<IWorkflow, BooguEditWorkflow>();      // Boogu-Image-0.1-Edit instruction editing (TextEncodeBooguEdit)
        services.AddSingleton<IWorkflow, ChronoEditWorkflow>();     // ChronoEdit-14B (Wan2.1-I2V backbone, last-frame edit; native nodes)
        services.AddSingleton<IWorkflow, DreamOmni2EditWorkflow>(); // DreamOmni2 reference edit (HM-RunningHub pipeline nodes, no llama.cpp)
        services.AddSingleton<IWorkflow, Step1XEditWorkflow>();     // Step1X-Edit i1258 (raykindle node, flash-attn->SDPA patched)
        services.AddSingleton<IWorkflow, PixelQuantizeWorkflow>();  // API-only model-free pixelizer (ComfyUI-PixelHarness)
        services.AddSingleton<IWorkflow, PixelQuantizeBatchWorkflow>(); // model-free BATCH-of-images pixelizer (LoadImage×N -> ImageBatch -> PixelQuantizeFP): derives the global palette over a set of stills, no video
        services.AddSingleton<IWorkflow, PixelQuantizeVideoWorkflow>(); // model-free video-to-video pixelizer (LoadVideo -> PixelQuantize -> SaveAnimatedWEBP)
        services.AddSingleton<IWorkflow, BiRefNetMatteVideoWorkflow>(); // background-removal matte (BiRefNetMatte node -> transparent lossless WEBP)
        services.AddSingleton<IWorkflow, BiRefNetMatteWorkflow>();      // still sibling: single-image matte (LoadImage -> BiRefNetMatte -> PNG w/ alpha)
        services.AddSingleton<IWorkflow, DeflickerAutoVideoWorkflow>(); // auto flicker/wash fix (DeflickerAuto node: BiRefNet stats -> drift-aware histmatch)
        services.AddSingleton<IWorkflow, LineThickenErodeWorkflow>(); // model-free outline thickener (min filter / cv2.erode)
        services.AddSingleton<IWorkflow, LineThickenXDoGWorkflow>();  // model-free outline-only thickener (XDoG extract + multiply)
        services.AddSingleton<IWorkflow, LineThickenAnime2SketchWorkflow>(); // anime line-extract (controlnet_aux) + thicken + composite
        services.AddSingleton<IWorkflow, LineThickenSketchKerasWorkflow>();  // sketchKeras line-extract + thicken + composite
        services.AddSingleton<IWorkflow, LineThickenControlNetWorkflow>();   // lineart ControlNet re-render through an anime checkpoint
        services.AddSingleton<IWorkflow, PixelizeWorkflow>();       // API-only diffusion pixelizer (per-step manifold projection)
        services.AddSingleton<IWorkflow, QwenPixelizeWorkflow>();   // API-only QIE pixelizer (pixel art direct from a reference)
        services.AddSingleton<IWorkflow, FluxKontextPixelizeWorkflow>();   // pixelize on FLUX.1-Kontext (per-step projection)
        services.AddSingleton<IWorkflow, Flux2Klein4bPixelizeWorkflow>();  // pixelize on FLUX.2-Klein 4B (per-step projection)
        services.AddSingleton<IWorkflow, DreamOmni2PixelizeWorkflow>();    // pixelize on DreamOmni2 (in-pipeline per-step projection)
        // Pixel-art VIDEO: any i2v base + per-frame PixelQuantize (locked palette = temporally consistent). One line
        // per base CLASS; the decorator reuses the base graph and the quantizer node as-is. Each model variant then
        // gets a "-pixel" config in workflows.json binding to one of these (LTX-2/2.3/dev all share LtxV2I2VWorkflow).
        services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new LtxV2I2VWorkflow()));    // ltx2-i2v-pixel  (LTX-2 / 2.3 / dev)
        services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new LtxvI2VWorkflow()));     // ltxv-i2v-pixel  (LTX 0.9.8 / 13b)
        services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new WanI2VWorkflow()));      // wan22-ti2v-5b-pixel
        services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new WanA14bI2VWorkflow()));  // wan22-i2v-a14b-pixel
        // Guiding (per-step PixelManifoldProjection) is now the `guided` boolean param on these, not a separate set.
        // 24GB-tier video: Wan 2.2 A14B MoE (i2v + t2v) and native HunyuanVideo text-to-video
        services.AddSingleton<IWorkflow, WanA14bI2VWorkflow>();
        services.AddSingleton<IWorkflow, WanA14bT2VWorkflow>();
        services.AddSingleton<IWorkflow, HunyuanVideo15T2VWorkflow>();
        services.AddSingleton<IWorkflow, HunyuanVideoT2VWorkflow>();

        services.AddSingleton<WorkflowRegistry>();
        return services;
    }
}
