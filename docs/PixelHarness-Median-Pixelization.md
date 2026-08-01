# PixelHarness — Pixel-Art Quantization & Per-Step Diffusion Projection

A self-contained spec for reimplementing the pixel-art pipeline inside a ComfyUI
environment. Two pieces:

1. **`PixelQuantize`** — a deterministic, model-free quantizer. Snaps any RGB image onto a
   fixed pixel grid + colour palette (the "pixel-art manifold"). Pure NumPy/PIL, no VRAM.
   This is both the standalone still/frame pixelizer **and** the authoritative final renderer.
2. **`PixelManifoldProjection`** — a **model patch** that inserts the *same* quantization math
   into the denoise loop of *any* diffusion model (SDXL / Flux / Qwen-Image-Edit / video VAEs).
   Every step's `x0` estimate is decoded → quantized → re-encoded → blended back, so the
   sampler's trajectory is steered onto the pixel-art manifold *as it generates* rather than
   the image being pixelized after the fact.

The whole point of (2) is that the model and the quantizer **cooperate**: the model supplies
local coherence and clean shapes while the projection enforces the grid + palette every step,
so the model produces manifold-friendly structure instead of fighting an already-finished image.

The default cell method everywhere is **`median`** — it is what makes flats crisp *and* edges
straight. The bulk of this doc explains why.

---

## 0. Dependencies & layout

Pure `numpy` + `Pillow` (PIL). No ML deps for the quantizer; the projection node additionally
uses `torch` (already present in ComfyUI) and a `VAE` handle.

```
ComfyUI-PixelHarness/
  __init__.py        # registers NODE_CLASS_MAPPINGS when loaded as a custom node
  quant.py           # THE ALGORITHM — single source of truth (colour space, palette, quantize, render)
  nodes.py           # ComfyUI node wrappers: PixelQuantize, PixelManifoldProjection
  palettes/*.hex     # bundled palettes, one 6-hex colour per line ('#'/';' comments allowed)
```

`quant.py` is shared verbatim by both nodes so the per-step projection target and the final
render use *identical* math — this matters: if they diverged, the diffusion would be steered
toward a manifold the final render doesn't actually produce.

---

## 1. Colour space — OKLab

"Nearest palette colour" must match **human perception**, not raw sRGB/RGB Euclidean distance
(which over-weights green and mangles dark tones). So every nearest-colour decision happens in
**OKLab**. Pipeline: sRGB u8 → linearize → OKLab.

```python
def srgb_to_linear(c):                       # c in [0,1]
    c = c.astype(np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)

def linear_to_oklab(rgb):                     # rgb linear-light, (...,3)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    l = 0.4122214708*r + 0.5363325363*g + 0.0514459929*b
    m = 0.2119034982*r + 0.6806995451*g + 0.1073969566*b
    s = 0.0883024619*r + 0.2817188376*g + 0.6299787005*b
    l_, m_, s_ = np.cbrt(l), np.cbrt(m), np.cbrt(s)
    L = 0.2104542553*l_ + 0.7936177850*m_ - 0.0040720468*s_
    a = 1.9779984951*l_ - 2.4285922050*m_ + 0.4505937099*s_
    bb = 0.0259040371*l_ + 0.7827717662*m_ - 0.8086757660*s_
    return np.stack([L, a, bb], axis=-1)

def srgb_u8_to_oklab(rgb_u8):
    return linear_to_oklab(srgb_to_linear(rgb_u8 / 255.0))
```

These are the canonical Björn Ottosson OKLab matrices — reproduce them exactly.

**Nearest-palette lookup.** Snap each pixel/representative colour to the index of the closest
palette entry by squared OKLab distance. Chunk it to bound memory (palette × pixels can be huge):

```python
def nearest_indices(px_oklab, pal_oklab, chunk=65536):
    M = px_oklab.shape[0]
    out = np.empty(M, dtype=np.int32)
    for i in range(0, M, chunk):
        block = px_oklab[i:i+chunk]
        d = block[:, None, :] - pal_oklab[None, :, :]          # (chunk, N, 3)
        out[i:i+chunk] = np.argmin(np.einsum("cnk,cnk->cn", d, d), axis=1)
    return out
```

---

## 2. Palette resolution

A palette spec resolves (in this order) to an `(N,3)` uint8 array:

- `"adaptive"` — derive a ≤256-colour palette **from the image itself** via median-cut
  (PIL `Image.ADAPTIVE`). Optionally pre-resize to the target grid first so the palette is
  derived at sprite resolution.
- **inline hex list** — `"aabbcc, 112233, ..."` (any run of 6 hex digits, `#` optional,
  comma/space/newline separated; needs ≥2 tokens). This is how a **per-character LOCKED palette**
  is fed so a sprite sheet / animation stays colour-consistent frame to frame.
- **bundled name** — `"chroma-256"` → `palettes/chroma-256.hex`, or an absolute/relative `.hex`
  path. `.hex` = one 6-hex colour per line; lines starting `;` or `#`-comment are skipped.

```python
def adaptive_palette(img_u8, n=256, grid=None):
    im = Image.fromarray(img_u8)
    if grid is not None:
        im = im.resize(grid, Image.BOX)
    p = im.convert("P", palette=Image.ADAPTIVE, colors=n).convert("RGB")
    return np.unique(np.asarray(p, np.uint8).reshape(-1, 3), axis=0)
```

`resolve_palette(spec, img_u8=None, grid=None)` dispatches over the three cases and raises if
`adaptive` is requested without an image or a name/path can't be found. **Fail loud** — never
silently fall back to a default palette.

Bundled palettes shipped: `chroma-256`, `vibrant-256`, `xterm-256`, `town-adaptive-256`,
`aap-splendor128`. (`adaptive` is the default and usually best — it tracks the source's hues.)

---

## 3. The grid (virtual resolution)

The sprite's pixel count. Either an explicit `grid_w × grid_h`, or — preferred — a single
**`virtual_resolution`** = the count of virtual pixels on the **longest** edge, with the short
edge derived from the image aspect. This decouples the sprite's pixel count from whatever
resolution the model renders at.

```python
def grid_for_aspect(W, H, grid_w, grid_h, virtual_resolution=0):
    if virtual_resolution and virtual_resolution > 0:
        if W >= H:
            return int(virtual_resolution), max(1, round(virtual_resolution * H / W))
        return max(1, round(virtual_resolution * W / H)), int(virtual_resolution)
    return int(grid_w), int(grid_h)
```

Cells need not divide the image evenly. Cell boundaries are computed by rounding a linspace, so
cell sizes differ by at most one pixel — no fractional-pixel drift:

```python
def _cell_bounds(length, n):
    return np.round(np.linspace(0, length, n + 1)).astype(int)
```

---

## 4. `quantize()` — image → index grid

Signature: `quantize(img_u8 (H,W,3), grid_w, grid_h, palette (N,3), method="median") -> (grid_h, grid_w) int32`

Every method reduces each cell to **one** palette index. They differ only in *how the cell's
representative colour is chosen* — and that only matters **at edges**. There are two families:

**A. Representative-colour-then-snap (edge-STABLE):** compute one colour per cell, snap once.
**B. Per-pixel-snap-then-vote (can be edge-UNSTABLE):** snap every pixel, then pick a winner per cell.

### 4.1 `median` — the default, and why it wins

```python
if method == "median":
    rep = np.empty((grid_h, grid_w, 3), dtype=np.float64)
    for gy in range(grid_h):
        y0, y1 = ys[gy], ys[gy+1]
        for gx in range(grid_w):
            x0, x1 = xs[gx], xs[gx+1]
            rep[gy, gx] = np.median(img[y0:y1, x0:x1].reshape(-1, 3), axis=0)   # per-channel median
    return snap_srgb(rep)                                                       # snap once, in OKLab
```

where the shared snap is:

```python
def snap_srgb(rep):                                  # (...,3) sRGB float -> palette index, matched in OKLab
    ok = linear_to_oklab(srgb_to_linear(np.clip(rep, 0, 255) / 255.0))
    return nearest_indices(ok.reshape(-1, 3), pal_oklab).reshape(rep.shape[:-1])
```

**Why median is the right default.** A grid cell that straddles a colour boundary contains a
*majority* of one colour and a *minority* of the anti-aliased edge tail (plus a sliver of the
other side). Three failure modes to avoid at that boundary cell:

- **`mode`** (snap-every-pixel, take most common index): a near-tie cell **flips** between the
  two extreme colours on a sub-pixel wiggle → a **sawtooth** crawling along straight lines. Crisp
  but bistable.
- **`box` / mean** (average the cell, snap once): the average lands *between* palette entries, so
  the boundary cell picks up a blended anti-aliased colour → **soft** edges (visible halo).
- **`median`**: the per-channel median **ignores minority outliers**, so the thin AA tail can't
  move the cell's representative colour off the majority colour. You get the majority colour
  *exactly* (crisp like mode) but it's a **stable** statistic that can't flip on sub-pixel noise
  (straight like box). Crisp flats **and** straight edges — no sawtooth, no halo.

That combination is why `median` is the default for the standalone quantizer, the per-step
projection target, and the final render.

### 4.2 The other methods (keep selectable for experimentation)

| method | family | one-line behaviour |
|---|---|---|
| `median` | A | per-channel median of cell, then snap. **Crisp + straight. Default.** |
| `mode` | B | snap every pixel, most-common index per cell. Crisp but bistable (sawtooth). |
| `box` | A | PIL BOX area-average to grid, then snap. Straight but soft edges. |
| `lanczos` | A | PIL Lanczos downscale, then snap. A touch sharper than box; can ring. |
| `mean_srgb` | A | area-mean in gamma sRGB, then snap. (Same idea as box.) |
| `mean_linear` | A | area-mean in **linear** light, then snap. Physically-correct edge blend (sRGB averaging biases edges too light). |
| `mean_oklab` | A | area-mean in **OKLab**, then snap. Most perceptually even blend. |
| `nearest_present` | B | snap cell mean to the nearest palette colour that **actually occurs** in the cell (≥15% of area). Stable like mean, real colour like mode, keeps thin features better than median. |
| `var_hybrid` | B | near-uniform cells (≥85% one index) use `mode` (crisp flat); edge cells use mean-snap (stable). |
| `supersample_mode` | B | snap a 2×-finer area-mean, then mode-collapse each 2×2 (de-noises mode's sub-cell vote). |

Reference implementations of the family-A averaging variants (all share `snap_srgb` /
`nearest_indices`):

```python
# area-mean over the uneven cell grid -> (gh, gw, C)
def _cell_means(field, xs, ys):
    cs = np.add.reduceat(field, xs[:-1], axis=1)
    cs = np.add.reduceat(cs, ys[:-1], axis=0)
    area = (np.diff(ys)[:, None] * np.diff(xs)[None, :])[..., None]
    return cs / area

if method in ("box", "lanczos"):
    flt = Image.BOX if method == "box" else Image.LANCZOS
    small = np.asarray(Image.fromarray(img).resize((grid_w, grid_h), flt), np.uint8)
    return nearest_indices(srgb_u8_to_oklab(small.reshape(-1, 3)), pal_oklab).reshape(grid_h, grid_w)

if method in ("mean_srgb", "mean"):
    return snap_srgb(_cell_means(img.astype(np.float64), xs, ys))
if method == "mean_linear":
    rep = _cell_means(srgb_to_linear(img / 255.0), xs, ys)
    return nearest_indices(linear_to_oklab(rep).reshape(-1, 3), pal_oklab).reshape(grid_h, grid_w)
if method == "mean_oklab":
    rep = _cell_means(srgb_u8_to_oklab(img.reshape(-1, 3)).reshape(H, W, 3), xs, ys)
    return nearest_indices(rep.reshape(-1, 3), pal_oklab).reshape(grid_h, grid_w)
```

Family-B variants all start from the per-pixel nearest map
`full_idx = nearest_indices(srgb_u8_to_oklab(img.reshape(-1,3)), pal_oklab).reshape(H, W)` and
then vote per cell (`np.bincount(cell, minlength=n_pal).argmax()` for `mode`; see the method table
for the others). See the full listing in §4.1's source file for the exact `nearest_present` /
`var_hybrid` / `supersample_mode` bodies.

---

## 5. `render()` — index grid → RGB

```python
def render(grid, palette, scale=1):
    img = palette[grid]                                    # (gh, gw, 3) index -> colour
    if scale > 1:
        img = np.repeat(np.repeat(img, scale, axis=0), scale, axis=1)   # nearest upscale (hard pixels)
    return img.astype(np.uint8)
```

Always **nearest** upscale — pixels must stay hard squares.

---

## 6. Node 1 — `PixelQuantize` (standalone / final renderer)

ComfyUI IMAGE tensors are `(B,H,W,3)` float `[0,1]`. The node converts to uint8, quantizes each
batch item, and **renders the block grid back to the INPUT resolution** so the output is "the
input, blockified" (same WxH, hard pixel cells):

```python
def run(self, image, grid_w, grid_h, palette, method, virtual_resolution=0):
    if image.ndim == 5:                                  # (B,T,H,W,3) video VAE -> flatten frames into batch
        image = image.reshape(-1, *image.shape[2:])
    batch = (image.clamp(0,1).cpu().numpy() * 255 + 0.5).astype(np.uint8)
    out = []
    for i in range(batch.shape[0]):
        arr = batch[i]; H, W = arr.shape[:2]
        gw, gh = quant.grid_for_aspect(W, H, grid_w, grid_h, virtual_resolution)
        pal  = quant.resolve_palette(palette, img_u8=arr, grid=(gw, gh))
        grid = quant.quantize(arr, gw, gh, pal, method=method)
        blocks = quant.render(grid, pal, scale=1)                      # (gh, gw, 3)
        full = np.asarray(Image.fromarray(blocks).resize((W, H), Image.NEAREST), np.uint8)
        out.append(full)
    return (torch.from_numpy(np.stack(out).astype(np.float32) / 255.0),)
```

**Inputs:** `image`, `grid_w` (default 384), `grid_h` (default 256), `palette` (default
`"chroma-256"`; `"adaptive"` recommended), `method` (default `"median"`), optional
`virtual_resolution` (>0 overrides grid_w/h from aspect). **Output:** `IMAGE`.

Use it two ways: (a) on its own to pixelize a still/frame; (b) as the **last** node after a VAE
decode so VAE noise never reaches the saved output — this is the *authoritative* render even when
a diffusion projection was already steering the latent.

---

## 7. Node 2 — `PixelManifoldProjection` (the pipeline integration)

**This is the important half.** It is a `MODEL` → `MODEL` patch. You insert it between your model
loader and a stock `KSampler` (or any sampler that respects post-CFG hooks). It registers a
**post-CFG callback** that runs every denoise step on the `x0` estimate (`denoised`), pulling that
estimate toward the pixel-art manifold. The model still does the sampling; the patch just bends
the trajectory.

### 7.1 Where it sits in the graph

```
Loader → PixelManifoldProjection(model, vae, grid/palette/method, ramp…) → KSampler → VAEDecode → PixelQuantize → Save
                                   (patches the model)                       (stock)                (authoritative render)
```

The sampler must run at a working resolution of roughly **grid × block** (i.e. the decoded image
resolution should be a clean multiple of the sprite grid) so the decoded image and the projected
render line up cell-for-cell. (The orchestrator computes a "snapped" render size = an exact
integer multiple `k` of the virtual resolution within the model's resolution range; see §9.)

### 7.2 The per-step projection (decode → quantize → re-encode → blend)

```python
def patch(self, model, vae, grid_w, grid_h, palette, method, w_start, w_end,
          start_percent, end_percent, project_every, virtual_resolution=0):
    m = model.clone()
    state = {"n": 0}

    def post_cfg(args):
        denoised = args["denoised"]            # (B,C,H,W) latent x0 estimate, MODEL latent space
        sigma    = args["sigma"]

        # --- locate this step within the actual denoise window, as a fraction 0..1 ---
        sched = args.get("model_options", {}).get("transformer_options", {}).get("sample_sigmas")
        if sched is not None and len(sched) > 1:
            cur  = sigma.flatten()[0]
            idx  = int(torch.argmin(torch.abs(sched.to(cur.device) - cur)).item())
            frac = idx / max(len(sched) - 2, 1)          # ramp over i in 0..nsteps-1
        else:
            frac = 1.0
        frac = min(max(frac, 0.0), 1.0)

        # --- window + cadence gates ---
        if frac < start_percent or frac > end_percent:
            return denoised                              # outside the projection window: let the model draw freely
        state["n"] += 1
        if (state["n"] - 1) % project_every != 0:
            return denoised                              # skip this step (speed knob)
        w = w_start + (w_end - w_start) * frac           # projection weight ramps with progress

        # --- LATENT SPACE CONVERSION (critical) ---
        # `denoised` is in the model's INTERNAL latent space (post process_latent_in). The VAE works in
        # VAE latent space. For Flux these differ by a shift+scale; skipping the conversion mangles colour
        # and rings every edge. Always convert model->VAE before decode and VAE->model after encode.
        model = args["model"]
        img = vae.decode(model.process_latent_out(denoised))    # (B,H,W,3)  [(B,T,H,W,3) for video VAEs]

        # (video VAEs: flatten the temporal axis into the batch here; restore after — see §7.4)
        arr = (img.detach().clamp(0,1).cpu().numpy() * 255 + 0.5).astype(np.uint8)

        out = []
        for b in range(arr.shape[0]):
            H, W = arr[b].shape[:2]
            gw, gh = quant.grid_for_aspect(W, H, grid_w, grid_h, virtual_resolution)
            pal = quant.resolve_palette(palette, img_u8=arr[b], grid=(gw, gh))
            g   = quant.quantize(arr[b], gw, gh, pal, method=method)
            r   = quant.render(g, pal, scale=max(1, round(W / gw)))
            if r.shape[0] != H or r.shape[1] != W:
                r = np.asarray(Image.fromarray(r).resize((W, H), Image.NEAREST), np.uint8)
            out.append(r)
        proj = torch.from_numpy(np.stack(out).astype(np.float32) / 255.0).to(img)

        proj_lat = model.process_latent_in(vae.encode(proj)).to(denoised)   # VAE space -> model space
        return (1.0 - w) * denoised + w * proj_lat                          # blend toward the manifold

    # disable_cfg1_optimization=True: Flux-dev / Qwen-Edit run at CFG 1, where ComfyUI's cfg-1 shortcut
    # can bypass the post-CFG hook. Force the full path so the projection runs every step.
    m.set_model_sampler_post_cfg_function(post_cfg, disable_cfg1_optimization=True)
    return (m,)
```

### 7.3 The five things that make it correct

1. **Model-agnostic.** ComfyUI hands the post-CFG hook the *normalized* `denoised` (x0) for any
   architecture — SDXL (eps), Flux/Qwen (flow-matching) — so **one** implementation covers them
   all. (A from-scratch sampler would need separate eps vs flow-matching code paths; the hook
   abstracts that away.)
2. **Latent-space round-trip.** `denoised` is in the model's internal latent space; the VAE is
   not. Convert `process_latent_out` before `vae.decode` and `process_latent_in` after
   `vae.encode`. Omitting this is the #1 bug — it silently shifts colour and rings every edge.
3. **Weight ramp `w = w_start + (w_end - w_start)·frac`.** Early steps (low `frac`) use a small
   weight so the model is free to compose; late steps ramp to `w_end` (default 1.0) to **snap
   hard**. Flux/Qwen defaults: `w_start=0.5`, `w_end=1.0`. Projecting at full strength from step 0
   freezes structure too early and looks stamped; never projecting late leaves VAE mush.
4. **`frac` is indexed off the *actual* denoise window.** Read `sample_sigmas` from
   `transformer_options` and find the current sigma's index — so with `denoise < 1` (img2img tail)
   the ramp still spans the steps that actually run, not the full theoretical schedule.
5. **`start_percent`/`end_percent`/`project_every`** gate *where* and *how often* the clamp
   engages. Raising `start_percent` lets the model lay down composition before the grid bites;
   `project_every > 1` trades manifold pull for speed.

The in-loop projection only **steers**; the crisp, authoritative output is the **separate**
`PixelQuantize` at the end of the graph (§6), so VAE decode noise never lands in the final image.

### 7.4 Video VAEs (optional)

A multi-frame video VAE decodes to `(B,T,H,W,3)`. Flatten `T` into the batch for the loop, then
restore the shape before re-encode. **Lock the palette across frames** (resolve once on frame 0,
reuse) so the clip projects onto one fixed palette and doesn't shimmer. Causal video VAEs may not
round-trip the frame count exactly (decode `T→P`, encode `P→T'` can differ by ~1 at the
reference-frame boundary); project only the overlapping frames and leave any unmatched tail frame
as the model produced it (the final `PixelQuantize` still pixelizes it). For an image VAE this
whole branch is a no-op.

### 7.5 Inputs

| input | default | meaning |
|---|---|---|
| `model`, `vae` | — | the model to patch + its VAE (for the decode/encode round-trip) |
| `grid_w`, `grid_h` | 384, 256 | explicit grid (ignored if `virtual_resolution > 0`) |
| `palette` | `"chroma-256"` | `adaptive` / inline hex / bundled name |
| `method` | `"median"` | per-step projection-target quantizer (use `median`) |
| `w_start`, `w_end` | 0.5, 1.0 | projection weight ramp over the denoise window |
| `start_percent`, `end_percent` | 0.0, 1.0 | restrict projection to a slice of the window |
| `project_every` | 1 | project every Nth step (speed knob) |
| `virtual_resolution` | 0 | >0: derive grid from decoded aspect, longest edge = this |

---

## 8. Integration recipes (per model family)

The projection patch is identical across models; only the *surrounding* edit graph changes. The
two universal pieces in every recipe: **(a)** the `PixelManifoldProjection` patch feeds the
sampler's `model`, and **(b)** a final `PixelQuantize` renders the decode. A `reference %` knob
maps to sampler **denoise** (`denoise = clamp(1 - reference/100, 0.01, 1.0)`): 0 = generate fresh,
100 = copy the source then just quantize it.

- **Plain img2img (Flux-dev / SDXL-style):** `VAEEncode(source)` → `KSampler(model=patched,
  latent=encoded, denoise=from reference%)` → decode → `PixelQuantize`. Low denoise (~0.3) +
  strong w-ramp: the projection leads, the model only cleans up.
- **Flux.1-Kontext:** mirror the Kontext edit graph (`CLIPTextEncode` → `ReferenceLatent` on the
  source's encoded latent → `FluxGuidance`), patch the model, sample, final-quantize.
- **Flux.2-Klein:** custom-sampler graph (`BasicGuider` + `SamplerCustomAdvanced` over a fresh
  Flux.2 latent); patch the model *before* the guider. For img2img, split the sigmas
  (`SplitSigmasDenoise`, low_sigmas = denoise fraction) and init from the source latent.
- **Qwen-Image-Edit (generate pixel art *directly* from a reference):**
  `TextEncodeQwenImageEditPlus(image1=ref)` + optional `ReferenceLatent`, `ModelSamplingAuraFlow`
  + `CFGNorm` sampling fix, then the projection patch. With `reference=0` it generates a fresh
  on-character design each seed (empty init, no ReferenceLatent); with `reference>0` it injects the
  source latent and img2img's it. The model redraws while the projection clamps every step — they
  cooperate instead of the model fighting a finished image.
- **Self-contained pipeline nodes (e.g. DreamOmni2):** when a node runs its whole diffusion
  internally, port the *same* projection math *inside* that node (decode→quantize→re-encode→blend
  the flow-matching x0 each step) rather than patching from outside, then still final-quantize the
  node's output.

---

## 9. Resolution snapping (recommended, orchestration-side)

To get **exact** k×k pixel cells (no resample fuzz between the grid and the render), pick a render
size whose long edge is an integer multiple `k` of the virtual resolution, with both dims a
multiple of the model's latent step and inside the model's resolution range. Then the decoded
image divides the sprite grid evenly and every `PixelQuantize` cell is a clean k×k block. Aspect
is a soft target (drift a few percent to hit a clean snap). This is computed before the job runs
from `(virtual_resolution, requested w/h, model min/max side, latent step)`:

```
gw     = round(vres / step) * step                  # grid long edge, step-aligned
k      = clamp(round(long / gw), kMin, kMax)         # integer cell multiplier within model range
m      = step / gcd(k, step)                         # keep k*grid_short divisible by step
gridShort = max(m, round(gw * short/long / m) * m)
render = (k*gw, k*gridShort)  (oriented to the requested aspect)
```

If snapping is off, render at an aspect-preserving megapixel area instead and let the
`NEAREST`-resize in the nodes handle the (now non-integer) block mapping.

---

## 10. Pre-processing: flatten alpha onto WHITE

Before quantizing a source with transparency, composite RGBA → RGB **on white** (not the default
black). A transparent background composited on black haloes a dark fringe around lit/soft-glow
edges; white avoids it. In ComfyUI: `GetImageSize` → `EmptyImage(color=0xFFFFFF)` →
`InvertMask(source alpha)` → `ImageCompositeMasked`. A no-op for sources without alpha.

---

## 11. Quick reimplementation checklist

1. Port `quant.py` verbatim: OKLab transforms (exact matrices), `nearest_indices` (chunked),
   `resolve_palette` (adaptive / inline-hex / file), `grid_for_aspect`, `quantize` (start with
   `median` + `box` + `mode`), `render` (nearest upscale).
2. Wrap `PixelQuantize`: u8 convert → per-batch quantize → block grid → `NEAREST` resize back to
   input WxH. Default `method="median"`, `palette="adaptive"`.
3. Wrap `PixelManifoldProjection` as a `MODEL` patch via `set_model_sampler_post_cfg_function(...,
   disable_cfg1_optimization=True)`. In the hook: locate `frac` from `sample_sigmas`, gate on
   window/cadence, ramp `w`, **convert latent spaces around the VAE round-trip**, decode → quantize
   → render → encode → blend `(1-w)·denoised + w·proj_lat`.
4. Always end the graph with a separate `PixelQuantize` after `VAEDecode`.
5. Default everything to `median`. It is the only method that is simultaneously crisp (like mode)
   and straight-edged (like box) — that is the whole reason it's the default.
```
