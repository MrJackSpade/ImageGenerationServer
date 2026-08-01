# ComfyUI-PixelHarness

First-party. This directory is the source of truth for it — it was developed in a separate
`MrJackSpade/ComfyUI-PixelHarness` repository, which is retired: a pack that lives in two places drifts
between them, and that repository was private, so nothing but the author's own machine could install it.
Vendored from `4fe859ab0150b122d3089fdd1be4c20bd3c98479`.

## What it carries, and what it does not

**`vendor/sketchKeras-pytorch/weights/model.pth` (71 MB) is here.** `sketchkeras_node.py` loads it and
raises without it, and `line-thicken-sketchkeras` is a shipped workflow — so leaving it out meant shipping
a workflow that throws `FileNotFoundError` until somebody found upstream's Google Drive link. It is carried
whole rather than diffed: a file with no lines has nothing to match, so it is present with these exact bytes
or it is not (see `FileDiff.Bytes`).

**`vendor/sketchKeras-pytorch/weights/mod.h5` (214 MB) is not.** It is the original Keras file that
`src/fromkeras.py` converts *into* `model.pth`; nothing loads it at runtime, and it exceeds what GitHub
accepts. It is only needed to regenerate `model.pth` from scratch, which nobody has to do.

**The two sketchKeras sample images are not** — `tests/{1234461,Hokusai}.jpg`, referenced by nothing here.

`PIXELIZER_CHECKPOINT.md` was tracked by the old repository and is deliberately gone: it was a working-notes
snapshot about the palette quantiser, carrying one machine's absolute paths and text that had no business in
a shipped artifact. It documented nothing about this pack.
