"""Palette conversion. Universal DIN99d master lattice (~2000 colors, step 5.6) -> per-image palette
derived from the image's own colors -> two-pass k-means flatten + DIN99d snap, bracketing the
downscale, with a LOCAL final snap (each output pixel must be a color present in its own source cell).
See PIXELIZER_CHECKPOINT.md. Requires: colour-science, scikit-learn, Pillow, numpy.
"""
import warnings; warnings.filterwarnings("ignore")
import colour, numpy as np, sys, os, itertools
from PIL import Image
from sklearn.cluster import KMeans
sys.path.insert(0, os.path.dirname(__file__))
import quant


def Lab(u8): return colour.XYZ_to_Lab(colour.sRGB_to_XYZ(np.asarray(u8, float) / 255.0))
def DIN(u8): return colour.Lab_to_DIN99(Lab(np.clip(u8, 0, 255)), method='DIN99d')
def DIN_to_rgb(d99):
    rgb = colour.XYZ_to_sRGB(colour.Lab_to_XYZ(colour.DIN99_to_Lab(d99, method='DIN99d')))
    return np.round(np.clip(rgb, 0, 1) * 255).astype(np.uint8)


def master_lattice(step=5.6):
    """Universal full color space: a DIN99d cubic lattice over the sRGB gamut (~2000 colors at 5.6)."""
    g = np.linspace(0, 255, 52)
    D = DIN(np.array(list(itertools.product(g, g, g))))
    return np.unique(DIN_to_rgb(np.unique(np.round(D / step) * step, axis=0)), axis=0)


def snapper(palette):
    pd = DIN(palette)
    return lambda arr: palette[((DIN(arr)[:, None, :] - pd[None, :, :]) ** 2).sum(2).argmin(1)]


def kflat(arr, k=31, seed=0):
    rng = np.random.RandomState(seed)
    s = arr[rng.choice(len(arr), min(len(arr), 40000), replace=False)]
    km = KMeans(k, n_init=3, random_state=seed).fit(s)
    return km.cluster_centers_[km.predict(arr)]


def per_image_palette(im_u8, master, k=31):
    """This image's colors, snapped to the master lattice -> sparse per-image palette."""
    flat = np.unique(kflat(im_u8.reshape(-1, 3).astype(float), k), axis=0)
    return np.unique(snapper(master)(flat), axis=0)


def _bounds(L, n): return np.round(np.linspace(0, L, n + 1)).astype(int)


def convert(im_u8, palette, vres=256, k=31, present=0.10):
    H, W, _ = im_u8.shape
    gw, gh = quant.grid_for_aspect(W, H, 0, 0, virtual_resolution=vres)
    snap = snapper(palette)
    stage1 = snap(kflat(im_u8.reshape(-1, 3).astype(float), k)).reshape(H, W, 3).astype(np.uint8)
    xs, ys = _bounds(W, gw), _bounds(H, gh)
    out = np.zeros((gh, gw, 3), np.uint8)
    for gy in range(gh):
        for gx in range(gw):
            reg = stage1[ys[gy]:ys[gy + 1], xs[gx]:xs[gx + 1]].reshape(-1, 3)
            cand, cnt = np.unique(reg, axis=0, return_counts=True)
            cand = cand[cnt >= max(1, int(present * len(reg)))]
            if len(cand) == 0:
                cand = np.unique(reg, axis=0)
            target = DIN(reg.mean(0, keepdims=True))
            out[gy, gx] = cand[((DIN(cand) - target) ** 2).sum(1).argmin()]
    return out, (gw, gh)


if __name__ == "__main__":
    master = master_lattice(step=5.6)          # ~2000 colors; use 7.0 for ~1100
    im = np.asarray(Image.open("vframes/f_040.png").convert("RGB"), np.uint8)
    palette = per_image_palette(im, master)
    out, _ = convert(im, palette)
    Image.fromarray(out).resize((im.shape[1], im.shape[0]), Image.NEAREST).save("out.png")
    print("wrote out.png")
