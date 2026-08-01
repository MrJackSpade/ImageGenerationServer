# Installing ImageGen

> Source-available — personal, non-commercial use only ([LICENSE](LICENSE)). Yours to run; not to sell, modify, or pass on.

Three ways in, best first:

1. **[A release build](#releases)** — compiled binaries, nothing to install. Start here.
2. **[Docker](#docker)** — brings up its own ComfyUI as well.
3. **[From source](docs/BUILDING.md)** — for changing the code, not for running it.

Every path needs a GPU (NVIDIA, or AMD via ROCm), a ComfyUI, and **your own image models**. Checkpoints, LoRAs and VAEs are hundreds of gigabytes,
they are yours, and nothing here downloads them. See [docs/MODELS.md](docs/MODELS.md) for what the app supports.

> This document covers getting the application running. Whether it runs in the background, starts at boot, or is
> reachable from anywhere other than the machine it is on, is yours to arrange.

---

## Releases

**Needs:** a GPU and a ComfyUI started with `--enable-cors-header`. Nothing else — the archive carries its own
.NET runtime. The app never touches the GPU itself; whatever ComfyUI runs on is what matters, so NVIDIA (CUDA),
AMD (ROCm), Intel (XPU) and Apple Silicon all work as far as this app is concerned.

Download the archive for your platform from
[**Releases**](https://github.com/MrJackSpade/ImageGenerationServer/releases), unpack it, and run the launcher:

```text
start.bat                     Windows — double-click it, or run it from a prompt
```
```bash
./start.sh                    # Linux
```

It opens your browser once it is listening; if it cannot, the address is printed in the window. The first page
asks for your ComfyUI's address, and then you create an account. `IMAGEGEN_OPEN_BROWSER=0` stops it opening one —
only the launchers ask for a browser, so a container or a service never does.

ffmpeg is not a prerequisite — it is linked into the application. The launcher selects SQLite and puts everything —
accounts, history and the images themselves — in `imagegen.db` beside the executable. **That file is your data;
back it up.**

`start.bat` is a wrapper around `start.ps1`. Windows will not run a `.ps1` by double-click, and refuses to run one
at all when it came out of a downloaded archive, so the `.bat` is the way in; the `.ps1` is there for running from
a shell.

The archive is the launchers, a README and the licences at the top, and the program in `bin/`. Your data stays at
the top with them: `imagegen.db` and `logs/`.

The first start downloads the tag model (~900 MB) and verifies it against published checksums. The app does this
itself; later starts skip anything already there. There is nothing to install.

A release archive knows its own version, so it checks **once per start** whether a newer release has been
published and shows a banner if so. Dismissing it lasts the browser session. It never updates itself — the
banner links to the release, and installing it is unpacking the new archive over your own. Turn the check off
under **Settings → This machine** if you would rather the box did not contact GitHub.

To point at a ComfyUI somewhere else, use the address box on first run, or change it later under
**Settings → This machine**.

---

## Docker

**Needs:** Docker with Compose and a GPU. There are two images — pick the profile that matches your card.

#### First, a models directory

A release build reuses the `models` directory your existing ComfyUI already has. **Docker brings its own ComfyUI**,
so you provide the models directory it mounts (read-only — it is never copied into the image). If you do not already
run ComfyUI you will not have one, so create it with ComfyUI's standard sub-folders and drop each model into the
folder for its kind:

```text
your-models/
├── checkpoints/       SD1.5, SDXL, SD3.5, Pony, Illustrious … all-in-one checkpoints
├── diffusion_models/  Flux, Qwen, Chroma, Wan, HunyuanVideo … unet/DiT weights (incl. .gguf)
├── text_encoders/     CLIP-L/G, T5, UMT5, Gemma, Qwen-VL … text & multimodal encoders
├── vae/               VAEs
├── loras/             LoRAs
├── clip_vision/       CLIP-vision (image-to-video, IP-Adapter)
├── controlnet/        ControlNets
└── upscale_models/    ESRGAN / DAT / … upscalers
```

```bash
mkdir -p your-models/{checkpoints,diffusion_models,text_encoders,vae,loras,clip_vision,controlnet,upscale_models}
```

(A workflow that needs another of ComfyUI's standard folders — `animatediff_models`, `ipadapter`, … — just wants
that folder added the same way.) Files can be named anything: the app recognises most models by their published
name and binds the rest by hand on the **Models** page. [docs/MODELS.md](docs/MODELS.md) lists what the catalogue
supports; nothing here downloads models — they are yours to place.

#### Then bring it up

```bash
git clone --depth 1 https://github.com/MrJackSpade/ImageGenerationServer.git
cd ImageGenerationServer
cp .env.example .env                  # set COMFY_MODELS_DIR to the your-models directory above

docker compose --profile nvidia up    # NVIDIA — needs the NVIDIA container toolkit
docker compose --profile amd up       # AMD — needs ROCm-capable hardware
```

There is no default profile on purpose: the two need different devices, and a default that starts the wrong one
fails at the first render with an error about the GPU rather than about the profile.

> **The AMD image is untested on real hardware.** It is built in CI on every push, so it compiles, resolves and
> installs — but nobody here owns an AMD card, so it has never rendered an image. `Dockerfile.rocm` pins ROCm 7.2
> to match the PyTorch wheel channel ComfyUI's own AMD instructions use. Cards ROCm does not officially support
> often work once `HSA_OVERRIDE_GFX_VERSION` is set (10.3.0 for RDNA2, 11.0.0 for some RDNA3); it is passed
> through from `.env`. Treat a failure as a bug worth reporting.

Then open <http://localhost:8080> and create an account.

First run downloads the tag model into a named volume, and the image build installs CUDA, PyTorch and ComfyUI —
so expect the first `up` to be a long one. Subsequent starts are quick.

The image carries ComfyUI because that is what makes a fresh install render something. To use your own instead, set
`ComfyUI__BaseUrl` to point at it. ComfyUI is pinned to a release tag (`COMFYUI_REF`) so the image does not silently
change backend between builds.

It is upstream's release **plus this project's patches** — a fix to ComfyUI's own quantised-controlnet loading, the
node packs the app needs, and fixes for a few third-party packs, all applied during the build. **Settings → Renderer
patches** lists them, and can remove, re-apply and restart ComfyUI so it picks the changes up. Changes made there live
in the container's writable layer, so they last until the container is recreated; the page says so.

`up` builds the image the first time on its own. To build it deliberately — a code change, a different ComfyUI
release (`COMFYUI_REF`), or a version-stamped release image (`IMAGEGEN_VERSION`) — see
[building the Docker image](docs/BUILDING.md#building-the-docker-image).

`.env` is the whole configuration surface for this path; every setting in it is commented.

---

## From source

See **[docs/BUILDING.md](docs/BUILDING.md)** to build the app from source. To simply run it, use a release
build or Docker.

---

## Configuration

**Almost everything is set in the app, under Settings → This machine.** Those values are stored in the database
against this machine's name, and most take effect as you save them; the page says which of them need a restart.
They are not in `appsettings.json` — a key lives in one place.

`appsettings.json` holds only what has to be known before the app can read its own settings out of the database:

| Key | Default | What it is |
| --- | --- | --- |
| `ConnectionStrings:ImageGen` | `Data Source=imagegen.db` | The database. A connection string for SQL Server. |
| `Database:Provider` | `Sqlite` | `Sqlite` or `SqlServer`. Must agree with the connection string; the app refuses to start if they disagree. |
| `Database:EnsureSchemaOnStartup` | `true` on SQLite | Create missing tables at startup. |
| `Urls` | `http://0.0.0.0:8080` | The address to listen on. |
| `Kestrel:Limits:MaxRequestBodySize` | `536870912` | Largest upload accepted. Keep in step with nginx's `client_max_body_size`. |

Set in the app, under **Settings → This machine**:

| Setting | Default | What it is |
| --- | --- | --- |
| Renderer address | *none — asked for on first run* | Your ComfyUI. |
| Renderer queue token | built-in | Must match `IMAGEGEN_GATE_TOKEN` in ComfyUI's `imagegen_gate` node. |
| Renderer folder | empty | Where ComfyUI is **installed**, as opposed to where it listens. Only the patches page uses it; leave it empty if the renderer is on another machine. |
| Renderer Python | empty | That ComfyUI's interpreter. Used only to install the requirements of a node pack a patch has just fetched. |
| Registration code | empty | A shared code required to register. Empty means open sign-up. |
| Free-memory floor | `500` MB | Refuse new work below this much free memory. |
| Check for updates | on | Asks github.com **once per start** whether a newer release exists, and shows a dismissable banner if so. Turn it off to stop this box contacting GitHub at all. A build with no version — anything not from a release archive — never checks. |
| Expose stack traces | on | Full exception in 500 bodies. |
| Trust all proxies | on | Honour `X-Forwarded-*` from any caller. |
| Run the reconciler | on | Reaps stale pending-job rows. |
| Log file, log level | `logs/imagegen-.log`, `Information` | Rolling-by-day log file. Blank the path to turn it off. |

The two file-held keys can be edited from that page too; it writes them to `appsettings.<Environment>.json`
(gitignored) and they apply on the next restart. Environment variables use `__` for `:` —
`ConnectionStrings__ImageGen`.

`Auth:RegistrationCode` matters solely if something other than you can reach the port — see
[If other people can reach it](#if-other-people-can-reach-it).

### If other people can reach it

The defaults are for a machine you trust, which is what this has always been. Before exposing it:

- Set **`Auth:RegistrationCode`**. Otherwise anyone who can reach the port can create an account.
- Set **`Diagnostics:ExposeStackTraces=false`**. Stack traces still go to the log file; they stop going to the browser.
- Set **`Security:TrustAllProxies=false`** unless a reverse proxy you control is the only route in. `X-Forwarded-*`
  headers are trivially spoofable by anything that can reach the app directly.

### SQLite vs SQL Server

SQLite needs no server, no schema step and no setup, and is what the release launcher and Docker both select.
**It permits exactly one writer, so exactly one app instance may point at a given database file.** Everything is in
that one file — accounts, history, and the images themselves — so back it up.

SQL Server supports several app instances sharing one database. It expects the schema to be applied out-of-band
(`src/ImageGen.Infrastructure/Database/schema.sql`), because the app's login is not granted DDL rights.

---

## Your first generation

A fresh install has **no image models**, so the workflow list will be short or empty: a workflow only appears once
you have pointed its slots at files on your disk. [docs/MODELS.md](docs/MODELS.md) lists everything the
catalogue supports.

To get from nothing to a picture: put a checkpoint in ComfyUI's `models/checkpoints/`, restart ComfyUI, then open
**Models**. It lists every workflow with the reason it is or is not available, and every model slot with the file
bound to it — recognised ones are already filled in, the rest are a dropdown away.

> **Your files can be named anything.** The app recognises many models by their published name and binds them
> automatically; anything it cannot place is bound by hand, and nothing requires renaming a file or editing a
> shipped one. Adding your own model or workflow is a matter of dropping a file into `configurations/models/` or
> `configurations/workflows/` — documented, but not supported.

---

## Licensing

Source-available — personal, non-commercial use only ([LICENSE](LICENSE)). The ffmpeg linked into the app is the **LGPL** build,
which is what allows it to ship alongside proprietary code; ComfyUI is GPL and runs as a separate process. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
