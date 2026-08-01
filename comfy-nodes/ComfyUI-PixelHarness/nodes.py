"""ComfyUI node wrappers for the pixel-art quantizer.

PixelQuantize is the deterministic, model-free pixelizer: OKLab palette snap + grid
mode-reduce + nearest-upscale render. It is BOTH the standalone frame/still pixelizer and
(when block-rendered after a VAE decode) the authoritative final renderer that keeps VAE
noise out of the output. No model, no VRAM, runs on CPU in milliseconds.

The diffusion projection node (PixelManifoldProjection) lands in a follow-up; it reuses
quant.py so the math stays identical between the per-step projection and the final render.
"""
from __future__ import annotations

import numpy as np
import torch
from PIL import Image

from . import quant


def _img_to_u8(image: torch.Tensor) -> np.ndarray:
    """ComfyUI IMAGE (B,H,W,3) float[0,1] -> list-friendly (B,H,W,3) uint8."""
    return (image.clamp(0, 1).cpu().numpy() * 255.0 + 0.5).astype(np.uint8)


def _u8_to_img(arr: np.ndarray) -> torch.Tensor:
    """(B,H,W,3) uint8 -> ComfyUI IMAGE (B,H,W,3) float[0,1]."""
    return torch.from_numpy(arr.astype(np.float32) / 255.0)


class PixelQuantize:
    """Snap an image onto a fixed grid + palette (the pixel-art manifold). Deterministic."""
    TITLE = "Pixel Quantize"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("image",)

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "grid_w": ("INT", {"default": 384, "min": 8, "max": 4096}),
                "grid_h": ("INT", {"default": 256, "min": 8, "max": 4096}),
                # 'adaptive', an inline hex list ("aabbcc, 112233, ..."), or a bundled name
                # like "chroma-256". The inline path is how a per-character locked palette is fed.
                "palette": ("STRING", {"default": "chroma-256", "multiline": False}),
                "method": (["median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode"], {"default": "median"}),
            },
            "optional": {
                # > 0: ignore grid_w/grid_h and derive the grid from the image aspect, longest edge = this.
                "virtual_resolution": ("INT", {"default": 0, "min": 0, "max": 4096}),
            },
        }

    def run(self, image, grid_w, grid_h, palette, method, virtual_resolution=0):
        if image.ndim == 5:                       # (B,T,H,W,3) from a 3D/video VAE -> flatten frames into the batch
            image = image.reshape(-1, *image.shape[2:])
        batch = _img_to_u8(image)
        has_alpha = batch.shape[-1] == 4          # keyed input: RGB carries the subject, A the matte
        out = []
        for i in range(batch.shape[0]):
            arr = batch[i]
            H, W = arr.shape[:2]
            gw, gh = quant.grid_for_aspect(W, H, grid_w, grid_h, virtual_resolution)
            if has_alpha:
                rgb, alpha = arr[..., :3], arr[..., 3]
                opaque = alpha >= 128
                # RGB is the subject on its original (white) background — the colour math runs on it as-is,
                # exactly as it did behind flatten-on-white. Alpha is carried separately and reattached below.
                pal = quant.resolve_palette(palette, img_u8=rgb, grid=(gw, gh), opaque=opaque)
                grid = quant.quantize(rgb, gw, gh, pal, method=method)
                blocks = quant.render(grid, pal, scale=1)                   # (gh, gw, 3)
                a_grid = quant.downsample_alpha(opaque, gw, gh)             # (gh, gw) 1-bit
                blocks = np.dstack([blocks, a_grid])                        # (gh, gw, 4) RGBA sprite
            else:
                pal = quant.resolve_palette(palette, img_u8=arr, grid=(gw, gh))
                grid = quant.quantize(arr, gw, gh, pal, method=method)
                blocks = quant.render(grid, pal, scale=1)                   # (gh, gw, 3)
            # Break the INPUT image into the gw×gh grid, snap each block to one colour, and keep the input's
            # resolution: nearest-upscale the block grid back to (W, H). So the output is the input, blockified.
            full = np.asarray(Image.fromarray(blocks).resize((W, H), Image.NEAREST), dtype=np.uint8)
            out.append(full)
        return (_u8_to_img(np.stack(out, axis=0)),)


class PixelManifoldProjection:
    """Patch a MODEL so every denoise step's x0 estimate is projected onto the pixel-art manifold
    (decode -> quantize to grid+palette -> re-encode -> blend), then feed the patched model to a stock
    KSampler. This is the diffusion pixelizer: the harness's projection sampler, but model-agnostic —
    ComfyUI hands us the normalized `denoised` (x0) for ANY architecture (SDXL / Flux / Qwen-Edit), so
    one implementation covers them all (the harness needed separate eps vs flow-matching samplers).

    The in-loop projection STEERS the trajectory toward the manifold; the authoritative crisp render is
    a separate PixelQuantize at the end of the graph (so VAE noise never reaches the output). Run the
    sampler at the working resolution grid*block so the decoded image and the rendered projection match.
    """
    TITLE = "Pixel Manifold Projection (model patch)"
    CATEGORY = "pixelharness"
    FUNCTION = "patch"
    RETURN_TYPES = ("MODEL",)
    RETURN_NAMES = ("model",)

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "model": ("MODEL",),
                "vae": ("VAE",),
                "grid_w": ("INT", {"default": 384, "min": 8, "max": 4096}),
                "grid_h": ("INT", {"default": 256, "min": 8, "max": 4096}),
                # 'adaptive', an inline hex list, or a bundled name (e.g. a per-character locked palette).
                "palette": ("STRING", {"default": "chroma-256", "multiline": False}),
                # Quantization method for the per-step projection target. 'median' (default) keeps edges
                # straight (no sawtooth); 'mode' is the old crisp-but-bistable behaviour; 'box' is softer.
                "method": (["median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode"], {"default": "median"}),
                # Projection weight ramps from w_start (early: let the model draw) to w_end (late: snap hard)
                # by STEP INDEX over the actual denoise window — w = w_start+(w_end-w_start)*i/(n-1) — exactly
                # as the harness does. Flux defaults are 0.5 -> 1.0.
                "w_start": ("FLOAT", {"default": 0.5, "min": 0.0, "max": 1.0, "step": 0.05}),
                "w_end": ("FLOAT", {"default": 1.0, "min": 0.0, "max": 1.0, "step": 0.05}),
                # Restrict projection to a slice of the step-index fraction (0 = first step .. 1 = last). Raising
                # start_percent lets the model compose freely before the clamp engages. Defaults 0..1 = no effect.
                "start_percent": ("FLOAT", {"default": 0.0, "min": 0.0, "max": 1.0, "step": 0.05}),
                "end_percent": ("FLOAT", {"default": 1.0, "min": 0.0, "max": 1.0, "step": 0.05}),
                # Project every Nth step (1 = every step). Higher = faster, weaker manifold pull.
                "project_every": ("INT", {"default": 1, "min": 1, "max": 8}),
            },
            "optional": {
                # > 0: ignore grid_w/grid_h and derive the grid from the decoded image aspect, longest edge = this.
                "virtual_resolution": ("INT", {"default": 0, "min": 0, "max": 4096}),
            },
        }

    def patch(self, model, vae, grid_w, grid_h, palette, method, w_start, w_end,
              start_percent, end_percent, project_every, virtual_resolution=0):
        m = model.clone()
        state = {"n": 0}

        def post_cfg(args):
            denoised = args["denoised"]                       # (B,C,H,W) latent x0 estimate
            sigma = args["sigma"]
            # Faithful to sampler_flux.pixelize: ramp by STEP INDEX over the actual denoise window. With
            # denoise<1 the KSampler runs only the tail of the schedule, and `sample_sigmas` is that exact
            # window's sigma list (len = nsteps+1), so i/(n-1) here == the harness's i/max(n-1,1).
            sched = args.get("model_options", {}).get("transformer_options", {}).get("sample_sigmas")
            if sched is not None and len(sched) > 1:
                cur = sigma.flatten()[0]
                idx = int(torch.argmin(torch.abs(sched.to(cur.device) - cur)).item())
                frac = idx / max(len(sched) - 2, 1)           # len-1 = nsteps; ramp over i in 0..nsteps-1
            else:
                frac = 1.0
            frac = min(max(frac, 0.0), 1.0)
            if frac < start_percent or frac > end_percent:
                return denoised
            state["n"] += 1
            if (state["n"] - 1) % project_every != 0:
                return denoised
            w = w_start + (w_end - w_start) * frac

            # `denoised` is in the model's INTERNAL latent space (post process_latent_in); the VAE works in VAE
            # latent space. For Flux they differ by a shift+scale, so we must convert model->VAE before decode and
            # VAE->model after encode, or the round-trip mangles colour and rings every edge.
            model = args["model"]
            img = vae.decode(model.process_latent_out(denoised))   # (B,H,W,3); (B,T,H,W,3) for 3D/video VAEs
            # VIDEO (additive): a multi-frame video VAE decodes to (B,T,H,W,3). Project EVERY frame (flatten the
            # temporal axis into the batch for the loop, then restore the shape for re-encode). A size-1 temporal
            # dim (image VAEs) and the 4D image case keep the original single-frame behaviour untouched.
            video_shape = None
            if img.ndim == 5:
                if img.shape[1] > 1:
                    video_shape = tuple(img.shape)
                    img = img.reshape(-1, *img.shape[2:])
                else:
                    img = img[:, 0]
            arr = (img.detach().clamp(0, 1).cpu().numpy() * 255.0 + 0.5).astype(np.uint8)
            # For video, LOCK the palette across frames (resolve once, reuse) so the clip projects onto one fixed
            # palette. The image path keeps its original per-item resolve.
            locked_pal = None
            if video_shape is not None:
                H0, W0 = arr[0].shape[:2]
                gw0, gh0 = quant.grid_for_aspect(W0, H0, grid_w, grid_h, virtual_resolution)
                locked_pal = quant.resolve_palette(palette, img_u8=arr[0], grid=(gw0, gh0))
            out = []
            for b in range(arr.shape[0]):
                H, W = arr[b].shape[:2]
                gw, gh = quant.grid_for_aspect(W, H, grid_w, grid_h, virtual_resolution)
                pal = locked_pal if locked_pal is not None else quant.resolve_palette(palette, img_u8=arr[b], grid=(gw, gh))
                g = quant.quantize(arr[b], gw, gh, pal, method=method)
                r = quant.render(g, pal, scale=max(1, round(W / gw)))
                if r.shape[0] != H or r.shape[1] != W:
                    r = np.asarray(Image.fromarray(r).resize((W, H), Image.NEAREST), dtype=np.uint8)
                out.append(r)
            proj = torch.from_numpy(np.stack(out).astype(np.float32) / 255.0).to(img)
            if video_shape is not None:
                proj = proj.reshape(*video_shape)
            proj_lat = model.process_latent_in(vae.encode(proj)).to(denoised)   # VAE space -> model space
            # Causal video VAEs don't round-trip the frame count exactly (decode T->P, encode P->T' can differ by ~1
            # at the reference-frame boundary), so the re-encode may be a latent-frame short/long. Project only the
            # frames that line up (the overlap) and leave any unmatched tail frame exactly as the model produced it —
            # the final PixelQuantize still pixelizes it. (End-padding a duplicate frame here ghosted the last frame.)
            if proj_lat.ndim == 5 and denoised.ndim == 5 and proj_lat.shape[2] != denoised.shape[2]:
                n = min(proj_lat.shape[2], denoised.shape[2])
                out = denoised.clone()
                out[:, :, :n] = (1.0 - w) * denoised[:, :, :n] + w * proj_lat[:, :, :n]
                return out
            return (1.0 - w) * denoised + w * proj_lat

        # disable_cfg1_optimization=True: Flux-dev / Qwen-Edit run at CFG 1, where ComfyUI's cfg-1 shortcut
        # can bypass the post-CFG hook. Force the full path so the projection runs every step.
        m.set_model_sampler_post_cfg_function(post_cfg, disable_cfg1_optimization=True)
        return (m,)


from .linethicken import NODE_CLASS_MAPPINGS as _LINETHICKEN_NODES
from .sketchkeras_node import NODE_CLASS_MAPPINGS as _SKETCHKERAS_NODES
from .pixelize_fp import NODE_CLASS_MAPPINGS as _PIXELIZE_FP_NODES
from .birefnet_matte import NODE_CLASS_MAPPINGS as _BIREFNET_MATTE_NODES
from .deflicker import NODE_CLASS_MAPPINGS as _DEFLICKER_NODES

NODE_CLASS_MAPPINGS = {
    "PixelQuantize": PixelQuantize,
    "PixelManifoldProjection": PixelManifoldProjection,
    **_LINETHICKEN_NODES,   # LineThicken (erode), XDoGLines
    **_SKETCHKERAS_NODES,   # SketchKerasLines
    **_PIXELIZE_FP_NODES,   # PixelQuantizeFP (feature-preserving, global palette)
    **_BIREFNET_MATTE_NODES,   # BiRefNetMatte (background-removal matte -> RGBA)
    **_DEFLICKER_NODES,   # DeflickerAuto (BiRefNet + drift-aware histmatch flicker fix)
}
