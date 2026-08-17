# Ideogram 4 two-stage correction

This first-party ComfyUI node applies the two frozen residual directions validated by the
`tools/debanner` diagnostic project. It patches only the conditional Ideogram 4 model in memory;
the separately loaded unconditional model and the checkpoint files on disk are unchanged.

The `ImageGenerationServer` Ideogram 4 workflow wires it automatically between the conditional
UNET loader and `CFGOverride`. It is paired with the reversible `core-ideogram4-block-patch`
renderer patch. Without that core hook this pack deliberately registers no node, so the app's
normal node-presence check keeps the workflow unavailable instead of silently running uncorrected.

Validated settings:

- Stage 2: step 0, conditional pass 0, blocks 20–24, strength `0.6422342360019688`.
- Stage 1: step 0, conditional pass 0, blocks 25–28, strength `0.4`.
- Subtract each spatial direction and restore every edited image token to its original norm.

The node's disabled state, or both strengths set to zero, is a strict model-level no-op: it returns
the original model object without cloning it or adding a callback.

The final fixed-seed validation used 992×992 output, Euler, 20 steps, the Ideogram 4 scheduler,
base CFG 7, and late CFG 3. All eight held-out target cases converted to clean images; all eight
fixed clean controls remained clean; all sixteen images matched their prompts.
