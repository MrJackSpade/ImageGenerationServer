"""Pixel-art manifold projector + final renderer. Pure NumPy/PIL, zero ML.

Lifted from the pixelharness POC (quantizer.py) verbatim in spirit: snap every pixel to
the nearest palette colour in OKLab, reduce to a fixed grid (mode-per-cell keeps flats
crisp), and render an indexed grid back to RGB. OKLab distance so "nearest" matches human
perception, not raw RGB.

This module is the single source of truth for the quantization math, shared by both the
PixelQuantize node (final/standalone) and the PixelManifoldProjection node (per-step
projection target during diffusion).
"""
from __future__ import annotations

import os
import re

import numpy as np
from PIL import Image

PALETTE_DIR = os.path.join(os.path.dirname(__file__), "palettes")


# ---------------------------------------------------------------- colour space
def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    c = c.astype(np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_oklab(rgb: np.ndarray) -> np.ndarray:
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b
    m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b
    s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b
    l_, m_, s_ = np.cbrt(l), np.cbrt(m), np.cbrt(s)
    L = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_
    a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_
    bb = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
    return np.stack([L, a, bb], axis=-1)


def srgb_u8_to_oklab(rgb_u8: np.ndarray) -> np.ndarray:
    return linear_to_oklab(srgb_to_linear(rgb_u8 / 255.0))


# -------------------------------------------------------- alpha / background
def downsample_alpha(opaque: np.ndarray, grid_w: int, grid_h: int) -> np.ndarray:
    """Opaque mask (H,W bool) -> 1-bit grid alpha (grid_h,grid_w uint8, 0/255). A grid cell is opaque
    iff at least half of its source pixels are opaque — hard-thresholded so sprite edges stay crisp
    (no soft/AA alpha ramp), matching the pixel-art contract of one flat value per cell."""
    xs = _cell_bounds(opaque.shape[1], grid_w)
    ys = _cell_bounds(opaque.shape[0], grid_h)
    o = opaque.astype(np.float64)
    cs = np.add.reduceat(np.add.reduceat(o, xs[:-1], axis=1), ys[:-1], axis=0)
    area = (np.diff(ys)[:, None] * np.diff(xs)[None, :])
    return np.where(cs / area >= 0.5, 255, 0).astype(np.uint8)


# -------------------------------------------------------------------- palette
def adaptive_palette(img_u8: np.ndarray, n: int = 256, grid=None,
                     opaque: np.ndarray | None = None) -> np.ndarray:
    """Derive an <=n colour palette from the image itself (median cut).

    When `opaque` (H,W bool) is given, the palette is built from the subject pixels ONLY, so a keyed
    frame's transparent (background) region can't consume palette slots or drag the median cut. The
    opaque pixels are packed into a synthetic tall image for the cut."""
    if opaque is not None:
        px = img_u8[opaque].reshape(-1, 3)
        if px.size == 0:
            return np.zeros((1, 3), np.uint8)
        im = Image.fromarray(px[None, :, :])       # (1, K, 3): every opaque pixel, no background
    else:
        im = Image.fromarray(img_u8)
        if grid is not None:
            im = im.resize(grid, Image.BOX)
    p = im.convert("P", palette=Image.ADAPTIVE, colors=n).convert("RGB")
    return np.unique(np.asarray(p, np.uint8).reshape(-1, 3), axis=0)


def _parse_hex_list(text: str) -> np.ndarray | None:
    """Parse an inline palette: any run of 6 hex digits ('#' optional), comma/space/newline
    separated. Returns (N,3) uint8, or None if fewer than two colours were found (so the
    caller can fall back to treating the string as a filename)."""
    toks = re.findall(r"[0-9a-fA-F]{6}", text)
    if len(toks) < 2:
        return None
    return np.array([(int(t[0:2], 16), int(t[2:4], 16), int(t[4:6], 16)) for t in toks],
                    dtype=np.uint8)


def load_palette_file(path: str) -> np.ndarray:
    colors = []
    with open(path) as f:
        for line in f:
            s = line.strip().lstrip("#")
            if not s or s.startswith(";"):
                continue
            colors.append((int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16)))
    if not colors:
        raise ValueError(f"no colours parsed from {path}")
    return np.array(colors, dtype=np.uint8)


def resolve_palette(spec: str, img_u8: np.ndarray | None = None, grid=None,
                    opaque: np.ndarray | None = None) -> np.ndarray:
    """Resolve a palette spec into (N,3) uint8. Accepts, in order:
      - 'adaptive'        -> derive from img_u8 (requires img_u8); `opaque` restricts it to the subject
      - an inline hex list -> 'aabbcc, 112233, ...' (two or more 6-hex tokens)
      - a bundled name    -> 'chroma-256' or 'chroma-256.hex' under palettes/
      - an absolute/relative .hex path
    """
    spec = (spec or "").strip()
    if spec.lower() == "adaptive":
        if img_u8 is None:
            raise ValueError("adaptive palette requested but no image provided")
        return adaptive_palette(img_u8, n=256, grid=grid, opaque=opaque)
    inline = _parse_hex_list(spec)
    if inline is not None:
        return inline
    cand = spec if os.path.isabs(spec) else os.path.join(PALETTE_DIR, spec)
    for path in (cand, cand + ".hex"):
        if os.path.isfile(path):
            return load_palette_file(path)
    raise FileNotFoundError(f"palette '{spec}' not found (not 'adaptive', not an inline hex "
                            f"list, not a file under {PALETTE_DIR})")


# ------------------------------------------------------------------- snapping
def nearest_indices(px_oklab: np.ndarray, pal_oklab: np.ndarray, chunk: int = 65536) -> np.ndarray:
    """For each pixel (M,3 OKLab) return index of nearest palette entry, chunked for memory."""
    M = px_oklab.shape[0]
    out = np.empty(M, dtype=np.int32)
    for i in range(0, M, chunk):
        block = px_oklab[i:i + chunk]
        d = block[:, None, :] - pal_oklab[None, :, :]
        out[i:i + chunk] = np.argmin(np.einsum("cnk,cnk->cn", d, d), axis=1)
    return out


def _cell_bounds(length: int, n: int) -> np.ndarray:
    return np.round(np.linspace(0, length, n + 1)).astype(int)


def grid_for_aspect(W: int, H: int, grid_w: int, grid_h: int, virtual_resolution: int = 0):
    """Resolve the target pixel grid (the sprite's 'virtual resolution'). If virtual_resolution > 0, derive the
    grid from the image's aspect with the LONGEST edge = virtual_resolution — so the sprite has that many virtual
    pixels on its long side regardless of the render size or the model's working resolution. Otherwise use the
    explicit grid_w x grid_h."""
    if virtual_resolution and virtual_resolution > 0:
        if W >= H:
            return int(virtual_resolution), max(1, int(round(virtual_resolution * H / W)))
        return max(1, int(round(virtual_resolution * W / H))), int(virtual_resolution)
    return int(grid_w), int(grid_h)


# ------------------------------------------------------------------ quantize
def _cell_means(field: np.ndarray, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    """Area-mean of `field` (H,W,C) over the (possibly uneven) cell grid -> (gh,gw,C)."""
    cs = np.add.reduceat(field, xs[:-1], axis=1)
    cs = np.add.reduceat(cs, ys[:-1], axis=0)
    area = (np.diff(ys)[:, None] * np.diff(xs)[None, :])[..., None]
    return cs / area


def quantize(img: np.ndarray, grid_w: int, grid_h: int, palette: np.ndarray,
             method: str = "median") -> np.ndarray:
    """img (H,W,3) uint8 -> (grid_h, grid_w) int32 palette-index grid.

    Every method reduces the source to one palette index per grid cell; they differ in HOW the cell's
    representative colour is chosen, which only matters at edges:

      'median' (default) — per-channel median of the cell, then snap. The median ignores minority
                outliers, so a thin anti-aliased edge tail can't tip a boundary cell: crisp flats AND
                straight edges. (mode's failure mode — a near-tie cell flipping between the two extreme
                colours on a sub-pixel wiggle, i.e. a sawtooth on straight lines — cannot happen here.)
      'mode'   — snap every pixel, take the most-common index per cell. Crisp, but a boundary cell is a
                near-tie that flips on sub-pixel noise -> sawtooth. (Kept for backwards compatibility.)
      'box'    — area-average the cell, then snap once. Straight edges, but the average lands between
                palette entries so edges pick up anti-aliased blend colours (softer).
      'nearest_present' — snap the cell's mean to the nearest palette colour that ACTUALLY occurs in the
                cell (>=15% of its area). Decision is stable like the mean, output is always a real
                colour like mode -> crisp AND straight, and keeps thin features better than median.

    Additional variants (kept selectable for experimentation; all are edge-stable like the averaging
    family, differing only in the averaging space / collapse rule):
      'mean_srgb'   — area-mean in gamma sRGB, then snap (PIL 'box' is the same idea via its kernel).
      'mean_linear' — area-mean in LINEAR light, then snap (physically correct edge blend; sRGB
                      averaging biases edges too light).
      'mean_oklab'  — area-mean in perceptual OKLab, then snap (most perceptually even blend).
      'lanczos'     — Lanczos-kernel downscale, then snap (a touch sharper than box; can ring).
      'var_hybrid'  — near-uniform cells use 'mode' (crisp flat), edge cells use the mean snap (stable).
      'supersample_mode' — snap a 2x-finer area-mean, then mode-collapse each 2x2 (de-noises the sub-cell
                      vote that makes plain 'mode' bistable, while staying crisp).
    """
    H, W = img.shape[:2]
    pal_oklab = srgb_u8_to_oklab(palette.reshape(-1, 3)).reshape(-1, 3)
    n_pal = palette.shape[0]
    xs = _cell_bounds(W, grid_w)
    ys = _cell_bounds(H, grid_h)

    def snap_srgb(rep):
        """(...,3) sRGB float -> palette index, matched in OKLab."""
        ok = linear_to_oklab(srgb_to_linear(np.clip(rep, 0, 255) / 255.0))
        return nearest_indices(ok.reshape(-1, 3), pal_oklab).reshape(rep.shape[:-1])

    # ---- representative-colour methods: one colour per cell, then snap (edge-stable) ----
    if method in ("box", "lanczos"):
        flt = Image.BOX if method == "box" else Image.LANCZOS
        small = np.asarray(Image.fromarray(img).resize((grid_w, grid_h), flt), dtype=np.uint8)
        return nearest_indices(srgb_u8_to_oklab(small.reshape(-1, 3)), pal_oklab).reshape(grid_h, grid_w)

    if method in ("mean_srgb", "mean"):
        return snap_srgb(_cell_means(img.astype(np.float64), xs, ys))

    if method == "mean_linear":
        rep = _cell_means(srgb_to_linear(img / 255.0), xs, ys)
        return nearest_indices(linear_to_oklab(rep).reshape(-1, 3), pal_oklab).reshape(grid_h, grid_w)

    if method == "mean_oklab":
        rep = _cell_means(srgb_u8_to_oklab(img.reshape(-1, 3)).reshape(H, W, 3), xs, ys)
        return nearest_indices(rep.reshape(-1, 3), pal_oklab).reshape(grid_h, grid_w)

    if method == "median":
        rep = np.empty((grid_h, grid_w, 3), dtype=np.float64)
        for gy in range(grid_h):
            y0, y1 = ys[gy], ys[gy + 1]
            for gx in range(grid_w):
                x0, x1 = xs[gx], xs[gx + 1]
                rep[gy, gx] = np.median(img[y0:y1, x0:x1].reshape(-1, 3), axis=0)
        return snap_srgb(rep)

    if method == "supersample_mode":
        fine = snap_srgb(np.asarray(Image.fromarray(img).resize((grid_w * 2, grid_h * 2), Image.BOX), np.float64))
        grid = np.empty((grid_h, grid_w), dtype=np.int32)
        for gy in range(grid_h):
            for gx in range(grid_w):
                blk = fine[2 * gy:2 * gy + 2, 2 * gx:2 * gx + 2].ravel()
                grid[gy, gx] = np.bincount(blk, minlength=n_pal).argmax()
        return grid

    # ---- methods that need the per-pixel nearest-palette map ----
    full_idx = nearest_indices(srgb_u8_to_oklab(img.reshape(-1, 3)), pal_oklab).reshape(H, W)

    if method == "mode":
        grid = np.empty((grid_h, grid_w), dtype=np.int32)
        for gy in range(grid_h):
            y0, y1 = ys[gy], ys[gy + 1]
            for gx in range(grid_w):
                x0, x1 = xs[gx], xs[gx + 1]
                cell = full_idx[y0:y1, x0:x1].ravel()
                grid[gy, gx] = np.bincount(cell, minlength=n_pal).argmax()
        return grid

    if method == "nearest_present":
        mean_ok = _cell_means(srgb_u8_to_oklab(img.reshape(-1, 3)).reshape(H, W, 3), xs, ys)
        grid = np.empty((grid_h, grid_w), dtype=np.int32)
        for gy in range(grid_h):
            y0, y1 = ys[gy], ys[gy + 1]
            for gx in range(grid_w):
                x0, x1 = xs[gx], xs[gx + 1]
                cell = full_idx[y0:y1, x0:x1].ravel()
                cnt = np.bincount(cell, minlength=n_pal)
                present = np.where(cnt >= max(1, int(0.15 * cell.size)))[0]
                if present.size == 0:
                    present = np.array([cnt.argmax()])
                d = pal_oklab[present] - mean_ok[gy, gx]
                grid[gy, gx] = present[np.argmin(np.einsum("ij,ij->i", d, d))]
        return grid

    if method == "var_hybrid":
        mean_idx = snap_srgb(_cell_means(img.astype(np.float64), xs, ys))
        grid = np.empty((grid_h, grid_w), dtype=np.int32)
        for gy in range(grid_h):
            y0, y1 = ys[gy], ys[gy + 1]
            for gx in range(grid_w):
                x0, x1 = xs[gx], xs[gx + 1]
                cell = full_idx[y0:y1, x0:x1].ravel()
                cnt = np.bincount(cell, minlength=n_pal)
                mc = cnt.argmax()
                grid[gy, gx] = mc if cnt[mc] >= 0.85 * cell.size else mean_idx[gy, gx]
        return grid

    raise ValueError(f"unknown method {method!r}")


def render(grid: np.ndarray, palette: np.ndarray, scale: int = 1) -> np.ndarray:
    """index grid -> RGB image, nearest-upscaled by `scale`."""
    img = palette[grid]
    if scale > 1:
        img = np.repeat(np.repeat(img, scale, axis=0), scale, axis=1)
    return img.astype(np.uint8)
