# comfy-nodes

The ComfyUI node packs this repository owns. **This directory is the source of truth for them** — they are
ordinary `.py` files and you edit them here.

They reach a ComfyUI installation as **patches**, like every other change this app makes to one: each pack
listed in `packs.json` is turned into an add-everything unified diff *in memory* by `ComfyPatchCatalog`, so
there is no generated file to keep in step with the tree and nothing is ever copied into place behind the
patch system's back. A pack directory with no `packs.json` entry is not a patch and never reaches ComfyUI —
adding the folder is half the job, the manifest entry is the other half.

They cannot be tracked in the ComfyUI checkout itself: that repo's `origin` is upstream
`comfyanonymous/ComfyUI` (nothing we can push to), and its `.gitignore` ignores `/custom_nodes/` outright.
Anything left only in `custom_nodes\` exists on exactly one box's disk and reaches no other machine — which is
how these six went unversioned in the first place.

Third-party packs with local fixes are **not** here: those live as authored diffs in `comfy-patches/`, against
their own upstream at a pinned revision, so applying one downloads the pack and patches it rather than
vendoring somebody else's code.

| pack | origin | what it's for |
|---|---|---|
| `ComfyUI-ModelPin` | ours | Per-graph VRAM residency. `PinModelGPU` forces a MODEL fully-resident; `EvictCLIPFromGPU` drops a text encoder once its conditioning exists, so a large UNET loading next isn't starved into streaming over PCIe. |
| `ComfyUI-CondCache` | ours | `SaveConditioning`/`LoadConditioning` — encode a fixed prompt once and reuse it across a clip's per-frame graphs. Its `cache\` directory is a runtime artifact and is not part of the patch. |
| `ComfyUI-Ideogram4Debanner` | ours | Frozen, reversible first-step residual correction for the conditional Ideogram 4 model. The Ideogram 4 workflow wires it before guidance; the checkpoint on disk is never changed. |
| `ComfyUI-ColorCorrectedComposite` | ours | Color-matched compositing for the edit/inpaint paths. |
| `imagegen_gate` | ours | Submission gate — the app's queue is the only way in (`/prompt` refuses direct submissions). Carries a removal warning, because taking it out changes a guarantee rather than a feature. |
| `ComfyUI-PixelHarness` | ours | `PixelQuantize` / `PixelManifoldProjection` and their palettes. ~15 workflow classes need these node names, and the DreamOmni2 patch loads its `quant.py`. Developed in a separate repo until that was retired — see `VENDORED.md`. |
| `ComfyUI-GGUF` | vendored, patched | city96's GGUF loaders, carrying local changes. Re-cloning would drop them. |

## Adding or changing a pack

1. Edit under `comfy-nodes\`.
2. Add or update its entry in `packs.json` — `dir`, `order`, `id`, `title`, `why`, and `warn` if removing it
   costs something. Add `provides` when the pack satisfies a catalogue `custom_node` requirement. That entry is
   what the patches page shows.
3. Apply it: **Settings → Renderer patches**, or
   `dotnet run --project tools/ComfyPatch -- apply --root <ComfyUI> --id <id>`.
   An installed copy that has fallen behind reports a conflict naming the files; **Overwrite** replaces them.
4. **Restart ComfyUI.** It scans `custom_nodes\` and imports node modules **at startup only** — a new pack is
   invisible and an edited one keeps running its old code until it restarts. In the container the page has a
   button; elsewhere it says so and leaves it to you.
5. Confirm registration before relying on it: the node must appear in
   `http://127.0.0.1:8188/object_info`. A graph referencing an unregistered node fails at submit.
