# Ideogram 4 correction

This ComfyUI node applies one frozen residual direction to the conditional Ideogram 4 model
in memory. The separately loaded unconditional model and checkpoint files are unchanged.

Place the node between the conditional UNET loader and `CFGOverride`. It is paired with the
bundled reversible `core-ideogram4-block-patch` ComfyUI patch. Without that core capability
marker, this pack deliberately registers no node instead of silently running uncorrected.

Configured operation:

- Step 0, conditional pass 0, blocks 25–28, strength `0.6`.
- Subtract the spatial direction and restore every edited image token to its original norm.

The disabled state or zero strength is a strict model-level no-op: it returns the original
model object without cloning it or adding a callback. The single-direction strength is pending
fixed-panel revalidation.
