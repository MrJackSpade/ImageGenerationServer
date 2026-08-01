"""Line-thickening nodes for the monster-girls art pipeline.

These are MODEL-FREE, deterministic image ops (numpy + PIL only, no cv2) that bolden the
outlines of an anime image. Two nodes:

  * LineThicken  — grayscale morphological erosion (the min filter). Per-channel 3x3 minimum
    applied `thickness` times: shrinking the lighter regions grows the darker lines. This is
    exactly cv2.erode / ImageMagick `-morphology Erode` / Photoshop "Minimum". Grows every
    dark pixel, interior detail included.

  * XDoGLines    — eXtended Difference-of-Gaussians line extraction. Outputs the existing edges
    as dark-lines-on-white with a built-in threshold/softness knob; flat-colour regions stay white.

Same IMAGE tensor convention as nodes.py: (B,H,W,3) float[0,1].
"""
from __future__ import annotations

import numpy as np
import torch
from PIL import Image, ImageFilter


def _img_to_u8(image: torch.Tensor) -> np.ndarray:
    """ComfyUI IMAGE (B,H,W,3) float[0,1] -> (B,H,W,3) uint8."""
    return (image.clamp(0, 1).cpu().numpy() * 255.0 + 0.5).astype(np.uint8)


def _u8_to_img(arr: np.ndarray) -> torch.Tensor:
    """(B,H,W,3) uint8 -> ComfyUI IMAGE (B,H,W,3) float[0,1]."""
    return torch.from_numpy(arr.astype(np.float32) / 255.0)


def _erode_rgb(arr_u8: np.ndarray, thickness: int) -> np.ndarray:
    """Grow dark regions by `thickness` px via repeated 3x3 per-channel min filter (== cv2.erode).
    `thickness` is the growth radius in pixels (iterations of a 3x3 minimum). 0 = no-op."""
    if thickness <= 0:
        return arr_u8
    img = Image.fromarray(arr_u8, mode="RGB")
    for _ in range(int(thickness)):
        img = img.filter(ImageFilter.MinFilter(3))
    return np.asarray(img, dtype=np.uint8)


class LineThicken:
    """Morphological erosion (min filter): grow the dark lines by `thickness` pixels. Model-free,
    deterministic. Thickens every dark pixel — the standard cv2.erode / PS-Minimum behaviour."""
    TITLE = "Line Thicken (erode)"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("image",)

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                # Growth radius in pixels = iterations of a 3x3 minimum filter. 1 ~= +1px lines.
                "thickness": ("INT", {"default": 2, "min": 0, "max": 32}),
            },
        }

    def run(self, image, thickness):
        batch = _img_to_u8(image)
        out = [_erode_rgb(batch[i], thickness) for i in range(batch.shape[0])]
        return (_u8_to_img(np.stack(out, axis=0)),)


class XDoGLines:
    """eXtended Difference-of-Gaussians line extraction. Returns the source's edges as
    dark-lines-on-white (RGB). Pair with LineThicken + a multiply ImageBlend to bolden only the
    outlines. `sigma` sets line scale, `epsilon`/`phi` the threshold/softness, `tau` the DoG
    sharpness, `k` the gaussian ratio (1.6 ~= a Laplacian-of-Gaussian)."""
    TITLE = "XDoG Lines"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("image",)

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "sigma": ("FLOAT", {"default": 1.0, "min": 0.3, "max": 8.0, "step": 0.1}),
                "k": ("FLOAT", {"default": 1.6, "min": 1.0, "max": 4.0, "step": 0.1}),
                "tau": ("FLOAT", {"default": 0.98, "min": 0.5, "max": 1.0, "step": 0.01}),
                # Flats have DoG response I*(1-tau) >= 0; edges go negative. epsilon=0 cleanly keeps
                # flats white and darkens only edges. Raise it to darken more (also grays flats); lower
                # (negative) to keep only the strongest edges.
                "epsilon": ("FLOAT", {"default": 0.0, "min": -1.0, "max": 1.0, "step": 0.01}),
                "phi": ("FLOAT", {"default": 10.0, "min": 0.1, "max": 50.0, "step": 0.1}),
            },
        }

    @staticmethod
    def _xdog(arr_u8: np.ndarray, sigma, k, tau, epsilon, phi) -> np.ndarray:
        # luminance -> two gaussians (PIL radius ~= sigma) -> sharpened DoG -> soft XDoG threshold
        gray = (0.299 * arr_u8[..., 0] + 0.587 * arr_u8[..., 1] + 0.114 * arr_u8[..., 2]).astype(np.uint8)
        gimg = Image.fromarray(gray, mode="L")
        g1 = np.asarray(gimg.filter(ImageFilter.GaussianBlur(radius=float(sigma))), dtype=np.float32) / 255.0
        g2 = np.asarray(gimg.filter(ImageFilter.GaussianBlur(radius=float(sigma * k))), dtype=np.float32) / 255.0
        dog = g1 - float(tau) * g2
        # T(u) = 1 where u >= epsilon (no line), else 1 + tanh(phi*(u-epsilon)) (darkens toward the edge)
        e = np.where(dog >= epsilon, 1.0, 1.0 + np.tanh(float(phi) * (dog - float(epsilon))))
        lines = np.clip(e, 0.0, 1.0)
        u8 = (lines * 255.0 + 0.5).astype(np.uint8)
        return np.repeat(u8[..., None], 3, axis=2)   # L -> RGB so a downstream multiply is per-channel

    def run(self, image, sigma, k, tau, epsilon, phi):
        batch = _img_to_u8(image)
        out = [self._xdog(batch[i], sigma, k, tau, epsilon, phi) for i in range(batch.shape[0])]
        return (_u8_to_img(np.stack(out, axis=0)),)


NODE_CLASS_MAPPINGS = {
    "LineThicken": LineThicken,
    "XDoGLines": XDoGLines,
}
