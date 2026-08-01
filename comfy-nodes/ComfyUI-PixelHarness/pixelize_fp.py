"""Feature-preserving video pixelizer node (PixelQuantizeFP).

Port of the standalone pipeline (E:\\pixelharness\\downscale\\pixelize.py) as a ComfyUI node.
Unlike PixelQuantize (per-frame palette snap), this runs the full feature-preserving pipeline
and derives ONE palette across the whole frame batch, so video output is temporally stable
without needing a hand-named locked palette:

  per frame:  L0 flatten -> XDoG line-thicken -> de-AA edge-collapse
  whole clip: DIN99d master lattice -> ONE global per-video palette (pooled k-means)
  per frame:  snap to that palette -> proportion-preserving rarity-weighted majority downsample
              -> nearest-upscale back to source size (same output contract as PixelQuantize)

The node receives the entire (B,H,W,3) / (B,T,H,W,3) batch at once, which is what makes the
single global palette + global rarity possible. Pure CPU (numpy/PIL/cv2/colour/sklearn/scipy).
"""
from __future__ import annotations

import itertools
import os
import uuid

import numpy as np
import torch
from PIL import Image, ImageFilter
import cv2
import colour
from sklearn.cluster import KMeans
from scipy.spatial import cKDTree

import folder_paths   # ComfyUI: resolves the output directory for saved sprite data

from . import quant   # grid_for_aspect (shared with PixelQuantize)


def _img_to_u8(image: torch.Tensor) -> np.ndarray:
    return (image.clamp(0, 1).cpu().numpy() * 255.0 + 0.5).astype(np.uint8)


def _u8_to_img(arr: np.ndarray) -> torch.Tensor:
    return torch.from_numpy(arr.astype(np.float32) / 255.0)


# ---- Stage 1: L0 gradient minimization ----
def _psf2otf(psf, shape):
    pad = np.zeros(shape, dtype=np.float64)
    pad[:psf.shape[0], :psf.shape[1]] = psf
    for ax, sz in enumerate(psf.shape):
        pad = np.roll(pad, -(sz // 2), axis=ax)
    return np.fft.fft2(pad)


def l0_smooth(img, lam=0.015, kappa=2.0, beta_max=1e5):
    S = img.astype(np.float64)
    H, W, _ = S.shape
    otfx = _psf2otf(np.array([[1, -1]]), (H, W))
    otfy = _psf2otf(np.array([[1], [-1]]), (H, W))
    Denormin2 = (np.abs(otfx) ** 2 + np.abs(otfy) ** 2)[..., None]
    FI = np.fft.fft2(S, axes=(0, 1))
    beta = 2 * lam
    while beta < beta_max:
        Denormin = 1 + beta * Denormin2
        h = np.concatenate([np.diff(S, axis=1), (S[:, :1] - S[:, -1:])], axis=1)
        v = np.concatenate([np.diff(S, axis=0), (S[:1, :] - S[-1:, :])], axis=0)
        mask = np.sum(h ** 2 + v ** 2, axis=2) < lam / beta
        h[mask] = 0
        v[mask] = 0
        h_diff = np.concatenate([h[:, -1:] - h[:, :1], -np.diff(h, axis=1)], axis=1)
        v_diff = np.concatenate([v[-1:, :] - v[:1, :], -np.diff(v, axis=0)], axis=0)
        FS = (FI + beta * np.fft.fft2(h_diff + v_diff, axes=(0, 1))) / Denormin
        S = np.real(np.fft.ifft2(FS, axes=(0, 1)))
        beta *= kappa
    return np.clip(S, 0, 1)


# ---- Stage 2: XDoG line thicken ----
def _xdog_lines(arr_u8, sigma, k, tau, epsilon, phi):
    gray = (0.299 * arr_u8[..., 0] + 0.587 * arr_u8[..., 1]
            + 0.114 * arr_u8[..., 2]).astype(np.uint8)
    gimg = Image.fromarray(gray, mode="L")
    g1 = np.asarray(gimg.filter(ImageFilter.GaussianBlur(radius=float(sigma))), np.float32) / 255.0
    g2 = np.asarray(gimg.filter(ImageFilter.GaussianBlur(radius=float(sigma * k))), np.float32) / 255.0
    dog = g1 - float(tau) * g2
    e = np.where(dog >= epsilon, 1.0, 1.0 + np.tanh(float(phi) * (dog - float(epsilon))))
    return np.clip(e, 0.0, 1.0)


def _erode_lines(lines_u8, thickness, supersample=4):
    if thickness <= 0:
        return lines_u8
    if float(thickness).is_integer():
        img = Image.fromarray(lines_u8, mode="RGB")
        for _ in range(int(thickness)):
            img = img.filter(ImageFilter.MinFilter(3))
        return np.asarray(img, dtype=np.uint8)
    H, W = lines_u8.shape[:2]
    s = supersample
    up = Image.fromarray(lines_u8, mode="RGB").resize((W * s, H * s), Image.NEAREST)
    for _ in range(max(1, round(thickness * s))):
        up = up.filter(ImageFilter.MinFilter(3))
    return np.asarray(up.resize((W, H), Image.BOX), dtype=np.uint8)


def line_thicken_xdog(img, thickness=0.75, sigma=1.0, k=1.6, tau=0.98, epsilon=0.0, phi=10.0):
    arr_u8 = (img * 255.0 + 0.5).astype(np.uint8)
    lines = _xdog_lines(arr_u8, sigma, k, tau, epsilon, phi)
    lines_u8 = np.repeat((lines * 255.0 + 0.5).astype(np.uint8)[..., None], 3, axis=2)
    lines_u8 = _erode_lines(lines_u8, thickness)
    return np.clip(img * (lines_u8.astype(np.float32) / 255.0), 0.0, 1.0)


# ---- Stage 3: edge-collapse (de-antialiasing) ----
def collapse_edges(img, tau=0.6, win=1, max_iter=64):
    H, W, _ = img.shape
    ker = np.ones((2 * win + 1, 2 * win + 1), np.uint8)
    rng = (cv2.dilate(img, ker) - cv2.erode(img, ker)).max(axis=2)
    assigned = rng < tau
    out = img.copy()
    INF = 1e18
    shifts = [(-1, 0), (1, 0), (0, -1), (0, 1)]
    for _ in range(max_iter):
        if assigned.all():
            break
        best = np.full((H, W), INF)
        bestcol = np.zeros_like(out)
        for dy, dx in shifts:
            nc = np.roll(out, (dy, dx), axis=(0, 1))
            na = np.roll(assigned, (dy, dx), axis=(0, 1))
            d = np.where(na, np.sqrt(((img - nc) ** 2).sum(axis=2)), INF)
            upd = d < best
            best = np.where(upd, d, best)
            bestcol = np.where(upd[..., None], nc, bestcol)
        frontier = (~assigned) & (best < INF)
        if not frontier.any():
            break
        out[frontier] = bestcol[frontier]
        assigned |= frontier
    return np.clip(out, 0.0, 1.0)


# ---- Stage 4: DIN99d palette ----
def _Lab(u8):
    return colour.XYZ_to_Lab(colour.sRGB_to_XYZ(np.asarray(u8, float) / 255.0))


def _DIN(u8):
    return colour.Lab_to_DIN99(_Lab(np.clip(u8, 0, 255)), method="DIN99d")


def _DIN_to_rgb(d99):
    rgb = colour.XYZ_to_sRGB(colour.Lab_to_XYZ(colour.DIN99_to_Lab(d99, method="DIN99d")))
    return np.round(np.clip(rgb, 0, 1) * 255).astype(np.uint8)


def master_lattice(step=5.6):
    g = np.linspace(0, 255, 52)
    D = _DIN(np.array(list(itertools.product(g, g, g))))
    return np.unique(_DIN_to_rgb(np.unique(np.round(D / step) * step, axis=0)), axis=0)


def _kflat(arr, k=31, seed=0):
    rng = np.random.RandomState(seed)
    s = arr[rng.choice(len(arr), min(len(arr), 40000), replace=False)]
    km = KMeans(k, n_init=3, random_state=seed).fit(s)
    return km.cluster_centers_[km.predict(arr)]


def palette_from_pixels(pixels_float, master, k=31):
    flat = np.unique(_kflat(pixels_float, k), axis=0)
    tree = cKDTree(_DIN(master))
    return np.unique(master[tree.query(_DIN(flat))[1]], axis=0)


def snap_labels(arr_u8, palette):
    return cKDTree(_DIN(palette)).query(_DIN(arr_u8))[1]


# ---- Stage 5: proportion-preserving rarity-weighted majority downsample ----
def _uniform_edges(n, n_out):
    return np.linspace(0, n, n_out + 1).round().astype(int)


def weighted_mode_downsample(labels, palette, out_w, out_h, beta=0.5, rarity=None, opaque=None):
    """Rarity-weighted majority vote per grid cell. When `opaque` (H,W bool) is given, ONLY opaque
    source pixels vote for a cell's colour (transparent pixels can't win a boundary cell) and a 1-bit
    grid alpha is returned alongside — a cell is opaque iff at least half its source pixels are.
    Returns (sprite_rgb, out_lab, alpha_grid); alpha_grid is None when `opaque` is None."""
    H, W = labels.shape
    K = len(palette)
    if rarity is None:
        freq = np.bincount(labels.ravel(), minlength=K).astype(np.float64)
        freq /= freq.sum()
        rarity = (1.0 / (freq + 1e-9)) ** beta
    bx, by = _uniform_edges(W, out_w), _uniform_edges(H, out_h)
    out_lab = np.zeros((out_h, out_w), dtype=labels.dtype)
    alpha_grid = None if opaque is None else np.zeros((out_h, out_w), np.uint8)
    for i in range(out_h):
        rows = labels[by[i]:by[i + 1], :]
        orows = None if opaque is None else opaque[by[i]:by[i + 1], :]
        for j in range(out_w):
            block = rows[:, bx[j]:bx[j + 1]].ravel()
            if opaque is not None:
                oblock = orows[:, bx[j]:bx[j + 1]].ravel()
                alpha_grid[i, j] = 255 if oblock.mean() >= 0.5 else 0
                block = block[oblock]              # colour vote from subject pixels only
                if block.size == 0:                # fully transparent cell -> colour is irrelevant
                    continue
            counts = np.bincount(block, minlength=K).astype(np.float64)
            out_lab[i, j] = np.argmax(counts * rarity)
    return palette[out_lab], out_lab, alpha_grid


class PixelQuantizeFP:
    """Feature-preserving pixelizer with ONE global palette across the batch (temporally stable
    video without a named palette). Output matches PixelQuantize: input frames, blockified at the
    grid then nearest-upscaled back to source resolution."""
    TITLE = "Pixel Quantize (feature-preserving)"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("image",)
    # OUTPUT_NODE so ComfyUI surfaces this node's `ui` dict into /history — that's how the derived palette + the
    # native-resolution LOSSLESS frames (both computed here and otherwise discarded) reach Forge, which persists them
    # keyed to the produced image so the sprite pipeline can request the true palette + clean frames (no lossy webp).
    # The `result` tuple still flows the upscaled RGB downstream to SaveAnimatedWEBP, so the normal output is unchanged.
    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "grid_w": ("INT", {"default": 384, "min": 8, "max": 4096}),
                "grid_h": ("INT", {"default": 256, "min": 8, "max": 4096}),
                "thicken": ("FLOAT", {"default": 0.75, "min": 0.0, "max": 8.0, "step": 0.05}),
                "tau": ("FLOAT", {"default": 0.6, "min": 0.0, "max": 2.0, "step": 0.01}),
            },
            "optional": {
                "virtual_resolution": ("INT", {"default": 0, "min": 0, "max": 4096}),
                "lam": ("FLOAT", {"default": 0.015, "min": 0.001, "max": 0.2, "step": 0.001}),
                "k": ("INT", {"default": 31, "min": 2, "max": 128}),
                "beta": ("FLOAT", {"default": 0.5, "min": 0.0, "max": 4.0, "step": 0.05}),
                "step": ("FLOAT", {"default": 5.6, "min": 1.0, "max": 20.0, "step": 0.1}),
                "xdog_sigma": ("FLOAT", {"default": 1.0, "min": 0.3, "max": 8.0, "step": 0.1}),
                "xdog_k": ("FLOAT", {"default": 1.6, "min": 1.0, "max": 4.0, "step": 0.1}),
                "xdog_tau": ("FLOAT", {"default": 0.98, "min": 0.5, "max": 1.0, "step": 0.01}),
                "xdog_epsilon": ("FLOAT", {"default": 0.0, "min": -1.0, "max": 1.0, "step": 0.01}),
                "xdog_phi": ("FLOAT", {"default": 10.0, "min": 0.1, "max": 50.0, "step": 0.1}),
                "sample_per_frame": ("INT", {"default": 20000, "min": 1000, "max": 200000}),
                # Replay inputs: a previous run's emitted globals, so SINGLE frames can be re-quantized later with
                # results identical to their original whole-batch run (same palette indices, same rarity weighting).
                # Empty = derive from this batch exactly as always. Order is significant: `frequencies` is indexed
                # by `palette` order, so both come from (and must be replayed from) the same emitting run.
                "palette": ("STRING", {"default": "", "multiline": True}),
                "frequencies": ("STRING", {"default": "", "multiline": True}),
            },
        }

    def run(self, image, grid_w, grid_h, thicken=0.75, tau=0.6, virtual_resolution=0,
            lam=0.015, k=31, beta=0.5, step=5.6, xdog_sigma=1.0, xdog_k=1.6, xdog_tau=0.98,
            xdog_epsilon=0.0, xdog_phi=10.0, sample_per_frame=20000, palette="", frequencies=""):
        supplied_palette = None
        pal_spec = (palette or "").strip()
        if pal_spec:
            supplied_palette = quant._parse_hex_list(pal_spec)
            if supplied_palette is None:
                raise ValueError("palette: expected an inline hex list (two or more RRGGBB tokens)")
        supplied_freq = None
        freq_spec = (frequencies or "").strip()
        if freq_spec:
            if supplied_palette is None:
                raise ValueError("frequencies supplied without a palette — the vector is indexed by palette order")
            supplied_freq = np.array([float(t) for t in freq_spec.replace(",", " ").split()], dtype=np.float64)
            if len(supplied_freq) != len(supplied_palette):
                raise ValueError(f"frequencies length {len(supplied_freq)} != palette length {len(supplied_palette)}")

        if image.ndim == 5:                       # (B,T,H,W,3) video decode -> flatten frames
            image = image.reshape(-1, *image.shape[2:])
        batch = _img_to_u8(image)
        n, H, W = batch.shape[0], batch.shape[1], batch.shape[2]
        # Keyed input (RGBA): RGB is the subject, A the matte. Alpha is carried as a separate per-frame
        # mask — never through the 3-channel DIN99d colour math — and gates every image statistic
        # (palette pool, global frequency, per-cell vote) to opaque pixels so the transparent background
        # can't pollute the palette or win a boundary cell. It is reattached as 1-bit grid alpha at the end.
        has_alpha = batch.shape[-1] == 4
        opaque_all = [batch[i][..., 3] >= 128 for i in range(n)] if has_alpha else [None] * n
        gw, gh = quant.grid_for_aspect(W, H, grid_w, grid_h, virtual_resolution)
        rng = np.random.RandomState(0)

        # pass 1: preprocess every frame; pool colours only when deriving ONE palette from this batch
        collapsed, pool = [], []
        for i in range(n):
            # RGB is the subject on its original (white) background — identical to what the pre-keying
            # flatten-on-white fed these stages, so they run on it unchanged. Alpha is carried separately.
            imgf = batch[i][..., :3].astype(np.float64) / 255.0
            flat = l0_smooth(imgf, lam=lam)
            thick = (line_thicken_xdog(flat, thickness=thicken, sigma=xdog_sigma, k=xdog_k,
                                       tau=xdog_tau, epsilon=xdog_epsilon, phi=xdog_phi)
                     if thicken > 0 else flat)
            col = collapse_edges(thick, tau=tau) if tau > 0 else thick
            cu8 = (col * 255 + 0.5).astype(np.uint8)
            collapsed.append(cu8)
            if supplied_palette is None:
                px = cu8.reshape(-1, 3)
                if has_alpha:
                    px = px[opaque_all[i].reshape(-1)]      # subject pixels only
                if len(px):
                    pool.append(px[rng.choice(len(px), min(len(px), sample_per_frame), replace=False)])
        if supplied_palette is None:
            master = master_lattice(step)
            palette = palette_from_pixels(np.concatenate(pool).astype(float), master, k=k)
        else:
            palette = supplied_palette

        # pass 2a: snap every frame to the fixed palette; accumulate global frequencies unless replaying
        labels_all = []
        total = np.zeros(len(palette), np.float64)
        for idx, cu8 in enumerate(collapsed):
            lab = snap_labels(cu8.reshape(-1, 3), palette).reshape(H, W)
            labels_all.append(lab)
            if supplied_freq is None:
                counted = lab[opaque_all[idx]] if has_alpha else lab.ravel()  # transparent pixels don't count
                total += np.bincount(counted.ravel(), minlength=len(palette))
        if supplied_freq is None:
            total /= total.sum()
            freq = total
        else:
            freq = supplied_freq
        rarity = (1.0 / (freq + 1e-9)) ** beta

        # pass 2b: downsample each frame -> native-res lossless sprite grid, then nearest-upscale back to source size.
        # The native `sprite` (gw x gh) is the true lossless pixel-art frame (one pixel = one game pixel); the upscale
        # is only for the downstream (lossy) webp. We keep both: sprites for persistence, full for the normal output.
        out, sprites = [], []
        for idx, lab in enumerate(labels_all):
            sprite, _, a_grid = weighted_mode_downsample(lab, palette, gw, gh, rarity=rarity,
                                                         opaque=opaque_all[idx])
            if a_grid is not None:
                sprite = np.dstack([sprite, a_grid])        # (gh, gw, 4) RGBA sprite
            sprites.append(sprite)
            full = np.asarray(Image.fromarray(sprite).resize((W, H), Image.NEAREST), np.uint8)
            out.append(full)
        ui = self._emit_sprite_data(sprites, palette, freq)
        return {"ui": ui, "result": (_u8_to_img(np.stack(out, axis=0)),)}

    @staticmethod
    def _emit_sprite_data(sprites, palette, freq):
        """Persist the derived palette + native-res lossless frames into ComfyUI's output dir and return a `ui` dict
        Forge reads from /history: palette inline (small), frames as saved-PNG refs fetched via /view. `frequencies`
        (indexed by palette order) rides along so a later run can replay BOTH globals and reproduce this run's
        rarity weighting exactly on a single frame."""
        pal = ["#{:02X}{:02X}{:02X}".format(int(r), int(g), int(b)) for r, g, b in palette]
        sub = "pixelharness"
        out_dir = os.path.join(folder_paths.get_output_directory(), sub)
        os.makedirs(out_dir, exist_ok=True)
        stem = "sprite_" + uuid.uuid4().hex[:12]
        refs = []
        for i, sprite in enumerate(sprites):
            fname = f"{stem}_{i:03d}.png"
            mode = "RGBA" if sprite.shape[-1] == 4 else "RGB"   # keyed clips persist their matte
            Image.fromarray(sprite.astype(np.uint8), mode=mode).save(os.path.join(out_dir, fname))
            refs.append({"filename": fname, "subfolder": sub, "type": "output"})
        return {"palette": pal, "frequencies": [float(x) for x in freq], "lossless_frames": refs}


NODE_CLASS_MAPPINGS = {
    "PixelQuantizeFP": PixelQuantizeFP,
}
