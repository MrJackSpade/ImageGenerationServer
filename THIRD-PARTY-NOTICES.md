# Third-party components

Things this project **distributes or downloads on your behalf**, and what each one obliges you to do. Ordinary NuGet
dependencies are not listed — they are permissively licensed, resolved from nuget.org at build time, and carry their
own licences in the packages.

---

## ffmpeg — LGPL, linked in-process

The app links ffmpeg through [Loxifi.FFmpeg](https://github.com/Loxifi/Loxifi.FFmpeg), whose `Runtime.*` packages
carry the native libraries. It is used to encode animated WebP clips into H.264 MP4 for in-browser `<video>`
playback.

The native libraries ship as separate `.dll`/`.so` files, not statically linked. The LGPL builds omit `libx264`;
H.264 comes from Cisco's OpenH264.

| | |
| --- | --- |
| Wrapper | [Loxifi.FFmpeg](https://github.com/Loxifi/Loxifi.FFmpeg) (MIT) |
| Native libraries | `Loxifi.FFmpeg.Runtime.win-x64` / `.linux-x64` — ffmpeg 7.1 |
| Upstream | <https://ffmpeg.org/> |
| Licence | LGPL-2.1-or-later |
| H.264 encoder | OpenH264 (BSD-2-Clause), <https://github.com/cisco/openh264> |

The Docker images still `apt-get install ffmpeg`. That copy is **for ComfyUI**, which shells out to it for video
workflows; it is a separate program and unrelated to what the app links.

---

## s2srec2 tag model

Downloaded by the install script into `tagmodel/artifacts` beside the app. Provides the context-aware tag autocomplete and
whole-prompt generation, and — since `tags.json` was retired — the tag vocabulary the whole app uses.

| | |
| --- | --- |
| Published at | <https://huggingface.co/mrjackspade/s2srec2-booru-tags> |
| Licence | CC0-1.0 |
| Runtime | ONNX Runtime (MIT), in-process. No PyTorch at runtime. |

Trained on booru tag co-occurrence; its vocabulary is booru tags.

---

## ComfyUI (Docker image only)

The Docker image installs ComfyUI so a fresh `docker compose up` can render. The app drives it over HTTP as a
separate program.

| | |
| --- | --- |
| Upstream | <https://github.com/comfyanonymous/ComfyUI> |
| Licence | GPL-3.0 |

Distributing an image that contains ComfyUI carries GPL-3.0 obligations for that copy. The image's copy is patched —
see below — and `comfy-patches/010-core-quant-controlnet.patch` is the modification, in source form.

**Image models are never included** by anything here. Checkpoints, LoRAs and VAEs are yours, bind-mounted, and carry
their own licences.

---

## ComfyUI node packs (`comfy-nodes/`)

Installed into ComfyUI's `custom_nodes/` as patches. A mix of first-party code and vendored third-party packs; each
retains its own licence where it has one — `ComfyUI-GGUF/LICENSE`, for instance. `imagegen_gate`, `ComfyUI-ModelPin`,
`ComfyUI-CondCache`, `ComfyUI-ColorCorrectedComposite` and `ComfyUI-PixelHarness` are first-party. PixelHarness
vendors sketchKeras-pytorch under `vendor/`, which carries its own LICENSE.

---

## Third-party node packs installed by `comfy-patches/`

**None of these are distributed here.** What ships is a pinned upstream revision — and, for the four marked
*patched*, a diff against it. Applying the patch downloads that commit from the upstream below onto your disk.
Each pack keeps its own licence, which this project neither alters nor redistributes under.

| Pack | Upstream | |
| --- | --- | --- |
| `ComfyUI_RH_DreamOmni2` | <https://github.com/HM-RunningHub/ComfyUI_RH_DreamOmni2> | patched |
| `ComfyUI_Step1X-Edit` | <https://github.com/raykindle/ComfyUI_Step1X-Edit> | patched |
| `Comfy_HunyuanImage3` | <https://github.com/EricRollei/Comfy_HunyuanImage3> | patched |
| `ComfyUI-ZImage-Triton` | <https://github.com/newgrit1004/ComfyUI-ZImage-Triton> | patched |
| `comfyui_controlnet_aux` | <https://github.com/Fannovel16/comfyui_controlnet_aux> | |
| `ComfyUI-AnimateDiff-Evolved` | <https://github.com/Kosinkadink/ComfyUI-AnimateDiff-Evolved> | |
| `ComfyUI-Advanced-ControlNet` | <https://github.com/Kosinkadink/ComfyUI-Advanced-ControlNet> | |
| `ComfyUI_IPAdapter_plus` | <https://github.com/cubiq/ComfyUI_IPAdapter_plus> | |
| `ComfyUI-Anima-LLLite` | <https://github.com/kohya-ss/ComfyUI-Anima-LLLite> | |
| `ComfyUI-SeedVR2_VideoUpscaler` | <https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler> | |
| `ComfyUI-HunyuanVideoWrapper` | <https://github.com/kijai/ComfyUI-HunyuanVideoWrapper> | |
| `ComfyUI-JoyAI-Image-Edit` | <https://github.com/judian17/ComfyUI-JoyAI-Image-Edit> | |
| `ComfyUI-Conditioning-Rebalance` | <https://github.com/nova452/ComfyUI-Conditioning-Rebalance> | |

Both container images apply the full set during their build, so a **distributed image does contain every pack
above**, under its own licence.

---

## This project's own licence

**Source-available — personal, non-commercial use only. No modification or redistribution. See [LICENSE](LICENSE).**
The components above keep their own licences, which it neither alters nor can alter.
