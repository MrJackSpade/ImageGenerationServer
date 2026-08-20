"""BiRefNet background-removal matte node (PixelHarness).

The sprite pipeline's background-removal bake-off picked BiRefNet (general) as the winner over a flood-fill key
and the other matting models, so this exposes it as a ComfyUI node for a Forge matte workflow. Per frame it runs
BiRefNet and returns the frame as RGBA (RGB + the matte as alpha) plus the alpha as a MASK. Feed the RGBA into
SaveImage (still) or SaveAnimatedWEBP(lossless=true) (video) to save a transparent-background result.

Uses transformers (already in the ComfyUI env); weights download from HF on first use. No external node install.
"""
from __future__ import annotations

import threading
import numpy as np
import torch
from PIL import Image

_PATCHER = None
_PATCHER_LOCK = threading.Lock()


def _load():
    global _PATCHER
    import comfy.model_management as mm
    from comfy.model_patcher import ModelPatcher
    with _PATCHER_LOCK:
        if _PATCHER is None:
            from transformers import AutoModelForImageSegmentation
            model = AutoModelForImageSegmentation.from_pretrained(
                "ZhengPeng7/BiRefNet", trust_remote_code=True).eval()
            load_device = mm.get_torch_device()
            if load_device.type == "cuda":
                model = model.half()          # fp16 on GPU (smaller footprint next to other resident models)
            else:
                model = model.float()         # CPU has no fp16 conv
            _PATCHER = ModelPatcher(model, load_device, mm.unet_offload_device())
        mm.load_models_gpu([_PATCHER])
        model = _PATCHER.model
        return model, _PATCHER.load_device, next(model.parameters()).dtype


_MEAN = torch.tensor([0.485, 0.456, 0.406]).view(3, 1, 1)
_STD = torch.tensor([0.229, 0.224, 0.225]).view(3, 1, 1)


class BiRefNetMatte:
    """Per-frame BiRefNet matte. Input IMAGE (B,H,W,3); outputs RGBA IMAGE (B,H,W,4) + alpha MASK (B,H,W)."""
    TITLE = "BiRefNet Matte (background removal)"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE", "MASK")
    RETURN_NAMES = ("rgba", "alpha")

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {"image": ("IMAGE",)},
            # 0 = soft alpha (let the caller threshold); >0 = hard cutoff at this matte value.
            "optional": {"threshold": ("FLOAT", {"default": 0.0, "min": 0.0, "max": 1.0, "step": 0.01})},
        }

    def run(self, image, threshold=0.0):
        model, dev, dtype = _load()
        if image.ndim == 5:                       # (B,T,H,W,3) video decode -> flatten frames
            image = image.reshape(-1, *image.shape[2:])
        b, h, w, _ = image.shape
        alphas = []
        with torch.no_grad():
            for i in range(b):
                pil = Image.fromarray((image[i].cpu().numpy() * 255.0 + 0.5).astype(np.uint8))
                rs = pil.resize((1024, 1024), Image.BILINEAR)
                t = torch.from_numpy(np.asarray(rs).astype(np.float32) / 255.0).permute(2, 0, 1)
                t = ((t - _MEAN) / _STD).unsqueeze(0).to(dev).to(dtype)
                pred = model(t)
                pred = pred[-1] if isinstance(pred, (list, tuple)) else pred
                a = pred.sigmoid().float().cpu()[0, 0]        # (1024,1024)
                del pred, t
                a = Image.fromarray((a.numpy() * 255.0).astype(np.uint8)).resize((w, h), Image.BILINEAR)
                av = torch.from_numpy(np.asarray(a).astype(np.float32) / 255.0)
                if threshold > 0:
                    av = (av >= threshold).float()
                alphas.append(av)
        alpha = torch.stack(alphas, axis=0)                    # (B,H,W)
        rgba = torch.cat([image, alpha.unsqueeze(-1)], dim=-1)  # (B,H,W,4) -> SaveImage/SaveAnimatedWEBP keep alpha
        return (rgba, alpha)


NODE_CLASS_MAPPINGS = {
    "BiRefNetMatte": BiRefNetMatte,
}
