# Make a Picture

A self-hosted, multi-user web front end for image generation, backed by a local **ComfyUI**
instance. It replaces ComfyUI's node graph with a single prompt box: type a description, generate,
and the result is saved to your account. Includes user accounts, generation history, and bookmarks.

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Booru models](#booru-models)
- [Data and storage](#data-and-storage)
- [Architecture](#architecture)
- [License](#license)

---

## Features

- **Prompt to image.** [95 models](docs/MODELS.md) are supported out of the box, including Flux,
  Qwen, Chroma, SDXL, Z-Image, and HiDream.
- **Iterative editing.** Describe a change to an existing image; each edit builds on the previous
  one. Inpainting and outpainting are included.
- **Per-user history and bookmarks**, stored server-side and available from any device.
- **Shared GPU queue.** One GPU is shared across users, round-robin by owner, with live progress and
  an ETA based on how long each workflow has historically taken on the machine.
- **Persistent generations.** A generation outlives the tab that started it. It stays available after
  you close the tab, switch devices, or ComfyUI clears its output folder.

---

## Requirements

These apply to every installation method:

- **A GPU.** The app never uses the GPU directly — ComfyUI does — so the vendor is whatever ComfyUI
  supports: NVIDIA (CUDA), AMD (ROCm), Intel (XPU), or Apple Silicon.
- **A ComfyUI instance** started with `--enable-cors-header`. The Docker image bundles one; the
  release and from-source builds connect to a ComfyUI you run yourself.
- **Your own image models.** Checkpoints, LoRAs, and VAEs run to hundreds of gigabytes and are not
  downloaded for you. Until ComfyUI reports the files present, the workflow list is short or empty.
  See [docs/MODELS.md](docs/MODELS.md) for what is supported.

---

## Installation

[**INSTALL.md**](INSTALL.md) has the full instructions, the configuration reference, and a
first-generation walkthrough. The three methods, in brief:

### Release build

Recommended. [Download the archive](https://github.com/MrJackSpade/ImageGenerationServer/releases)
for your platform, unpack it, and run the launcher — `start.bat` on Windows, `start.sh` on Linux. It
carries its own .NET runtime and downloads the tag model on first run. Open <http://localhost:8080>
and create an account.

### Docker

The only method that does not need a ComfyUI already running, because the image bundles one. In
exchange, you supply a **models directory** in ComfyUI's standard layout:

```bash
mkdir -p ~/imagegen-models/{checkpoints,diffusion_models,text_encoders,vae,loras,clip_vision,controlnet,upscale_models}
# place your model files in the matching folders
cp .env.example .env                   # set COMFY_MODELS_DIR to that directory
docker compose --profile nvidia up     # or: --profile amd
```

[INSTALL.md](INSTALL.md#docker) documents the full directory tree. The AMD image builds in CI but has
not been run on real AMD hardware.

### From source

For building the app from source instead of running a release. See [docs/BUILDING.md](docs/BUILDING.md).

---

## Booru models

A few models are trained on booru tags (`1girl, solo, looking at viewer`) rather than prose: the
Anima family, Pony Diffusion V6 XL, AutismMix Confetti, Photanima, and Pixelanima, marked 🏷 in
[the model list](docs/MODELS.md). The features below apply only to these models. For every other
model you type an ordinary sentence, and none of this appears.

- **Context-aware autocomplete.** After `1girl, solo`, suggestions are ranked by P(tag | prompt) from
  a 639k-tag set-transformer running in-process — not a static word list.
- **Whole-prompt generation.** The same model expands what you have typed into a complete prompt,
  stopping when it judges the tag set finished rather than at a fixed length. A temperature control
  sets how varied the result is.
- **Prompt markers.** `#tag`, `@artist`, `!quiet`, and `~guide` declare which part of your input is a
  tag, which is an artist, and which should steer suggestions without being sent to the image model.
  The in-app help page documents the syntax.
- **Tag bans.** A tag a model keeps producing unprompted is suppressed at the sampler rather than
  stripped afterward, so the prompt completes to a real alternative. Bans apply only to generated
  tags, never to a tag you typed yourself.

---

## Data and storage

**Images are stored in the database, not on disk.** Each generation is therefore a single durable
record with one id, reachable from any device and unaffected by ComfyUI rotating its output folder.
It also means the database *is* your images — back it up.

**Prompts and tags are encrypted at rest** under a per-user key. The scope of that is worth stating
precisely. Each user's key is stored in the same database as their data, so the running app can
always decrypt, and so can anyone holding a full copy of the database. This is not end-to-end
encryption, and it is not protection from whoever runs the server. What it does is keep the columns
out of plaintext: a stray backup, a glance at the tables, or a query run for an unrelated reason does
not reveal what anyone typed. Tags that must stay searchable are encrypted deterministically, which
reveals which rows share a tag but not what the tag is. It guards against casual exposure, the threat
it was built for.

**Uploads are never persisted.** Edit sources, reference images, and masks are render inputs, held in
memory until the job runs. They are not written to the database and cannot be retrieved as outputs.

---

## Architecture

```
   ┌─────────────────────────────────────────────────────┐
   │  ImageGen.Web (ASP.NET Core)                 :8080  │
   │                                                     │
   │    /api      per-user history, bookmarks, settings  │
   │    /forge    workflow routing, job queue,           │
   │              live progress over /forge/ws           │
   │    tag model, in-process (ONNX Runtime)             │
   │                                                     │
   │  Database:  SQLite file, or SQL Server              │
   └─────────────────────────────────────────────────────┘
                              │ HTTP
                              ▼
              ComfyUI  →  :8188          renders the images
                              │
                              ▼
              your image models          checkpoints, LoRAs, VAEs
```

The whole app is a single process serving one origin, backed by one database. The browser calls
`/api` for per-user data and `/forge` for rendering; `/forge` drives ComfyUI, which must run with
`--enable-cors-header`.

Non-browser callers (scripts, the MCP) authenticate with a **per-user** API key — `AppUser.ApiKey`,
sent as `X-Api-Key` or `Authorization: Bearer` — and act as that user. There is no app-wide key.

---

## License

**Source-available — personal, non-commercial use only. No modification or redistribution.
See [LICENSE](LICENSE).** You may run and host it for your own personal, non-commercial use. You may
not use it commercially, modify it, or pass it on — publishing, selling, rehosting, or offering it to
others as a service is redistribution. The source is published to be read and run, not changed. It is
not open source.

Bundled third-party components — ffmpeg, ComfyUI, the tag model — retain their own licenses, which
this project neither alters nor can alter. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
