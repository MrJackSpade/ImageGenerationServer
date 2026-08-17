# Ideogram 4 correction

This first-party ComfyUI node applies one frozen residual direction to the conditional
Ideogram 4 model in memory. The separately loaded unconditional model and checkpoint files
on disk are unchanged.

The `ImageGenerationServer` Ideogram 4 workflow wires it automatically between the conditional
UNET loader and `CFGOverride`. It is paired with the reversible `core-ideogram4-block-patch`
renderer patch. Without that core hook this pack deliberately registers no node, so the app's
normal node-presence check keeps the workflow unavailable instead of silently running uncorrected.

Configured operation:

- Step 0, conditional pass 0, blocks 25–28, strength `0.6`.
- Subtract the spatial direction and restore every edited image token to its original norm.

The disabled state or zero strength is a strict model-level no-op: it returns the original
model object without cloning it or adding a callback. The single-direction strength is pending
fixed-panel revalidation.
