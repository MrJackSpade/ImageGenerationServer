"""
ComfyUI-ModelPin — per-graph control over which weights hold VRAM.

Two halves of the same concern: PinModelGPU forces a MODEL fully-resident, EvictCLIPFromGPU
drops a text encoder once its conditioning exists.

ComfyUI's load_models_gpu partial-loads big models even in NORMAL_VRAM state unless
force_full_load=True (comfy/model_management.py: `... and not force_full_load`). For a
frame-by-frame img2img pass that re-runs the UNET every frame, that partial load streams
the whole model to GPU each frame (~7s/frame here). PinModelGPU calls force_full_load once
per execution so the model lands fully on the GPU and stays resident across frames.

Scoped by design: these only affect graphs that include them. Standard image gen never sees
them — this is NOT a global VRAM setting, and nothing here touches model_management's own
policy, so every other node and workflow behaves exactly as before.
"""
import logging

import comfy.model_management as mm


class PinModelGPU:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"model": ("MODEL",)}}

    RETURN_TYPES = ("MODEL",)
    FUNCTION = "pin"
    CATEGORY = "model/pin"
    TITLE = "Pin Model to GPU (full load)"

    @classmethod
    def IS_CHANGED(cls, model):
        # Always re-run so the full-load is re-asserted every frame (a no-op when already
        # resident; a reload only if something evicted it).
        return float("nan")

    def pin(self, model):
        mm.load_models_gpu([model], force_full_load=True)
        return (model,)


class EvictCLIPFromGPU:
    """Fully evict a CLIP/text encoder from VRAM, after its conditioning exists and before sampling.

    Why a node and not a memory-management setting: free_memory() only ever frees *just enough*
    (model_management.py — `model_unload(memory_to_free)` -> `partially_unload(...)`), so when the
    encoder and the UNET are each large relative to the card, the encoder is left partially resident
    and the UNET that loads next gets starved and streams the remainder over PCIe every step. On
    FLUX.2-dev (a ~19.6 GB Mistral encoder and a ~19.6 GB Q4_K_M UNET on a 24 GB card) that is the
    difference between `loaded completely, full load: True` and `loaded partially; 3206 MB usable,
    16999 MB offloaded`. They never need to be co-resident: the encoder runs once, emits a few MB of
    conditioning, and is dead weight for every sampling step after that.

    So this drops it explicitly, with memory_to_free=None -> LoadedModel.model_unload takes the full
    `detach()` path and skips partial unload entirely.

    Both conditionings pass through so that BOTH CLIPTextEncode nodes are upstream dependencies —
    ComfyUI's executor is demand-driven, and evicting while an encode is still pending would just
    force the encoder straight back onto the card.
    """

    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "positive": ("CONDITIONING",),
            "negative": ("CONDITIONING",),
            "clip": ("CLIP",),
        }}

    RETURN_TYPES = ("CONDITIONING", "CONDITIONING")
    RETURN_NAMES = ("positive", "negative")
    FUNCTION = "evict"
    CATEGORY = "model/pin"
    TITLE = "Evict CLIP from GPU (free VRAM before sampling)"

    @classmethod
    def IS_CHANGED(cls, positive, negative, clip):
        # Always re-run: a cached execution would skip the eviction and silently restore the stall.
        return float("nan")

    def evict(self, positive, negative, clip):
        patcher = clip.patcher
        freed = 0
        for i in range(len(mm.current_loaded_models) - 1, -1, -1):
            entry = mm.current_loaded_models[i]
            if entry.is_dead():
                continue
            if entry.model is patcher or entry.model.is_clone(patcher):
                freed += entry.model.loaded_size()
                entry.model_unload()          # memory_to_free=None -> full detach, never partial
                mm.current_loaded_models.pop(i)
        if freed:
            mm.soft_empty_cache()
            logging.info("Evicted CLIP from GPU: %.2f MB freed for sampling.", freed / (1024 ** 2))
        else:
            logging.info("Evict CLIP from GPU: encoder was not resident, nothing to free.")
        return (positive, negative)


NODE_CLASS_MAPPINGS = {"PinModelGPU": PinModelGPU, "EvictCLIPFromGPU": EvictCLIPFromGPU}
NODE_DISPLAY_NAME_MAPPINGS = {
    "PinModelGPU": "Pin Model to GPU (full load)",
    "EvictCLIPFromGPU": "Evict CLIP from GPU (free VRAM before sampling)",
}
__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
