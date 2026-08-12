using ImageGen.Comfy.Edit.AnimaInpaint;
using ImageGen.Comfy.Edit.AnimaOutpaint;
using ImageGen.Comfy.Edit.AnimateDiffLightningI2V;
using ImageGen.Comfy.Edit.AnimateDiffSd15;
using ImageGen.Comfy.Edit.AnimateLcmI2V;
using ImageGen.Comfy.Edit.BiRefNetMatte;
using ImageGen.Comfy.Edit.BiRefNetMatteVideo;
using ImageGen.Comfy.Edit.BooguEdit;
using ImageGen.Comfy.Edit.ChronoEdit;
using ImageGen.Comfy.Edit.DeflickerAutoVideo;
using ImageGen.Comfy.Edit.DreamOmni2Edit;
using ImageGen.Comfy.Edit.DreamOmni2Pixelize;
using ImageGen.Comfy.Edit.Flux2Klein4bEdit;
using ImageGen.Comfy.Edit.Flux2Klein4bPixelize;
using ImageGen.Comfy.Edit.Flux2Klein9bEdit;
using ImageGen.Comfy.Edit.FluxFillInpaint;
using ImageGen.Comfy.Edit.FluxFillOutpaint;
using ImageGen.Comfy.Edit.FluxKontextEdit;
using ImageGen.Comfy.Edit.FluxKontextPixelize;
using ImageGen.Comfy.Edit.HunyuanVideo15I2V;
using ImageGen.Comfy.Edit.Img2ImgRedraw;
using ImageGen.Comfy.Edit.Krea2AnyPaint;
using ImageGen.Comfy.Edit.Krea2Redraw;
using ImageGen.Comfy.Edit.LineThickenAnime2Sketch;
using ImageGen.Comfy.Edit.LineThickenControlNet;
using ImageGen.Comfy.Edit.LineThickenErode;
using ImageGen.Comfy.Edit.LineThickenSketchKeras;
using ImageGen.Comfy.Edit.LineThickenXDoG;
using ImageGen.Comfy.Edit.LtxV2I2V;
using ImageGen.Comfy.Edit.LtxvI2V;
using ImageGen.Comfy.Edit.MageFlowEdit;
using ImageGen.Comfy.Edit.MageFlowEditTurbo;
using ImageGen.Comfy.Edit.MiniMaxH3I2V;
using ImageGen.Comfy.Edit.MiniMaxH3Ref2V;
using ImageGen.Comfy.Edit.Pixelize;
using ImageGen.Comfy.Edit.PixelQuantize;
using ImageGen.Comfy.Edit.PixelQuantizeBatch;
using ImageGen.Comfy.Edit.PixelQuantizeVideo;
using ImageGen.Comfy.Edit.PixelVideo;
using ImageGen.Comfy.Edit.QwenImageEdit;
using ImageGen.Comfy.Edit.QwenImageEditInpaint;
using ImageGen.Comfy.Edit.QwenImageInpaint;
using ImageGen.Comfy.Edit.QwenImageOutpaint;
using ImageGen.Comfy.Edit.QwenPixelize;
using ImageGen.Comfy.Edit.QwenRapidAio;
using ImageGen.Comfy.Edit.SdxlAnimateDiff;
using ImageGen.Comfy.Edit.SeedVr2Upscale;
using ImageGen.Comfy.Edit.Step1XEdit;
using ImageGen.Comfy.Edit.Upscale;
using ImageGen.Comfy.Edit.WanA14bI2V;
using ImageGen.Comfy.Edit.WanI2V;
using ImageGen.Comfy.Generation.Anima;
using ImageGen.Comfy.Generation.AutismMix;
using ImageGen.Comfy.Generation.BooguBase;
using ImageGen.Comfy.Generation.Chroma;
using ImageGen.Comfy.Generation.Flux1Dev;
using ImageGen.Comfy.Generation.Flux1Schnell;
using ImageGen.Comfy.Generation.Flux2Dev;
using ImageGen.Comfy.Generation.Flux2Klein4b;
using ImageGen.Comfy.Generation.Flux2Klein9b;
using ImageGen.Comfy.Generation.HiDream;
using ImageGen.Comfy.Generation.HunyuanImage21;
using ImageGen.Comfy.Generation.HunyuanVideo15T2V;
using ImageGen.Comfy.Generation.HunyuanVideoT2V;
using ImageGen.Comfy.Generation.Ideogram4;
using ImageGen.Comfy.Generation.Krea2;
using ImageGen.Comfy.Generation.Krea2Refine;
using ImageGen.Comfy.Generation.LtxV2T2V;
using ImageGen.Comfy.Generation.Lumina2;
using ImageGen.Comfy.Generation.MageFlow;
using ImageGen.Comfy.Generation.MageFlowTurbo;
using ImageGen.Comfy.Generation.MiniMaxH3T2V;
using ImageGen.Comfy.Generation.PhotAnima;
using ImageGen.Comfy.Generation.PixelAnima;
using ImageGen.Comfy.Generation.PixelDiT;
using ImageGen.Comfy.Generation.PonyV6;
using ImageGen.Comfy.Generation.QwenImage;
using ImageGen.Comfy.Generation.Sd15;
using ImageGen.Comfy.Generation.Sd21;
using ImageGen.Comfy.Generation.Sd35Medium;
using ImageGen.Comfy.Generation.Sd35TripleClip;
using ImageGen.Comfy.Generation.Sdxl;
using ImageGen.Comfy.Generation.Wan22T2v;
using ImageGen.Comfy.Generation.WanA14bT2V;
using ImageGen.Comfy.Generation.ZImage;
using ImageGen.Comfy.Generation.ZImageTurbo;

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
        _ = services.AddSingleton<IWorkflow, Sd15Workflow>();
        _ = services.AddSingleton<IWorkflow, Sd21Workflow>();
        _ = services.AddSingleton<IWorkflow, SdxlWorkflow>();
        _ = services.AddSingleton<IWorkflow, PonyV6Workflow>();
        _ = services.AddSingleton<IWorkflow, AutismMixWorkflow>();
        _ = services.AddSingleton<IWorkflow, Lumina2Workflow>();
        _ = services.AddSingleton<IWorkflow, AnimaWorkflow>();
        _ = services.AddSingleton<IWorkflow, PixelAnimaWorkflow>();     // Anima txt2img under the per-step pixel-manifold projection + final PixelQuantize
        _ = services.AddSingleton<IWorkflow, PhotAnimaWorkflow>();
        _ = services.AddSingleton<IWorkflow, ZImageTurboWorkflow>();
        _ = services.AddSingleton<IWorkflow, ZImageWorkflow>();
        _ = services.AddSingleton<IWorkflow, MageFlowWorkflow>();       // Mage-Flow (RL) text-to-image (TextEncodeMageFlowEdit, text-only)
        _ = services.AddSingleton<IWorkflow, MageFlowTurboWorkflow>();  // Mage-Flow-Turbo (4-step) text-to-image
        _ = services.AddSingleton<IWorkflow, Flux1DevWorkflow>();
        _ = services.AddSingleton<IWorkflow, Flux1SchnellWorkflow>();
        _ = services.AddSingleton<IWorkflow, Flux2Klein4bWorkflow>();
        _ = services.AddSingleton<IWorkflow, Flux2Klein9bWorkflow>();
        _ = services.AddSingleton<IWorkflow, Wan22T2vWorkflow>();
        _ = services.AddSingleton<IWorkflow, Sd35MediumWorkflow>();
        _ = services.AddSingleton<IWorkflow, PixelDiTWorkflow>();
        // 24GB-tier generation models
        _ = services.AddSingleton<IWorkflow, QwenImageWorkflow>();
        _ = services.AddSingleton<IWorkflow, Flux2DevWorkflow>();
        _ = services.AddSingleton<IWorkflow, HiDreamWorkflow>();
        _ = services.AddSingleton<IWorkflow, Sd35TripleClipWorkflow>();
        _ = services.AddSingleton<IWorkflow, ChromaWorkflow>();
        _ = services.AddSingleton<IWorkflow, HunyuanImage21Workflow>();
        _ = services.AddSingleton<IWorkflow, BooguBaseWorkflow>();      // Boogu-Image-0.1-Base text-to-image (reuses txt2img topology)
        _ = services.AddSingleton<IWorkflow, Krea2Workflow>();          // Krea 2 RAW text-to-image (reuses txt2img topology)
        _ = services.AddSingleton<IWorkflow, Krea2RefineWorkflow>();    // Krea 2 base → Turbo polish (two-stage latent refiner)
        _ = services.AddSingleton<IWorkflow, Ideogram4Workflow>();      // Ideogram 4 text-to-image (custom dual-model guider graph)

        // Edit (image + instruction)
        _ = services.AddSingleton<IWorkflow, QwenImageEditWorkflow>();
        _ = services.AddSingleton<IWorkflow, QwenRapidAioWorkflow>();
        _ = services.AddSingleton<IWorkflow, MageFlowEditWorkflow>();       // Mage-Flow-Edit (RL) instruction editing (TextEncodeMageFlowEdit + references)
        _ = services.AddSingleton<IWorkflow, MageFlowEditTurboWorkflow>();  // Mage-Flow-Edit-Turbo (4-step) instruction editing
        _ = services.AddSingleton<IWorkflow, AnimaInpaintWorkflow>();   // masked img2img inpaint on the Anima checkpoint
        _ = services.AddSingleton<IWorkflow, AnimaOutpaintWorkflow>();  // ImagePadForOutpaint + masked img2img on the Anima checkpoint
        _ = services.AddSingleton<IWorkflow, QwenImageInpaintWorkflow>();   // base Qwen-Image + InstantX inpainting ControlNet (NOT the Edit fine-tune)
        _ = services.AddSingleton<IWorkflow, QwenImageEditInpaintWorkflow>();   // Qwen-Image-EDIT + painted mask + references via InpaintModelConditioning (no ControlNet)
        _ = services.AddSingleton<IWorkflow, QwenImageOutpaintWorkflow>();  // same ControlNet, canvas extended by ImagePadForOutpaint
        _ = services.AddSingleton<IWorkflow, FluxFillInpaintWorkflow>();    // FLUX.1 Fill [dev] — mask is a NATIVE model input, not a ControlNet
        _ = services.AddSingleton<IWorkflow, FluxFillOutpaintWorkflow>();   // same model, canvas extended by ImagePadForOutpaint
        _ = services.AddSingleton<IWorkflow, Krea2AnyPaintInpaintWorkflow>();   // Krea 2 Turbo + AnyPaint LoRA — arbitrary-mask inpaint (reference attention + per-step token pinning, no composite)
        _ = services.AddSingleton<IWorkflow, Krea2AnyPaintOutpaintWorkflow>();  // same LoRA/nodes, canvas grown by per-side pads
        _ = services.AddSingleton<IWorkflow, Img2ImgRedrawWorkflow>();  // whole-image img2img redraw on any gen checkpoint (anima / photanima)
        _ = services.AddSingleton<IWorkflow, Krea2RedrawWorkflow>();    // whole-image partial-denoise polish on Krea 2 Turbo
        _ = services.AddSingleton<IWorkflow, UpscaleWorkflow>();        // feed-forward ESRGAN-family upscale (anime PLKSR / photo DAT2)
        _ = services.AddSingleton<IWorkflow, SeedVr2UpscaleWorkflow>(); // one-step diffusion upscale/restore (SeedVR2 3B)
        _ = services.AddSingleton<IWorkflow, WanI2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, AnimateDiffSd15Workflow>();
        _ = services.AddSingleton<IWorkflow, SdxlAnimateDiffWorkflow>();
        _ = services.AddSingleton<IWorkflow, LtxvI2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, LtxV2I2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, HunyuanVideo15I2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, AnimateDiffLightningI2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, AnimateLcmI2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, FluxKontextEditWorkflow>();
        _ = services.AddSingleton<IWorkflow, Flux2Klein4bEditWorkflow>();
        _ = services.AddSingleton<IWorkflow, Flux2Klein9bEditWorkflow>();
        _ = services.AddSingleton<IWorkflow, BooguEditWorkflow>();      // Boogu-Image-0.1-Edit instruction editing (TextEncodeBooguEdit)
        _ = services.AddSingleton<IWorkflow, ChronoEditWorkflow>();     // ChronoEdit-14B (Wan2.1-I2V backbone, last-frame edit; native nodes)
        _ = services.AddSingleton<IWorkflow, DreamOmni2EditWorkflow>(); // DreamOmni2 reference edit (HM-RunningHub pipeline nodes, no llama.cpp)
        _ = services.AddSingleton<IWorkflow, Step1XEditWorkflow>();     // Step1X-Edit i1258 (raykindle node, flash-attn->SDPA patched)
        _ = services.AddSingleton<IWorkflow, PixelQuantizeWorkflow>();  // API-only model-free pixelizer (ComfyUI-PixelHarness)
        _ = services.AddSingleton<IWorkflow, PixelQuantizeBatchWorkflow>(); // model-free BATCH-of-images pixelizer (LoadImage×N -> ImageBatch -> PixelQuantizeFP): derives the global palette over a set of stills, no video
        _ = services.AddSingleton<IWorkflow, PixelQuantizeVideoWorkflow>(); // model-free video-to-video pixelizer (LoadVideo -> PixelQuantize -> SaveAnimatedWEBP)
        _ = services.AddSingleton<IWorkflow, BiRefNetMatteVideoWorkflow>(); // background-removal matte (BiRefNetMatte node -> transparent lossless WEBP)
        _ = services.AddSingleton<IWorkflow, BiRefNetMatteWorkflow>();      // still sibling: single-image matte (LoadImage -> BiRefNetMatte -> PNG w/ alpha)
        _ = services.AddSingleton<IWorkflow, DeflickerAutoVideoWorkflow>(); // auto flicker/wash fix (DeflickerAuto node: BiRefNet stats -> drift-aware histmatch)
        _ = services.AddSingleton<IWorkflow, LineThickenErodeWorkflow>(); // model-free outline thickener (min filter / cv2.erode)
        _ = services.AddSingleton<IWorkflow, LineThickenXDoGWorkflow>();  // model-free outline-only thickener (XDoG extract + multiply)
        _ = services.AddSingleton<IWorkflow, LineThickenAnime2SketchWorkflow>(); // anime line-extract (controlnet_aux) + thicken + composite
        _ = services.AddSingleton<IWorkflow, LineThickenSketchKerasWorkflow>();  // sketchKeras line-extract + thicken + composite
        _ = services.AddSingleton<IWorkflow, LineThickenControlNetWorkflow>();   // lineart ControlNet re-render through an anime checkpoint
        _ = services.AddSingleton<IWorkflow, PixelizeWorkflow>();       // API-only diffusion pixelizer (per-step manifold projection)
        _ = services.AddSingleton<IWorkflow, QwenPixelizeWorkflow>();   // API-only QIE pixelizer (pixel art direct from a reference)
        _ = services.AddSingleton<IWorkflow, FluxKontextPixelizeWorkflow>();   // pixelize on FLUX.1-Kontext (per-step projection)
        _ = services.AddSingleton<IWorkflow, Flux2Klein4bPixelizeWorkflow>();  // pixelize on FLUX.2-Klein 4B (per-step projection)
        _ = services.AddSingleton<IWorkflow, DreamOmni2PixelizeWorkflow>();    // pixelize on DreamOmni2 (in-pipeline per-step projection)
        // Pixel-art VIDEO: any i2v base + per-frame PixelQuantize (locked palette = temporally consistent). One line
        // per base CLASS; the decorator reuses the base graph and the quantizer node as-is. Each model variant then
        // gets a "-pixel" config in workflows.json binding to one of these (LTX-2/2.3/dev all share LtxV2I2VWorkflow).
        _ = services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new LtxV2I2VWorkflow()));    // ltx2-i2v-pixel  (LTX-2 / 2.3 / dev)
        _ = services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new LtxvI2VWorkflow()));     // ltxv-i2v-pixel  (LTX 0.9.8 / 13b)
        _ = services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new WanI2VWorkflow()));      // wan22-ti2v-5b-pixel
        _ = services.AddSingleton<IWorkflow>(_ => new PixelVideoWorkflow(new WanA14bI2VWorkflow()));  // wan22-i2v-a14b-pixel
        // Guiding (per-step PixelManifoldProjection) is the `guided` boolean param on these, not a separate set.
        // 24GB-tier video: Wan 2.2 A14B MoE (i2v + t2v) and native HunyuanVideo text-to-video
        _ = services.AddSingleton<IWorkflow, WanA14bI2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, WanA14bT2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, HunyuanVideo15T2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, HunyuanVideoT2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, LtxV2T2VWorkflow>();       // LTX-2 / 2.3 / 2.5 text→video (EmptyLTXVLatentVideo + LTXV sampler chain)
        // MiniMax-H3 — omni-modal video with NATIVE audio (mp4, not the silent SaveAnimatedWEBP). TWO task-specific
        // diffusion checkpoints over one shared encoder/VAE stack: fl2va serves T2V (generation) and I2V (edit,
        // optional last frame); ref2va (edit, subject-reference conditioning) is its own weight set.
        // Needs ComfyUI >= v0.30.1.
        _ = services.AddSingleton<IWorkflow, MiniMaxH3T2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, MiniMaxH3I2VWorkflow>();
        _ = services.AddSingleton<IWorkflow, MiniMaxH3Ref2VWorkflow>();

        _ = services.AddSingleton<WorkflowRegistry>();
        return services;
    }
}
