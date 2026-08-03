# ImageGen: the app, its tag model, and ComfyUI, in one image.
#
# ComfyUI is INCLUDED here so a fresh `docker compose up` renders something without the user standing up a backend
# first. It is upstream's release, plus this repo's patches (comfy-patches/ and the node packs in comfy-nodes/),
# applied below by the same engine the settings page uses. Those patches can be listed, removed and re-applied at
# Settings -> Renderer patches, and this image can restart ComfyUI on request -- see docker/entrypoint.sh. Beyond
# that restart there is no supervision: if ComfyUI dies, the container goes with it.
#
# The image is deliberately large (CUDA + PyTorch + ComfyUI puts it in the multi-GB range). That is the cost of it
# working on first run. Point ComfyUI__BaseUrl at your own ComfyUI instead if you would rather not carry it.
#
# Image MODELS are never baked in — they are hundreds of GB and they are yours. Bind-mount them (see compose.yml).

# --- build ------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so `restore` is cached against dependency changes rather than every source edit.
COPY Directory.Build.props ImageGen.slnx ./
COPY src/ImageGen.Analyzers/ImageGen.Analyzers.csproj      src/ImageGen.Analyzers/
COPY src/ImageGen.Domain/ImageGen.Domain.csproj            src/ImageGen.Domain/
COPY src/ImageGen.Application/ImageGen.Application.csproj  src/ImageGen.Application/
COPY src/ImageGen.Infrastructure/ImageGen.Infrastructure.csproj src/ImageGen.Infrastructure/
COPY src/ImageGen.Comfy/ImageGen.Comfy.csproj              src/ImageGen.Comfy/
COPY src/ImageGen.Media/ImageGen.Media.csproj              src/ImageGen.Media/
COPY src/ImageGen.TagModel/ImageGen.TagModel.csproj        src/ImageGen.TagModel/
COPY src/ImageGen.Api/ImageGen.Api.csproj                  src/ImageGen.Api/
COPY src/ImageGen.Web/ImageGen.Web.csproj                  src/ImageGen.Web/
RUN dotnet restore src/ImageGen.Web/ImageGen.Web.csproj

COPY src/ src/
COPY comfy-patches/ comfy-patches/
COPY comfy-nodes/ comfy-nodes/

# The version this image reports. Empty by default and that is deliberate: an image built from a working copy
# is not a point on the release line, and the update banner stays silent rather than comparing a made-up number
# against published releases. Pass --build-arg IMAGEGEN_VERSION=0.6.0 when building a release image.
ARG IMAGEGEN_VERSION=
RUN dotnet publish src/ImageGen.Web/ImageGen.Web.csproj -c Release -o /app --no-restore \
        ${IMAGEGEN_VERSION:+-p:Version=$IMAGEGEN_VERSION}

# The patch tool, published separately. It is what applies the patches below, and it links the SAME engine the
# settings page runs -- one implementation, so the image cannot end up in a state its own UI misreads.
COPY tools/ComfyPatch/ tools/ComfyPatch/
RUN dotnet publish tools/ComfyPatch/ComfyPatch.csproj -c Release -o /comfy-patch

# --- final ------------------------------------------------------------------------------------------
# CUDA runtime, because ComfyUI lives here and needs the GPU. The .NET runtime is installed on top rather than
# starting from the aspnet image, since only one of the two bases can be inherited.
FROM nvidia/cuda:12.6.2-runtime-ubuntu24.04 AS final

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# ffmpeg here is for COMFYUI, which shells out to it for video workflows. The app no longer needs it: it links
# its own LGPL ffmpeg through Loxifi.FFmpeg. See THIRD-PARTY-NOTICES.md.
#
# build-essential + python3-dev are for COMFYUI too, specifically Triton: torch reaches Triton for some kernels
# (a bmm_outer_product inside CLIPTextEncode, among others), and Triton JIT-compiles a launcher module on first
# use — which fails with "Failed to find C compiler" on the -runtime base, since it carries no toolchain and no
# Python headers. Triton bundles its own ptxas, so the C compiler and Python.h are the whole gap.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl git ffmpeg \
        build-essential python3 python3-pip python3-venv python3-dev \
        libicu74 libssl3 \
        aspnetcore-runtime-10.0 \
    || (curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
        && bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
        && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet) \
    && rm -rf /var/lib/apt/lists/*

# --- ComfyUI ---
# Pinned to a commit rather than tracking master: an image that silently changes its backend between builds is not
# reproducible, and a ComfyUI change can break a workflow graph. Bump it deliberately.
ARG COMFYUI_REF=v0.29.2
RUN git clone --depth 1 --branch "${COMFYUI_REF}" https://github.com/comfyanonymous/ComfyUI.git /opt/ComfyUI \
    && python3 -m venv /opt/comfy-venv \
    && /opt/comfy-venv/bin/pip install --no-cache-dir --upgrade pip \
    && /opt/comfy-venv/bin/pip install --no-cache-dir torch torchvision torchaudio \
        --index-url https://download.pytorch.org/whl/cu126 \
    && /opt/comfy-venv/bin/pip install --no-cache-dir -r /opt/ComfyUI/requirements.txt \
    && /opt/comfy-venv/bin/pip freeze | grep -E '^(torch|torchvision|torchaudio)==' > /opt/comfy-venv/torch-constraints.txt

# Node packs declare loose requirements like `torch>=2.8.0`. If pip decides that is unsatisfied it will happily
# fetch a fresh torch from PyPI — the DEFAULT build, not the cu126 one installed above — and the GPU stack this
# image was assembled around is silently replaced. The constraint pins nothing of its own: it is exactly the
# versions already installed, read back out of the environment, so pip may add packages but never move these.
# Set as an environment variable so it applies to the build step below AND to a pack installed later from the
# patches page, which runs pip through the same interpreter.
ENV PIP_CONSTRAINT=/opt/comfy-venv/torch-constraints.txt

# --- app ---
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /comfy-patch /comfy-patch
COPY configurations/ ./configurations/
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
# Strip CR so a CRLF checkout (a Windows working tree) does not break the shebang — env would look for `bash\r`.
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh && chmod +x /usr/local/bin/entrypoint.sh

# --- patches ---
# Every change this app makes to ComfyUI, applied through one mechanism: the core fix, the node packs this repo owns
# (the queue gate among them, which is what keeps anything but this app's fair queue from submitting work), and the
# fixes it carries for third-party packs -- which this fetches at their pinned revisions.
#
# A patch that will not apply FAILS THE BUILD. That is the point. The image pins a ComfyUI release, and upstream
# moves; the alternative to failing here is shipping a container that quietly lacks a fix nobody notices is gone
# until a render is wrong.
RUN dotnet /comfy-patch/ComfyPatch.dll apply --all \
        --root /opt/ComfyUI \
        --patches /app/comfy-patches \
        --nodes /app/comfy-nodes \
        --python /opt/comfy-venv/bin/python \
    && dotnet /comfy-patch/ComfyPatch.dll list --root /opt/ComfyUI \
        --patches /app/comfy-patches --nodes /app/comfy-nodes

# SQLite, pinned explicitly rather than inherited: the container's paths are its own, and a deployment should not
# have to know what the application's default happens to be this release.
#
# ComfyUI__Path and ComfyUI__Python are what let the patches page act on the ComfyUI in this image: the app has only
# ever known the renderer as a URL, and a URL cannot say which directory this process may write to.
# ComfyUI__Supervisor is the directory shared with the entrypoint, and its presence is what turns the restart button
# on -- a deployment fact set by the image, never a setting, because it describes how this container is run.
ENV Database__Provider=Sqlite \
    ConnectionStrings__ImageGen="Data Source=/data/imagegen.db" \
    ComfyUI__BaseUrl=http://127.0.0.1:8188 \
    ComfyUI__Path=/opt/ComfyUI \
    ComfyUI__Python=/opt/comfy-venv/bin/python \
    ComfyUI__Supervisor=/run/imagegen \
    Logging__FilePath=/data/logs/imagegen-.log \
    ASPNETCORE_ENVIRONMENT=Production \
    Urls=http://0.0.0.0:8080

# /data — the SQLite database and logs. /app/tagmodel/artifacts — the tag model, which the app fetches on first
# run; a volume so ~900 MB is not re-downloaded every time the image is updated. ComfyUI's own image models are a
# separate bind mount; see compose.yml.
VOLUME ["/data", "/app/tagmodel/artifacts"]
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
