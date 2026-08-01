"""
ComfyUI-CondCache — persist a CONDITIONING to disk and load it back.

Purpose: a fixed prompt's conditioning is invariant across every frame of a clip, but
with --cache-none ComfyUI re-encodes it each submission. This lets a workflow encode
ONCE (CLIPTextEncode -> SaveConditioning), then have each per-frame graph LoadConditioning
instead of re-running the text encoder. Explicit, workflow-owned reuse of a provably
invariant artifact — not global output caching.
"""
import os
import torch

CACHE_DIR = os.path.join(os.path.dirname(__file__), "cache")
os.makedirs(CACHE_DIR, exist_ok=True)


def _move(obj, device):
    if torch.is_tensor(obj):
        return obj.to(device)
    if isinstance(obj, dict):
        return {k: _move(v, device) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        t = [_move(v, device) for v in obj]
        return type(obj)(t)
    return obj


def _path(key):
    safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in str(key))
    return os.path.join(CACHE_DIR, safe + ".pt")


class SaveConditioning:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"conditioning": ("CONDITIONING",),
                             "key": ("STRING", {"default": "cond"})}}
    RETURN_TYPES = ()
    FUNCTION = "save"
    OUTPUT_NODE = True
    CATEGORY = "conditioning/cache"

    def save(self, conditioning, key):
        torch.save(_move(conditioning, "cpu"), _path(key))
        return {}


class LoadConditioning:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"key": ("STRING", {"default": "cond"})}}
    RETURN_TYPES = ("CONDITIONING",)
    FUNCTION = "load"
    CATEGORY = "conditioning/cache"

    def load(self, key):
        import comfy.model_management as mm
        path = _path(key)
        if not os.path.exists(path):
            raise FileNotFoundError(f"CondCache: no saved conditioning for key '{key}' ({path})")
        cond = torch.load(path, map_location="cpu", weights_only=False)
        return (_move(cond, mm.get_torch_device()),)


NODE_CLASS_MAPPINGS = {"SaveConditioning": SaveConditioning, "LoadConditioning": LoadConditioning}
NODE_DISPLAY_NAME_MAPPINGS = {"SaveConditioning": "Save Conditioning (cache)",
                              "LoadConditioning": "Load Conditioning (cache)"}
__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
