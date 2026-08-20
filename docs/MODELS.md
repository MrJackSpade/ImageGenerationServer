# Supported models

**109 models, 160 presets.** Generated from `configurations/workflows/` by `tools/gen-models-doc.py` — edit the catalogue, not this file.

A model can appear in more than one section: the same weights that generate a picture often also redraw one or drive an effect.

A model appears in the app once you have pointed its slots at files on your disk — most are recognised automatically, the rest are bound on the models page.

🏷 marks a **booru-tagged** model — the ones with tag autocomplete, `#tag` / `@artist` markers and tag bans. Every other model takes an ordinary sentence.

The Download column links the landing page of the row's main model file. Rows without one need no download of their own: they run on pure image ops or on weights their ComfyUI node fetches itself.

## Text to image

Type a prompt, get a picture.

| Model | What it is | Download |
| --- | --- | --- |
| **Anima** 🏷 | The highest-quality anime-specific model here -- a 2B anime base with the cleanest output, the best hands/anatomy for anime characters of any model here, and strong character/artist/series knowledge. | [Hugging Face](https://huggingface.co/circlestone-labs/Anima) |
| **AutismMix Confetti** 🏷 | A high-quality, fast SDXL anime model (Pony-based merge) aimed especially at anime women/girls -- cleaner default style and better hands than raw Pony, and faster than Anima. | [Civitai](https://civitai.com/models/288584/autismmix-sdxl) |
| **Boogu-Image** | Brand-new (0.1) Apache-2.0 text-to-image foundation model. | [Hugging Face](https://huggingface.co/Comfy-Org/Boogu-Image) |
| **Chroma1-Base** | The raw Chroma1-Base (512px foundational model; Chroma1-HD is its high-res finetune). | [Hugging Face](https://huggingface.co/lodestones/Chroma1-Base) |
| **Chroma1-Flash** | The distilled Chroma1-Flash (HD-based): ~8 steps at low CFG with a 2nd-order sampler (heun) — fast uncensored generation at HD quality. | [Hugging Face](https://huggingface.co/lodestones/Chroma1-Flash) |
| **Chroma1-HD** | Open, fully-uncensored 8.9B Flux-derived model (bf16 on 24 GB). | [Hugging Face](https://huggingface.co/Comfy-Org/Chroma1-HD_repackaged) |
| **Chroma1-Radiance** | Chroma, with the VAE removed: denoises RGB directly, so there is no latent round-trip and no VAE compression artifacts. | [Hugging Face](https://huggingface.co/Comfy-Org/Chroma1-Radiance_Repackaged) |
| **FLUX.1-dev** | 12B open-weight quality flagship of the Flux.1 generation: excellent prompt following, typography, photorealism, and hands. | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-dev) |
| **FLUX.1-Krea** | Krea's opinionated FLUX.1-dev realism finetune (fp8 on 24 GB). | [Hugging Face](https://huggingface.co/Comfy-Org/FLUX.1-Krea-dev_ComfyUI) |
| **FLUX.1-schnell** | Apache-2.0 12B distilled to 1-4 steps: fast, commercial-friendly, same strong prompt understanding as dev with slightly lower peak fidelity. | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-schnell) |
| **FLUX.2-dev** | The full FLUX.2 [dev] flagship (Q4_K_M on 24 GB). | [Hugging Face](https://huggingface.co/Comfy-Org/flux2-dev) |
| **FLUX.2-Klein 4B** | Newest small Flux.2: 4B unified generate+edit model with a Qwen3 encoder, fast (4-step), Apache-licensed, strong complex-prompt understanding for its size. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-4B) |
| **FLUX.2-Klein 4B Base** | FLUX.2-Klein 4B at full quality — the NON-distilled base (bf16) on 24 GB: 20 steps at real CFG 5 with negative prompts, higher fidelity than the 4-step distilled fp8 used on smaller cards. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-base-4B) |
| **FLUX.2-Klein 9B** | Higher-quality Klein with best-in-family complex-prompt handling; non-commercial. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-9B) |
| **HiDream-I1 Dev** | The Dev (faster, guidance-distilled) HiDream-I1 variant. | [Hugging Face](https://huggingface.co/Comfy-Org/HiDream-I1_ComfyUI) |
| **HiDream-I1 Fast** | The Fast (16-step distilled) HiDream-I1 variant. | [Hugging Face](https://huggingface.co/Comfy-Org/HiDream-I1_ComfyUI) |
| **HiDream-I1 Full** | 17B sparse-MoE model, the Full (highest-quality) variant. | [Hugging Face](https://huggingface.co/Comfy-Org/HiDream-I1_ComfyUI) |
| **HunyuanImage 2.1** | Tencent's 2K-native text-to-image base, distilled for ~8-step sampling (meanflow). | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanImage_2.1_ComfyUI) |
| **HunyuanImage 2.1 Full** | The full (non-distilled) HunyuanImage 2.1: ~50 steps at cfg ~3.5 for maximum fidelity and prompt adherence. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanImage_2.1_ComfyUI) |
| **Ideogram 4** | Ideogram's open text-to-image model, renowned for best-in-class typography/text rendering and prompt adherence. | [Hugging Face](https://huggingface.co/Comfy-Org/Ideogram-4) |
| **Krea 2** | Krea's open aesthetic-first text-to-image model (RAW base variant). | [Hugging Face](https://huggingface.co/Comfy-Org/Krea-2) |
| **Krea 2 Turbo** | Distilled few-step (8-step) version of Krea 2 with the same polished aesthetic at a fraction of the cost. | [Hugging Face](https://huggingface.co/Comfy-Org/Krea-2) |
| **Lumina-Image 2.0** | 2.6B flow DiT with a Gemma-2 LLM encoder; strong text-image alignment and a distinctive system-prompt mechanism for setting style/role. | [Hugging Face](https://huggingface.co/Comfy-Org/Lumina_Image_2.0_Repackaged) |
| **Mage-Flow** | Microsoft's compact 4B text-to-image model at native resolution (512-2048 per side, any aspect including extreme 4:1). | [Hugging Face](https://huggingface.co/Comfy-Org/Mage-Flow) |
| **Mage-Flow Turbo** | Few-step distilled Mage-Flow: 4 steps, cfg 1, ~0.6 s/image at 1024x1024 on an A100. | [Hugging Face](https://huggingface.co/Comfy-Org/Mage-Flow) |
| **Photanima** 🏷 | Photographic finetune of Anima Base v1.0 (same 2B architecture), trained ~45k steps on ~2000 highly-aesthetic photos. | [Civitai](https://civitai.com/models/2645333/photanima) |
| **Pixelanima** 🏷 | Pixel-art GENERATION with Anima: the per-step pixel-manifold projection clamps the x0 estimate onto a fixed grid+palette every denoise step while Anima (the highest-quality anime model here) authors… | [Hugging Face](https://huggingface.co/circlestone-labs/Anima) |
| **PixelDiT 1300M** | NVIDIA PixelDiT-1300M. | [Hugging Face](https://huggingface.co/Comfy-Org/PixelDiT) |
| **Pony Diffusion V6 XL** 🏷 | The best model here for FURRY and PONY (My Little Pony) art -- a heavily re-trained SDXL with deep furry/anthro and pony concept coverage. | [Civitai](https://civitai.com/models/257749/pony-diffusion-v6-xl) |
| **Qwen Rapid** | Uncensored, distilled all-in-one editor on the same 20B Qwen-Image-Edit base. | [Hugging Face](https://huggingface.co/Phr00t/Qwen-Image-Edit-Rapid-AIO) |
| **Qwen-Image** | The 20B Qwen-Image text-to-image base (Q6_K on 24 GB). | [Hugging Face](https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI) |
| **Qwen-Image 2512** | The December 2025 refresh of Qwen-Image (Q8_0 on 24 GB). | [Hugging Face](https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI) |
| **SDXL 1.0** | High-quality native-1024 base with strong prompt adherence and composition; the standard general-purpose foundation. | [Hugging Face](https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0) |
| **Stable Diffusion 1.5** | Lightweight, fast workhorse with the biggest LoRA/ControlNet ecosystem; lower native resolution and weaker prompt adherence than modern models. | [Hugging Face](https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive) |
| **Stable Diffusion 2.1** | Native-768 SD2 model; niche today but works perfectly on ComfyUI (auto v-prediction). | [Hugging Face](https://huggingface.co/Comfy-Org/stable_diffusion_2.1_repackaged) |
| **Stable Diffusion 3.5 Large** | The 8.1B SD3.5 Large flagship at full bf16 precision with the fp16 T5 encoder (24 GB). | [Hugging Face](https://huggingface.co/stabilityai/stable-diffusion-3.5-large) |
| **Stable Diffusion 3.5 Large Turbo** | The 4-step distilled SD3.5 Large Turbo at bf16 (24 GB). | [Hugging Face](https://huggingface.co/stabilityai/stable-diffusion-3.5-large-turbo) |
| **Stable Diffusion 3.5 Medium** | 2.5B MMDiT with strong prompt adherence + typography; runs great on ComfyUI (EmptySD3LatentImage, cfg ~4.5). | [Hugging Face](https://huggingface.co/stabilityai/stable-diffusion-3.5-medium) |
| **Z-Image** | Full Z-Image base supporting classifier-free guidance and negative prompts; higher diversity/control than Turbo at the cost of more steps. | [Hugging Face](https://huggingface.co/Comfy-Org/z_image) |
| **Z-Image Turbo** | 8-step distilled ~6B model with near-base quality and notably STRONG instruction/prompt following -- the best model here for anatomical study (specific poses, body positioning, anatomy/figure work). | [Hugging Face](https://huggingface.co/Comfy-Org/z_image_turbo) |
| **Z-Image — NippleDiffusion** | Z-Image base with the NippleDiffusion LoRA applied (NSFW). | [Hugging Face](https://huggingface.co/Comfy-Org/z_image) |

## Editing, inpaint and outpaint

Take an existing image and change it. Multi-turn: each edit builds on the last.

| Model | What it is | Download |
| --- | --- | --- |
| **Anima** 🏷 ×3 | Regenerate a PAINTED region of an existing image with the Anima model — paint the area (e.g. | [Hugging Face](https://huggingface.co/circlestone-labs/Anima) |
| **Anime** | Upscale anime, illustration and other 2D art through AnimeSharpV2's 'Sharp' RealPLKSR network. | [Hugging Face](https://huggingface.co/Kim2091/AnimeSharpV2) |
| **Boogu-Image** | Brand-new (0.1) Apache-2.0 unified edit model. | [Hugging Face](https://huggingface.co/Comfy-Org/Boogu-Image) |
| **Chroma1-Base** | Reinterpret an entire existing image through Chroma1-Base using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/lodestones/Chroma1-Base) |
| **Chroma1-Flash** | Reinterpret an entire existing image through Chroma1-Flash using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/lodestones/Chroma1-Flash) |
| **Chroma1-HD** | Reinterpret an entire existing image through Chroma1-HD using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/Comfy-Org/Chroma1-HD_repackaged) |
| **Chroma1-Radiance** | Reinterpret an entire existing image through Chroma1-Radiance using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but… | [Hugging Face](https://huggingface.co/Comfy-Org/Chroma1-Radiance_Repackaged) |
| **DreamOmni2** | Reference-based instruction editor: give a source image + ONE reference image and an instruction (e.g. |  |
| **FireRed-Image-Edit 1.1** | FireRed's instruction editor on a ~20B Qwen-class backbone, Q5_K_M GGUF (~14.9GB) for 24GB fit. | [Hugging Face](https://huggingface.co/FireRedTeam/FireRed-Image-Edit-1.1) |
| **FLUX.1 Fill** ×2 | Replace a painted region with the model BUILT for inpainting rather than a txt2img base steered by a ControlNet. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.1-Fill-dev) |
| **FLUX.1-dev** | Reinterpret an entire existing image through FLUX.1-dev using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-dev) |
| **FLUX.1-Kontext** | Takes an INPUT IMAGE + a natural-language edit instruction and returns the edited image; preserves identity/style across sequential edits. | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-kontext-dev_ComfyUI) |
| **FLUX.1-Krea** | Reinterpret an entire existing image through FLUX.1-Krea using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/Comfy-Org/FLUX.1-Krea-dev_ComfyUI) |
| **FLUX.1-schnell** | Reinterpret an entire existing image through FLUX.1-schnell using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-schnell) |
| **FLUX.2-dev** | Reinterpret an entire existing image through FLUX.2-dev using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Hugging Face](https://huggingface.co/Comfy-Org/flux2-dev) |
| **FLUX.2-Klein 4B** ×2 | Newest small Flux.2: 4B unified generate+edit model with a Qwen3 encoder, fast (4-step), Apache-licensed, strong complex-prompt understanding for its size. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-4B) |
| **FLUX.2-Klein 4B Base** | Reinterpret an entire existing image through FLUX.2-Klein 4B using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but… | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-base-4B) |
| **FLUX.2-Klein 9B** ×2 | Higher-quality Klein with best-in-family complex-prompt handling; non-commercial. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-9B) |
| **Ideogram 4** | Refine or redraw an existing image through Ideogram 4. | [Hugging Face](https://huggingface.co/Comfy-Org/Ideogram-4) |
| **Krea 2 (Base + Turbo Polish)** | Best-of-both Krea 2: the RAW base gives strong prompt adherence and composition at real CFG, then the distilled Turbo polishes texture and aesthetic over the base latent in a few steps. | [Hugging Face](https://huggingface.co/Comfy-Org/Krea-2) |
| **Krea 2 AnyPaint** ×2 | Arbitrary-mask inpainting on Krea 2 Turbo. | [Hugging Face](https://huggingface.co/Comfy-Org/Krea-2) · [Hugging Face](https://huggingface.co/yijunwang2/krea2-anypaint) |
| **Krea 2 Turbo** | Polish any existing image through Krea 2 Turbo — the source is VAE-encoded to the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept while Turbo reworks texture… | [Hugging Face](https://huggingface.co/Comfy-Org/Krea-2) |
| **LongCat-Image-Edit** | Meituan's instruction editor on a Flux-class backbone, fp8/GGUF-light (transformer ~4.7GB). | [Hugging Face](https://huggingface.co/Comfy-Org/LongCat-Image) |
| **LongCat-Image-Edit Turbo** | Distilled LongCat editor: ~8 steps at CFG 1 for fast drafts/previews. | [Hugging Face](https://huggingface.co/Comfy-Org/LongCat-Image) |
| **Mage-Flow-Edit** | Microsoft's 4B instruction-based image editor. | [Hugging Face](https://huggingface.co/Comfy-Org/Mage-Flow) |
| **Mage-Flow-Edit Turbo** | Few-step distilled Mage-Flow-Edit: 4 steps, cfg 1, ~1 s/edit at 1024x1024 on an A100. | [Hugging Face](https://huggingface.co/Comfy-Org/Mage-Flow) |
| **Photanima** 🏷 | Reinterpret an entire existing image through Photanima using a prompt — the source is used as the init latent and re-sampled at a partial denoise with NO mask, so the composition is kept but the… | [Civitai](https://civitai.com/models/2645333/photanima) |
| **Photo** | Upscale photographic and real-world images through Nomos2's high-quality DAT2 network. | [Hugging Face](https://huggingface.co/Phips/4xNomos2_hq_dat2) |
| **Qwen-Image** ×2 | Regenerate a PAINTED region of an image with base Qwen-Image driven by the InstantX inpainting ControlNet. | [Hugging Face](https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI) |
| **Qwen-Image-Edit** | Clean instruction editor on the 20B Qwen-Image base. | [Hugging Face](https://huggingface.co/Comfy-Org/Qwen-Image-Edit_ComfyUI) |
| **Qwen-Image-Edit (masked)** | Paint a region, give an instruction, and optionally attach reference images — the Qwen edit runs only inside the mask and everything outside is composited back untouched. | [Hugging Face](https://huggingface.co/Comfy-Org/Qwen-Image-Edit_ComfyUI) |
| **SeedVR2** | Restore and upscale an image through SeedVR2's one-step diffusion transformer. | [Hugging Face](https://huggingface.co/Comfy-Org/SeedVR2) |
| **Step1X-Edit (i1258)** | StepFun's original instruction editor (i1258), fp8. | [Hugging Face](https://huggingface.co/stepfun-ai/Step1X-Edit) |

## Video

Animate a still, or generate a clip from a prompt.

| Model | What it is | Download |
| --- | --- | --- |
| **AnimateDiff (SD1.5)** | Animates an anime image into a short clip while STAYING anime (unlike Wan, which realifies). | [Hugging Face](https://huggingface.co/gsdf/Counterfeit-V3.0) |
| **AnimateDiff Lightning (SD1.5)** | Animate the current image into a short clip: SparseCtrl pins the first frame to your image, IP-Adapter keeps the subject, and the motion module animates it. | [Hugging Face](https://huggingface.co/gsdf/Counterfeit-V3.0) |
| **AnimateLCM (SD1.5)** | Animate the current image into a short clip: SparseCtrl pins the first frame to your image, IP-Adapter keeps the subject, and the motion module animates it. | [Hugging Face](https://huggingface.co/gsdf/Counterfeit-V3.0) |
| **ChronoEdit** | NVIDIA's temporal-reasoning editor on a Wan 14B backbone (same class as your Wan 2.2). | [Hugging Face](https://huggingface.co/nvidia/ChronoEdit-14B-Diffusers) |
| **HunyuanVideo** | Text→video on the original HunyuanVideo 13B (Q8_0 GGUF, 24 GB). | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_repackaged) |
| **HunyuanVideo 1.5** ×2 | Uncensored newer video base (HunyuanVideo 1.5). | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 (1080p SR T2V)** | The two-stage HunyuanVideo 1.5 text→video pipeline: generate at 480p, then latent upsampler + 1080p SR distilled model refine to 1080p. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 (1080p SR)** | The official two-stage HunyuanVideo 1.5 pipeline: generate at 480p, then a latent upsampler + 1080p SR distilled model refine to full 1080p. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 (480p T2V)** | Text→video on HunyuanVideo 1.5 at 480p (cfg-distilled fp8): the faster, lighter sibling of the 720p t2v. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 480p T2V Full** | High-quality non-distilled HunyuanVideo 1.5 480p text→video: true CFG with negative prompts, ~30 steps at cfg ~6. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 720p** | Uncensored newer video base (HunyuanVideo 1.5). | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 Fast** | The fast HunyuanVideo 1.5 image-to-video: a step-distilled checkpoint that needs only ~6 steps, several times quicker than the cfg-distilled base at a small quality cost. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 HQ (480p)** | The high-quality non-distilled HunyuanVideo 1.5 480p image-to-video: true classifier-free guidance (negative prompts work), ~30 steps at cfg ~6. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **HunyuanVideo 1.5 HQ (720p)** | The high-quality non-distilled HunyuanVideo 1.5 720p image-to-video: true classifier-free guidance (negatives work), ~30 steps at cfg ~6. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanVideo_1.5_repackaged) |
| **LTX Video** | Fast image-to-video: a ~4s clip in ~20s on 8GB (distilled, 8 steps). | [Hugging Face](https://huggingface.co/Lightricks/LTX-Video) |
| **LTX Video 13B** | The 13B LTX-Video 0.9.8 (distilled fp8) image→video — much higher quality than the 2B 0.9.8 the catalog has, still fast (~8 steps). | [Hugging Face](https://huggingface.co/Lightricks/LTX-Video) |
| **LTX-2** | Newer, larger LTX video model (19B) run as an 11GB Q4 GGUF + Gemma encoder, spilling to RAM on 8GB. | [Hugging Face](https://huggingface.co/Lightricks/LTX-2) |
| **LTX-2 dev** | The NON-distilled LTX-2 19B (dev) image→video — higher quality than the distilled build, at the cost of more steps (~30) and real CFG (~3). | [Hugging Face](https://huggingface.co/Lightricks/LTX-2) |
| **LTX-2.3 22B** | The newest LTX flagship (2.3, 22B) — distilled-1.1, image→video, Q4_K_M GGUF on 24 GB. | [Hugging Face](https://huggingface.co/Lightricks/LTX-2.3) |
| **LTX-2.5 22B** ×2 | The newest LTX flagship (2.5, 22B) — distilled, image→video, the ComfyUI int8-convrot build on 24 GB. | [Hugging Face](https://huggingface.co/Lightricks/LTX-2.5) |
| **LTX-2.5 22B dev** ×2 | The NON-distilled LTX-2.5 22B (dev) image→video — higher quality than the distilled build, at the cost of more steps (~30) and real CFG (~3). | [Hugging Face](https://huggingface.co/Lightricks/LTX-2.5) |
| **MiniMax-H3** ×2 | Animate a still into a clip with NATIVE stereo audio — voice, sound effects and music generated together with the motion. | [Hugging Face](https://huggingface.co/Comfy-Org/MiniMax-H3) |
| **MiniMax-H3 (reference)** | Generate a clip whose SUBJECT is taken from your reference image(s), with NATIVE stereo audio — voice, sound effects and music generated together with the motion. | [Hugging Face](https://huggingface.co/Comfy-Org/MiniMax-H3) |
| **MiniMax-H3 Turbo** ×2 | The fast MiniMax-H3 image→video: the Turbo distill LoRA cuts sampling from ~20 steps to 6 at a small quality cost. | [Hugging Face](https://huggingface.co/Comfy-Org/MiniMax-H3) |
| **MiniMax-H3 Turbo (reference)** | The fast MiniMax-H3 reference→video: the Turbo distill LoRA cuts sampling from ~20 steps to 6 at a small quality cost. | [Hugging Face](https://huggingface.co/Comfy-Org/MiniMax-H3) |
| **SDXL AnimateDiff** | Animates a still image into a short clip using base SDXL and the AnimateDiff SDXL motion module. | [Hugging Face](https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0) |
| **SeedVR2** | Restores and upscales a source video with SeedVR2's temporal diffusion model. | [Hugging Face](https://huggingface.co/Comfy-Org/SeedVR2) |
| **Wan 2.2** | Animates a still image into a short clip (image-to-video). | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Wan 2.2 (Anime LoRA)** | Animate a still into a short clip with an anime-style LoRA on WAN 2.2 TI2V-5B. | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Wan 2.2 (Flat Color)** | Animate a still into a short clip in flat anime-color style (WAN 2.2 TI2V-5B + Flat Color LoRA). | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Wan 2.2 14B** ×2 | The high-quality 14B image→video (two-expert MoE) — much better motion/detail than the 5B TI2V. | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Wan 2.2 14B 720P** ×2 | The 14B image→video at its HIGH resolution tier: the source frame is fitted (at its own aspect) into the official 720P pixel area (1280x720 = 0.92 MP) instead of the 480P tier. | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Wan 2.2 T2V** | Text-to-VIDEO MoE model generating ~5s 720p clips using paired high/low-noise experts. | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |

## Effects and post-processing

Applied to an image or a clip you already have. Several need no diffusion model at all.

| Model | What it is | Download |
| --- | --- | --- |
| **Anima** | Pixel art with ANIMA authoring under reprojection: the per-step pixel-manifold projection clamps the x0 estimate onto a fixed grid+palette every denoise step while Anima (the house-style anime model)… | [Hugging Face](https://huggingface.co/circlestone-labs/Anima) |
| **BiRefNet Matte** ×2 | Removes the background from every frame of a source clip with BiRefNet and outputs a transparent-background animated WEBP (lossless, so the alpha channel survives). |  |
| **Chroma1-HD** | Pixel art authored by Chroma1-HD under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/Chroma1-HD_repackaged) |
| **Deflicker Auto** | Automatically detects and corrects flicker/washed-out frames in an AI-generated clip. |  |
| **DreamOmni2 Pixel** | Pixel art via DreamOmni2's reference editor: the projection runs inside the self-contained pipeline (decode the x0 estimate -> grid+palette quantize -> re-encode -> blend) every denoise step. |  |
| **FireRed-Image-Edit 1.1** | High-quality pixel art via FireRed-Image-Edit 1.1: the ~20B Qwen-class editor redraws while the projection clamps onto a fixed grid+palette every step. | [Hugging Face](https://huggingface.co/FireRedTeam/FireRed-Image-Edit-1.1) |
| **Flux-dev** | The diffusion pixelizer: redraws an image into clean pixel art while clamping the x0 estimate onto a fixed grid+palette every denoise step. | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-dev) |
| **FLUX.1-Kontext** | Pixel art via FLUX.1-Kontext: the source conditions a reference-latent edit while the projection clamps the x0 estimate onto a fixed grid+palette every step. | [Hugging Face](https://huggingface.co/Comfy-Org/flux1-kontext-dev_ComfyUI) |
| **FLUX.1-Krea** | Pixel art authored by FLUX.1-Krea under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/FLUX.1-Krea-dev_ComfyUI) |
| **FLUX.2-dev** | Pixel art authored by FLUX.2-dev under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/flux2-dev) |
| **FLUX.2-Klein 4B** | Pixel art via the fast 4-step FLUX.2-Klein 4B: reference-latent conditioning + per-step grid+palette projection. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-4B) |
| **FLUX.2-Klein 9B** | Pixel art authored by FLUX.2-Klein 9B under per-step reprojection. | [Hugging Face](https://huggingface.co/black-forest-labs/FLUX.2-klein-9B) |
| **HiDream-I1 Full** | Pixel art authored by HiDream-I1 Full under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/HiDream-I1_ComfyUI) |
| **HunyuanImage 2.1 HQ** | Pixel art authored by HunyuanImage 2.1 HQ under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/HunyuanImage_2.1_ComfyUI) |
| **Krea 2** | Pixel art authored by Krea 2 under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/Krea-2) |
| **Line Thicken (anime line-extract)** | Extracts the anime line art with a neural detector, inverts to dark-on-white, boldens the lines, and multiplies them back over the source so only the outlines darken. |  |
| **Line Thicken (ControlNet lineart re-render)** | Re-renders the source as img2img at partial denoise with a lineart ControlNet enforcing the (coarse, bolder) outlines, so the character is preserved while the lines are redrawn clean and thick. | [Hugging Face](https://huggingface.co/gsdf/Counterfeit-V3.0) |
| **Line Thicken (erode)** | Boldens outlines by growing dark pixels with a per-channel 3x3 min filter (the cv2.erode / ImageMagick Erode / Photoshop Minimum algorithm). |  |
| **Line Thicken (sketchKeras)** | Extracts the source's lines with sketchKeras (dark-on-white), boldens them, and multiplies over the source so only the lines darken. |  |
| **Line Thicken (XDoG, outline-only)** | Outline-only thicken: eXtended Difference-of-Gaussians pulls the existing edges out, the line layer is boldened, and it's multiplied back over the source so flat-colour interiors stay clean. |  |
| **LongCat-Image-Edit** | Pixel art via LongCat-Image-Edit: instruction-conditioned redraw with the projection clamping onto a fixed grid+palette every denoise step. | [Hugging Face](https://huggingface.co/Comfy-Org/LongCat-Image) |
| **LongCat-Image-Edit Turbo** | Fast pixel art via the distilled 8-step LongCat editor at CFG 1 — quick drafts; switch to the full LongCat pixelizer for final quality. | [Hugging Face](https://huggingface.co/Comfy-Org/LongCat-Image) |
| **LTX Video 13B Pixel** | Pixel-art image-to-video (locked-palette per-frame quantize). | [Hugging Face](https://huggingface.co/Lightricks/LTX-Video) |
| **LTX Video Pixel** | Pixel-art image-to-video (locked-palette per-frame quantize). | [Hugging Face](https://huggingface.co/Lightricks/LTX-Video) |
| **LTX-2 dev Pixel** | Pixel-art image-to-video (locked-palette per-frame quantize). | [Hugging Face](https://huggingface.co/Lightricks/LTX-2) |
| **LTX-2 Pixel** | LTX-2 image-to-video with the deterministic pixel-art quantizer applied to every decoded frame. | [Hugging Face](https://huggingface.co/Lightricks/LTX-2) |
| **LTX-2.3 22B Pixel** | LTX-2.3 (newest LTX) image-to-video, pixel-quantized per frame with a locked palette. | [Hugging Face](https://huggingface.co/Lightricks/LTX-2.3) |
| **Lumina-Image 2.0** | Pixel art authored by Lumina-Image 2.0 under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/Lumina_Image_2.0_Repackaged) |
| **Pixel Quantize** ×2 | Pixel-quantizes every frame of a source clip onto a fixed grid + palette and re-encodes the clip at its own frame rate. |  |
| **Pixel Quantize (batch)** | Pixel-quantizes N still frames together, deriving ONE global fp palette + label frequencies across the whole set (temporally consistent — no frame-to-frame flicker) and emitting the per-frame… |  |
| **Qwen-Image-Edit** | Generates pixel art directly from a reference image: QIE redraws per the instruction while the projection clamps onto a fixed grid+palette every denoise step, so the model produces manifold-friendly… | [Hugging Face](https://huggingface.co/Comfy-Org/Qwen-Image-Edit_ComfyUI) |
| **Stable Diffusion 3.5 Large** | Pixel art authored by Stable Diffusion 3.5 Large under per-step reprojection. | [Hugging Face](https://huggingface.co/stabilityai/stable-diffusion-3.5-large) |
| **VAE Round-trip (Qwen diagnostic)** | Developer diagnostic that reconstructs the source through the bound Qwen VAE without diffusion, prompting, sampling, or application-side resizing. |  |
| **Wan 2.2 14B Pixel** | Pixel-art image-to-video (locked-palette per-frame quantize). | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Wan 2.2 Pixel** | Pixel-art image-to-video (locked-palette per-frame quantize). | [Hugging Face](https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged) |
| **Z-Image** | Pixel art authored by Z-Image under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/z_image) |
| **Z-Image Turbo** | Pixel art authored by Z-Image Turbo under per-step reprojection. | [Hugging Face](https://huggingface.co/Comfy-Org/z_image_turbo) |
