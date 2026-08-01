# Supported models

**95 models, 162 presets.** Generated from `configurations/workflows/` by `tools/gen-models-doc.py` — edit the catalogue, not this file.

A model can appear in more than one section: the same weights that generate a picture often also redraw one or drive an effect.

A model appears in the app once you have pointed its slots at files on your disk — most are recognised automatically, the rest are bound on the models page.

🏷 marks a **booru-tagged** model — the ones with tag autocomplete, `#tag` / `@artist` markers and tag bans. Every other model takes an ordinary sentence.

## Text to image

Type a prompt, get a picture.

| Model | What it is |
| --- | --- |
| **Anima** 🏷 | The highest-quality anime-specific model here -- a 2B anime base with the cleanest output, the best hands/anatomy for anime characters of any model here, and strong character/artist/series knowledge. |
| **Anima (bf16 test)** 🏷 | The highest-quality anime-specific model here -- a 2B anime base with the cleanest output, the best hands/anatomy for anime characters of any model here, and strong character/artist/series knowledge. |
| **AutismMix Confetti** 🏷 | A high-quality, fast SDXL anime model (Pony-based merge) aimed especially at anime women/girls -- cleaner default style and better hands than raw Pony, and faster than Anima. |
| **Boogu-Image Base** | Brand-new (0.1) Apache-2.0 text-to-image foundation model. |
| **Chroma1-Base** | The raw Chroma1-Base (512px foundational model; Chroma1-HD is its high-res finetune). |
| **Chroma1-Flash** | The distilled Chroma1-Flash (HD-based): ~8 steps at low CFG with a 2nd-order sampler (heun) — fast uncensored generation at HD quality. |
| **Chroma1-HD** | Open, fully-uncensored 8.9B Flux-derived model (bf16 on 24 GB). |
| **Chroma1-Radiance (pixel-space)** | Chroma, with the VAE removed: denoises RGB directly, so there is no latent round-trip and no VAE compression artifacts. |
| **FLUX.1-dev** ×2 | 12B open-weight quality flagship of the Flux.1 generation: excellent prompt following, typography, photorealism, and hands. |
| **FLUX.1-Krea** | Krea's opinionated FLUX.1-dev realism finetune (fp8 on 24 GB). |
| **FLUX.1-schnell** ×2 | Apache-2.0 12B distilled to 1-4 steps: fast, commercial-friendly, same strong prompt understanding as dev with slightly lower peak fidelity. |
| **FLUX.2-dev** | The full FLUX.2 [dev] flagship (Q4_K_M on 24 GB). |
| **FLUX.2-Klein 4B** ×2 | FLUX.2-Klein 4B at full quality — the NON-distilled base (bf16) on 24 GB: 20 steps at real CFG 5 with negative prompts, higher fidelity than the 4-step distilled fp8 used on smaller cards. |
| **FLUX.2-Klein 9B** ×2 | Higher-quality Klein with best-in-family complex-prompt handling; non-commercial. |
| **HiDream-I1 Dev** | The Dev (faster, guidance-distilled) HiDream-I1 variant. |
| **HiDream-I1 Fast** | The Fast (16-step distilled) HiDream-I1 variant. |
| **HiDream-I1 Full** | 17B sparse-MoE model, the Full (highest-quality) variant. |
| **HunyuanImage 2.1** | Tencent's 2K-native text-to-image base, distilled for ~8-step sampling (meanflow). |
| **HunyuanImage 2.1 HQ** | The full (non-distilled) HunyuanImage 2.1: ~50 steps at cfg ~3.5 for maximum fidelity and prompt adherence. |
| **HunyuanImage 3.0 (NF4)** | Tencent's 80B (13B-active MoE) image model, NF4-quantized to ~45 GB on disk. |
| **Krea 2** | Krea's open aesthetic-first text-to-image model (RAW base variant). |
| **Krea 2 Turbo** | Distilled few-step (8-step) version of Krea 2 with the same polished aesthetic at a fraction of the cost. |
| **Lumina-Image 2.0** | 2.6B flow DiT with a Gemma-2 LLM encoder; strong text-image alignment and a distinctive system-prompt mechanism for setting style/role. |
| **Photanima** 🏷 | Photographic finetune of Anima Base v1.0 (same 2B architecture), trained ~45k steps on ~2000 highly-aesthetic photos. |
| **Pixelanima** 🏷 | Pixel-art GENERATION with Anima: the per-step pixel-manifold projection clamps the x0 estimate onto a fixed grid+palette every denoise step while Anima (the highest-quality anime model here) authors… |
| **PixelDiT 1300M (pixel-space)** | NVIDIA PixelDiT-1300M. |
| **Pony Diffusion V6 XL** 🏷 | The best model here for FURRY and PONY (My Little Pony) art -- a heavily re-trained SDXL with deep furry/anthro and pony concept coverage. |
| **Qwen Rapid** | Uncensored, distilled all-in-one editor on the same 20B Qwen-Image-Edit base. |
| **Qwen-Image** | The 20B Qwen-Image text-to-image base (Q6_K on 24 GB). |
| **Qwen-Image 2512** | The December 2025 refresh of Qwen-Image (Q8_0 on 24 GB). |
| **SDXL 1.0** | High-quality native-1024 base with strong prompt adherence and composition; the standard general-purpose foundation. |
| **Stable Diffusion 1.5** | Lightweight, fast workhorse with the biggest LoRA/ControlNet ecosystem; lower native resolution and weaker prompt adherence than modern models. |
| **Stable Diffusion 2.1** | Native-768 SD2 model; niche today but works perfectly on ComfyUI (auto v-prediction). |
| **Stable Diffusion 3.5 Large** ×2 | The 8.1B SD3.5 Large flagship at full bf16 precision with the fp16 T5 encoder (24 GB). |
| **Stable Diffusion 3.5 Large Turbo** | The 4-step distilled SD3.5 Large Turbo at bf16 (24 GB). |
| **Stable Diffusion 3.5 Medium** | 2.5B MMDiT with strong prompt adherence + typography; runs great on ComfyUI (EmptySD3LatentImage, cfg ~4.5). |
| **Z-Image** ×2 | Full Z-Image base supporting classifier-free guidance and negative prompts; higher diversity/control than Turbo at the cost of more steps. |
| **Z-Image Turbo** ×2 | 8-step distilled ~6B model with near-base quality and notably STRONG instruction/prompt following -- the best model here for anatomical study (specific poses, body positioning, anatomy/figure work). |
| **Z-Image — NippleDiffusion** ×2 | Z-Image base with the NippleDiffusion LoRA applied (NSFW). |

## Editing, inpaint and outpaint

Take an existing image and change it. Multi-turn: each edit builds on the last.

| Model | What it is |
| --- | --- |
| **Anima** 🏷 | Reinterpret an entire existing image through Anima using a prompt — the source is used as the init latent ('the noise thing') and re-sampled at a partial denoise with NO mask, so the composition is… |
| **Anima (inpaint)** 🏷 | Regenerate a PAINTED region of an existing image with the Anima model — paint the area (e.g. |
| **Anima (outpaint)** 🏷 | Extend an image beyond its edges with Anima. |
| **Anime** | Upscale anime, illustration and other 2D art through AnimeSharpV2's 'Sharp' RealPLKSR network. |
| **Boogu-Image Edit** | Brand-new (0.1) Apache-2.0 unified edit model. |
| **Chroma1-Base** | Reinterpret an entire existing image through Chroma1-Base using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **Chroma1-Flash** | Reinterpret an entire existing image through Chroma1-Flash using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **Chroma1-HD** | Reinterpret an entire existing image through Chroma1-HD using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **Chroma1-Radiance (pixel-space)** | Reinterpret an entire existing image through Chroma1-Radiance using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but… |
| **ChronoEdit-14B** | NVIDIA's temporal-reasoning editor on a Wan 14B backbone (same class as your Wan 2.2). |
| **DreamOmni2 (reference edit)** | Reference-based instruction editor: give a source image + ONE reference image and an instruction (e.g. |
| **FireRed-Image-Edit 1.1** | FireRed's instruction editor on a ~20B Qwen-class backbone, Q5_K_M GGUF (~14.9GB) for 24GB fit. |
| **FLUX.1 Fill (inpaint)** | Replace a painted region with the model BUILT for inpainting rather than a txt2img base steered by a ControlNet. |
| **FLUX.1 Fill (outpaint)** | Extend an image past its edges with the model trained for outpainting. |
| **FLUX.1-dev** ×2 | Reinterpret an entire existing image through FLUX.1-dev using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **FLUX.1-Kontext** | Takes an INPUT IMAGE + a natural-language edit instruction and returns the edited image; preserves identity/style across sequential edits. |
| **FLUX.1-Krea** | Reinterpret an entire existing image through FLUX.1-Krea using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **FLUX.1-schnell** ×2 | Reinterpret an entire existing image through FLUX.1-schnell using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **FLUX.2-dev** | Reinterpret an entire existing image through FLUX.2-dev using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **FLUX.2-Klein 4B** ×4 | Newest small Flux.2: 4B unified generate+edit model with a Qwen3 encoder, fast (4-step), Apache-licensed, strong complex-prompt understanding for its size. |
| **FLUX.2-Klein 9B** ×4 | Higher-quality Klein with best-in-family complex-prompt handling; non-commercial. |
| **Krea 2 (Base + Turbo Polish)** | Best-of-both Krea 2: the RAW base gives strong prompt adherence and composition at real CFG, then the distilled Turbo polishes texture and aesthetic over the base latent in a few steps. |
| **Krea 2 Turbo** | Polish any existing image through Krea 2 Turbo — the source is VAE-encoded to the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept while Turbo reworks texture… |
| **LongCat-Image-Edit** | Meituan's instruction editor on a Flux-class backbone, fp8/GGUF-light (transformer ~4.7GB). |
| **LongCat-Image-Edit Turbo** | Distilled LongCat editor: ~8 steps at CFG 1 for fast drafts/previews. |
| **Photanima** 🏷 | Reinterpret an entire existing image through Photanima using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… |
| **Photo** | Upscale photographic and real-world images through Nomos2's high-quality DAT2 network. |
| **Qwen-Image (inpaint)** | Regenerate a PAINTED region of an image with base Qwen-Image driven by the InstantX inpainting ControlNet. |
| **Qwen-Image (outpaint)** | Extend an image past its edges with base Qwen-Image. |
| **Qwen-Image-Edit** | Clean instruction editor on the 20B Qwen-Image base, fp8 for near-bf16 quality. |
| **SeedVR2** | Restore and upscale an image through SeedVR2's one-step diffusion transformer. |
| **Step1X-Edit (i1258)** | StepFun's original instruction editor (i1258), fp8. |

## Video

Animate a still, or generate a clip from a prompt.

| Model | What it is |
| --- | --- |
| **AnimateDiff Lightning (SD1.5)** | Animate the current image into a short clip: SparseCtrl pins the first frame to your image, IP-Adapter keeps the subject, and the motion module animates it. |
| **AnimateLCM (SD1.5)** | Animate the current image into a short clip: SparseCtrl pins the first frame to your image, IP-Adapter keeps the subject, and the motion module animates it. |
| **HunyuanVideo** ×3 | Original HunyuanVideo (13B) image-to-video, run as a 7.2GB Q4 GGUF (spills to RAM on 8GB). |
| **HunyuanVideo (Anime Style)** ×2 | Anime-style image-to-video on HunyuanVideo via the Anime Style LoRA. |
| **HunyuanVideo (AnimeShots)** ×2 | Anime-style image-to-video on HunyuanVideo via the AnimeShots LoRA. |
| **HunyuanVideo 1.5** ×3 | Uncensored newer video base (HunyuanVideo 1.5). |
| **HunyuanVideo 1.5 (1080p SR T2V)** | The two-stage HunyuanVideo 1.5 text→video pipeline: generate at 480p, then latent upsampler + 1080p SR distilled model refine to 1080p. |
| **HunyuanVideo 1.5 (1080p SR)** | The official two-stage HunyuanVideo 1.5 pipeline: generate at 480p, then a latent upsampler + 1080p SR distilled model refine to full 1080p. |
| **HunyuanVideo 1.5 (480p T2V)** | Text→video on HunyuanVideo 1.5 at 480p (cfg-distilled fp8): the faster, lighter sibling of the 720p t2v. |
| **HunyuanVideo 1.5 Fast** ×2 | The fast HunyuanVideo 1.5 image-to-video (step-distilled, ~6 steps). |
| **HunyuanVideo 1.5 HQ (480p T2V)** | High-quality non-distilled HunyuanVideo 1.5 480p text→video: true CFG with negative prompts, ~30 steps at cfg ~6. |
| **HunyuanVideo 1.5 HQ (480p)** | The high-quality non-distilled HunyuanVideo 1.5 480p image-to-video: true classifier-free guidance (negative prompts work), ~30 steps at cfg ~6. |
| **HunyuanVideo 1.5 HQ (720p)** | The high-quality non-distilled HunyuanVideo 1.5 720p image-to-video: true classifier-free guidance (negatives work), ~30 steps at cfg ~6. |
| **LTX Video** | Fast image-to-video: a ~4s clip in ~20s on 8GB (distilled, 8 steps). |
| **LTX Video 13B** | The 13B LTX-Video 0.9.8 (distilled fp8) image→video — much higher quality than the 2B 0.9.8 the catalog has, still fast (~8 steps). |
| **LTX-2** ×2 | Newer, larger LTX video model (19B) run as an 11GB Q4 GGUF + Gemma encoder, spilling to RAM on 8GB. |
| **LTX-2 dev** | The NON-distilled LTX-2 19B (dev) image→video — higher quality than the distilled build, at the cost of more steps (~30) and real CFG (~3). |
| **LTX-2.3 22B** | The newest LTX flagship (2.3, 22B) — distilled-1.1, image→video, Q4_K_M GGUF on 24 GB. |
| **SDXL AnimateDiff** | Animates a still image into a short clip using base SDXL and the AnimateDiff SDXL motion module. |
| **Wan 2.2** ×2 | Animates a still image into a short clip (image-to-video). |
| **Wan 2.2 (Anime LoRA)** ×2 | Animate a still into a short clip with an anime-style LoRA on WAN 2.2 TI2V-5B. |
| **Wan 2.2 (Flat Color)** ×2 | Animate a still into a short clip in flat anime-color style (WAN 2.2 TI2V-5B + Flat Color LoRA). |
| **Wan 2.2 14B** ×2 | The high-quality 14B image→video (two-expert MoE) — much better motion/detail than the 5B TI2V. |
| **Wan 2.2 14B 720P** ×2 | The 14B image→video at its HIGH resolution tier: the source frame is fitted (at its own aspect) into the official 720P pixel area (1280x720 = 0.92 MP) instead of the 480P tier. |

## Effects and post-processing

Applied to an image or a clip you already have. Several need no diffusion model at all.

| Model | What it is |
| --- | --- |
| **Anima** | Pixel art with ANIMA authoring under reprojection: the per-step pixel-manifold projection clamps the x0 estimate onto a fixed grid+palette every denoise step while Anima (the house-style anime model)… |
| **BiRefNet Matte (image)** | Removes the background from a single image with BiRefNet and outputs a transparent-background PNG. |
| **BiRefNet Matte (video)** | Removes the background from every frame of a source clip with BiRefNet and outputs a transparent-background animated WEBP (lossless, so the alpha channel survives). |
| **Chroma1-HD** | Pixel art authored by Chroma1-HD under per-step reprojection. |
| **Deflicker Auto (video)** | Automatically detects and corrects flicker/washed-out frames in an AI-generated clip. |
| **DreamOmni2** | Pixel art via DreamOmni2's reference editor: the projection runs inside the self-contained pipeline (decode the x0 estimate -> grid+palette quantize -> re-encode -> blend) every denoise step. |
| **FireRed-Image-Edit 1.1** | High-quality pixel art via FireRed-Image-Edit 1.1: the ~20B Qwen-class editor redraws while the projection clamps onto a fixed grid+palette every step. |
| **Flux-dev** | The diffusion pixelizer: redraws an image into clean pixel art while clamping the x0 estimate onto a fixed grid+palette every denoise step. |
| **FLUX.1-Kontext** | Pixel art via FLUX.1-Kontext: the source conditions a reference-latent edit while the projection clamps the x0 estimate onto a fixed grid+palette every step. |
| **FLUX.1-Krea** | Pixel art authored by FLUX.1-Krea under per-step reprojection. |
| **FLUX.2-dev** | Pixel art authored by FLUX.2-dev under per-step reprojection. |
| **FLUX.2-Klein 4B** | Pixel art via the fast 4-step FLUX.2-Klein 4B: reference-latent conditioning + per-step grid+palette projection. |
| **FLUX.2-Klein 9B** | Pixel art authored by FLUX.2-Klein 9B under per-step reprojection. |
| **HiDream-I1 Full** | Pixel art authored by HiDream-I1 Full under per-step reprojection. |
| **HunyuanImage 2.1 HQ** | Pixel art authored by HunyuanImage 2.1 HQ under per-step reprojection. |
| **Krea 2** | Pixel art authored by Krea 2 under per-step reprojection. |
| **Line Thicken (anime line-extract)** | Extracts the anime line art with a neural detector, inverts to dark-on-white, boldens the lines, and multiplies them back over the source so only the outlines darken. |
| **Line Thicken (ControlNet lineart re-render)** | Re-renders the source as img2img at partial denoise with a lineart ControlNet enforcing the (coarse, bolder) outlines, so the character is preserved while the lines are redrawn clean and thick. |
| **Line Thicken (erode)** | Boldens outlines by growing dark pixels with a per-channel 3x3 min filter (the cv2.erode / ImageMagick Erode / Photoshop Minimum algorithm). |
| **Line Thicken (sketchKeras)** | Extracts the source's lines with sketchKeras (dark-on-white), boldens them, and multiplies over the source so only the lines darken. |
| **Line Thicken (XDoG, outline-only)** | Outline-only thicken: eXtended Difference-of-Gaussians pulls the existing edges out, the line layer is boldened, and it's multiplied back over the source so flat-colour interiors stay clean. |
| **LongCat-Image-Edit** | Pixel art via LongCat-Image-Edit: instruction-conditioned redraw with the projection clamping onto a fixed grid+palette every denoise step. |
| **LongCat-Image-Edit Turbo** | Fast pixel art via the distilled 8-step LongCat editor at CFG 1 — quick drafts; switch to the full LongCat pixelizer for final quality. |
| **LTX Video** | Pixel-art image-to-video (locked-palette per-frame quantize). |
| **LTX Video 13B** | Pixel-art image-to-video (locked-palette per-frame quantize). |
| **LTX-2** | LTX-2 image-to-video with the deterministic pixel-art quantizer applied to every decoded frame. |
| **LTX-2 dev** | Pixel-art image-to-video (locked-palette per-frame quantize). |
| **LTX-2.3 22B** | LTX-2.3 (newest LTX) image-to-video, pixel-quantized per frame with a locked palette. |
| **Lumina-Image 2.0** | Pixel art authored by Lumina-Image 2.0 under per-step reprojection. |
| **Pixel Quantize** | Snaps an input image onto a fixed grid + palette in OKLab (median-per-cell by default; mode/box/nearest_present selectable). |
| **Pixel Quantize (batch)** | Pixel-quantizes N still frames together, deriving ONE global fp palette + label frequencies across the whole set (temporally consistent — no frame-to-frame flicker) and emitting the per-frame… |
| **Pixel Quantize (video)** | Pixel-quantizes every frame of a source clip onto a fixed grid + palette and re-encodes the clip at its own frame rate. |
| **Qwen-Image-Edit** | Generates pixel art directly from a reference image: QIE redraws per the instruction while the projection clamps onto a fixed grid+palette every denoise step, so the model produces manifold-friendly… |
| **Stable Diffusion 3.5 Large** | Pixel art authored by Stable Diffusion 3.5 Large under per-step reprojection. |
| **Wan 2.2** | Pixel-art image-to-video (locked-palette per-frame quantize). |
| **Wan 2.2 14B** | Pixel-art image-to-video (locked-palette per-frame quantize). |
| **Z-Image** | Pixel art authored by Z-Image under per-step reprojection. |
| **Z-Image Turbo** | Pixel art authored by Z-Image Turbo under per-step reprojection. |
