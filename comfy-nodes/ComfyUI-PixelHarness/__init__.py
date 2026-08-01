# ComfyUI-PixelHarness — pixel-art projection nodes for the monster-girls art pipeline.
# Ported from the standalone pixelharness POC (E:\pixelharness) so the pixelizer runs
# inside ComfyUI: one VRAM arbiter, one queue (via Forge), one API surface.
#
# Nodes are intended to be driven over the Forge API by API-only (visible:false) workflows,
# not used by hand in the graph editor — but they work standalone too.
try:
    import comfy.utils  # noqa: F401  (only register when loaded as a ComfyUI custom node)
except ImportError:
    NODE_CLASS_MAPPINGS = {}
    NODE_DISPLAY_NAME_MAPPINGS = {}
else:
    from .nodes import NODE_CLASS_MAPPINGS
    NODE_DISPLAY_NAME_MAPPINGS = {k: getattr(v, "TITLE", k) for k, v in NODE_CLASS_MAPPINGS.items()}

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
