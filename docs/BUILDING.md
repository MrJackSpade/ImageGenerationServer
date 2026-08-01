# Building from source

**This is the least supported way to run it.** If you only want to use the app, take a
[release build](../INSTALL.md#releases) or [Docker](../INSTALL.md#docker) instead — both are packaged, and
neither needs a toolchain. Build from source when you are changing the code.

- [Prerequisites](#prerequisites)
- [Build and run](#build-and-run)
- [ComfyUI](#comfyui)
- [Building the Docker image](#building-the-docker-image)
- [Tests](#tests)
- [The analyzers](#the-analyzers)
- [Regenerating the model list](#regenerating-the-model-list)

---

## Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- A ComfyUI you can reach, started with `--enable-cors-header`.
- A GPU, for ComfyUI's benefit rather than the app's — NVIDIA, AMD via ROCm, Intel via XPU or Apple Silicon.
  Nothing in this application talks to the GPU.

---

## Build and run

```bash
git clone --depth 1 https://github.com/MrJackSpade/ImageGenerationServer.git
cd ImageGenerationServer

dotnet run --project src/ImageGen.Web
```

The first run fetches the tag model (~900 MB) into `tagmodel/artifacts` beside the built output and verifies it
against published checksums; later runs skip whatever is already there. ffmpeg is a NuGet dependency, not a
download.

The app defaults to SQL Server. For a development checkout you want SQLite, which needs no server and no
schema step:

```bash
export Database__Provider=Sqlite
export ConnectionStrings__ImageGen="Data Source=imagegen.db"
```

> `git clone --depth 1` is not incidental. The full history is several GB — it once carried a 54 MB tag file
> and an 868 MB LFS checkpoint, both since removed from the working tree, and the blobs remain in the pack.

---

## ComfyUI

Your ComfyUI **must** be started with `--enable-cors-header`, and needs this repo's **patches** — every change
the app makes to a ComfyUI installation, from the core quantised-controlnet fix to the node packs it ships:

```bash
dotnet run --project tools/ComfyPatch -- list  --root /path/to/ComfyUI     # what's in place
dotnet run --project tools/ComfyPatch -- apply --root /path/to/ComfyUI --all \
    --python /path/to/ComfyUI/venv/bin/python                              # --python installs pack requirements
```

The same thing is on **Settings → Renderer patches** once the app is running; the tool exists because the
container build uses it, and it links the same engine, so the two cannot disagree about what is applied.

`imagegen_gate` among them makes ComfyUI reject submissions that do not carry this app's token, so the app's
fair queue is the only thing that can enqueue work on the GPU. **Restart ComfyUI afterwards** — it scans
`custom_nodes/` and imports every module only at startup, so nothing applied here takes effect until it does.

Where the patches come from, and how to change one:

| | |
|---|---|
| `comfy-patches/*.patch` | Authored diffs against code somebody else owns — ComfyUI core, and third-party packs. A metadata header, a line reading `---`, then a unified diff. A patch with `Source:`/`Rev:` downloads its pack at that pinned commit if it is missing. |
| `comfy-nodes/<pack>/` + `packs.json` | The node packs this repo owns. Ordinary `.py` files — edit them directly. They are turned into add-everything diffs in memory, so there is no generated file to keep in step. A pack with no `packs.json` entry is not a patch and never reaches ComfyUI. |

`scripts/export-comfy-patches.ps1` re-exports the authored diffs from a live checkout. It is a dry run by
default, and the default matters: the shipped patches carry corrections the checkout may not have.

---

## Building the Docker image

`docker compose --profile nvidia up` builds the image the first time on its own, so you rarely build by
hand. Do it deliberately when you have changed the code, want a different ComfyUI release baked in, or are
cutting a version-stamped release image:

```bash
docker compose --profile nvidia build      # or --profile amd, which uses Dockerfile.rocm
```

Building needs Docker and nothing else — **not a GPU.** Nothing in the build talks to one; the GPU and its
container toolkit are only needed to *run* the result. It builds from a clean checkout on any host, Windows
included, and that is arranged rather than accidental: `.dockerignore` keeps a host `bin/`/`obj/` out of the
build context — a Windows `obj/project.assets.json` names a NuGet fallback folder the Linux build image does
not have, and copying it in breaks the container's own `restore` — and `.gitattributes` forces the
entrypoint script to LF, so a CRLF checkout does not ship a `#!/usr/bin/env bash\r` shebang the container
cannot execute.

One build, in order: `dotnet publish` the app, clone ComfyUI at the pinned tag and install PyTorch for the
profile's accelerator, then apply every patch and node pack with the **same tool the settings page uses** —
installing each pack's `requirements.txt` through ComfyUI's interpreter as it goes. **A patch that will not
apply fails the build.** That is the point: the alternative is a container that quietly lacks a fix nobody
notices is gone until a render is wrong.

Two build args, both with sensible defaults:

| arg | default | what it does |
|---|---|---|
| `COMFYUI_REF` | `v0.28.0` | The ComfyUI release the image bakes. Pinned so the backend cannot change between builds; bump it deliberately. |
| `IMAGEGEN_VERSION` | *(empty)* | The version the image reports. Empty is correct for an image built from a working copy — it is not a point on the release line, so the update banner stays quiet rather than comparing a made-up number against published releases. Pass it only for a release image. |

```bash
docker compose --profile nvidia build \
    --build-arg IMAGEGEN_VERSION=0.6.0 --build-arg COMFYUI_REF=v0.28.0
```

The CUDA, PyTorch and ComfyUI layers are cached against their own inputs, so a rebuild after a code change
re-runs the publish and the patch step, not the multi-gigabyte backend install.

---

## Tests

```bash
dotnet build
dotnet test                                    # 335 tests, on SQLite, no database server needed
IMAGEGEN_TEST_SQLSERVER=1 dotnet test          # the same tests against SQL Server LocalDB
```

Both provider runs must pass — that equivalence is the entire claim behind "runs on either", and half the
engine-specific landmines are invisible on whichever one you did not run. CI runs both on every push; the
SQL Server job points at a container via `IMAGEGEN_TEST_SQLSERVER_MASTER` / `_DB` instead of LocalDB.

The tag model parity tests skip themselves unless the artifacts are present, so the suite runs on a bare
checkout. A skip is not a pass: if they skip after the app has started once, the download did not land beside the
built output.

---

## The analyzers

Two custom analyzers are build errors, not warnings:

- **`IMGDOC001`** — a comment attached to a type or member must be `///`, not `//`.
- **`IMGDB001` / `IMGDB002`** — no provider-typed database reads. `(int)ExecuteScalarAsync(...)` and
  `reader.GetByte(...)` fail the build; use the converting helpers in `DbValueExtensions`. SQLite returns
  `long` for every integer, so these are runtime failures on one engine that the compiler cannot otherwise
  see.

---

## Regenerating the model list

[`docs/MODELS.md`](MODELS.md) is generated from the catalogue, not maintained by hand. After changing
anything under `configurations/`:

```bash
python tools/gen-models-doc.py
```

---

[`ARCHITECTURE.md`](../ARCHITECTURE.md) is the authority on the design — the components, what state lives
where, and the invariants a change has to preserve. Read it before changing anything structural.
