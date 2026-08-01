"""DeflickerAuto — automatic flicker/wash detection + correction for AI sprite clips (PixelHarness).

Faithful port of the standalone deflicker_auto.py, operating on a whole clip (image batch):
  1. Matte every frame with BiRefNet (the pipeline's keyer; reused from birefnet_matte). The matte is
     used for two different jobs with two different robustness treatments, because BiRefNet's alpha
     VALUES are unstable on exactly the frames that need fixing (a washed, low-contrast frame mattes
     softer, and even decoder rounding — ffmpeg vs LoadVideo differ by ~1 sub-level — moves them),
     while WHERE the matte says the character is barely moves:
       - pixel-set selection (stats + CDF fit): alpha renormalized by its own interior high-quantile
         (P95 of the support), so the alpha_cut membership doesn't shift with matte confidence;
       - the final composite: weights come from the matte's GEOMETRY only — binary mask at alpha_cut,
         feathered by a small fixed gaussian — never from alpha values. Using values as blend weights
         scales the correction by the model's confidence, i.e. blends the defect back into the very
         frames being corrected, by an amount that varies with how the clip was decoded.
  2. Per-frame character statistics over matte>alpha_cut, chosen to be invariant to pose and to slow
     whole-clip drift:
       - luma P5 / P15  (the character's own darkest content, whatever it is)
       - chroma P90     (max-min of RGB: the character's own most saturated content, whatever it is;
                         a wash is a chroma collapse and craters this while pose and lightness drift
                         barely move it)
  3. Detect per statistic series against the WHOLE clip: baseline = global median, threshold =
     mad_k * MAD of the whole series (a few bad frames cannot move either), absolute floor min_dev
     8-bit levels. Flag frames whose deviation exceeds the threshold. Nothing flagged -> untouched.
  4. Correct each flagged frame by per-channel CDF matching of its character pixels to the character
     pixels of ALL clean frames, pooled with temporal-proximity gaussian weights (sigma time_sigma):
     every clean frame contributes, but the reference tracks the clip's legitimate drift. Correction
     iterates matte->fit->correct to a FIXED POINT (coverage change <1%, max 5 rounds): the defective
     frame's own first matte is unstable and under-covers, and a LUT fitted from a partial matte only
     partially restores — each round's restored frame mattes better, converging on the fully restored
     frame whose matte has clean-frame quality regardless of decode. The final composite uses that
     matte's geometry (see step 1).
Only flagged frames are rewritten; clean frames pass through bit-exact.
"""
import numpy as np
import torch
from PIL import Image, ImageFilter

from .birefnet_matte import _load, _MEAN, _STD

LUMA = np.array([0.299, 0.587, 0.114])
QGRID = (np.arange(4096) + 0.5) / 4096


def _matte(img01):
    """BiRefNet foreground alpha (HxW, 0..1) for one HxWx3 float(0..1) frame — the pipeline's keyer."""
    model, dev, dtype = _load()
    pil = Image.fromarray((img01 * 255.0 + 0.5).astype(np.uint8))
    rs = pil.resize((1024, 1024), Image.BILINEAR)
    t = torch.from_numpy(np.asarray(rs).astype(np.float32) / 255.0).permute(2, 0, 1)
    t = ((t - _MEAN) / _STD).unsqueeze(0).to(dev).to(dtype)
    with torch.no_grad():
        pred = model(t)
        pred = pred[-1] if isinstance(pred, (list, tuple)) else pred
        a = pred.sigmoid().float().cpu()[0, 0]
    a = Image.fromarray((a.numpy() * 255.0).astype(np.uint8)).resize(pil.size, Image.BILINEAR)
    return np.asarray(a).astype(np.float64) / 255.0


def _normalize_alpha(a):
    """Scale a frame's matte by its own interior confidence (P95 over the matte's support) so the
    alpha_cut pixel selection doesn't shift with the matte's absolute confidence level."""
    support = a[a > 0.25]
    if support.size == 0:
        return a
    return np.clip(a / max(np.percentile(support, 95), 1e-6), 0.0, 1.0)


# Feather radius (px) for the composite mask — sized to the 1-2px anti-aliased edge of the source
# art, i.e. about edge RENDERING, not content. Not exposed: it only matters that it is small.
_FEATHER_PX = 1.5


def _composite_weights(alpha, cut):
    """Composite weights from the matte's GEOMETRY, not its confidence: binary mask at `cut`,
    gaussian-feathered. Interior pixels get the correction at full strength regardless of how
    confident (or how decoded) the matte is; only the boundary blends."""
    m = Image.fromarray(np.where(alpha > cut, 255, 0).astype(np.uint8))
    return np.asarray(m.filter(ImageFilter.GaussianBlur(_FEATHER_PX))).astype(np.float64) / 255.0


def _weighted_quantile_fn(vals, wts):
    o = np.argsort(vals)
    v, w = vals[o], wts[o]
    cw = np.cumsum(w)
    cw = (cw - 0.5 * w) / cw[-1]
    return np.interp(QGRID, cw, v)


class DeflickerAuto:
    TITLE = "Deflicker Auto (BiRefNet + drift-aware histmatch)"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("images",)

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {"image": ("IMAGE",)},
            "optional": {
                "mad_k": ("FLOAT", {"default": 4.0, "min": 0.5, "max": 20.0, "step": 0.1}),
                "min_dev": ("FLOAT", {"default": 1.0, "min": 0.0, "max": 16.0, "step": 0.1}),
                "alpha_cut": ("FLOAT", {"default": 0.5, "min": 0.0, "max": 1.0, "step": 0.01}),
                "time_sigma": ("FLOAT", {"default": 3.0, "min": 0.1, "max": 32.0, "step": 0.1}),
            },
        }

    def run(self, image, mad_k=4.0, min_dev=1.0, alpha_cut=0.5, time_sigma=3.0):
        if image.ndim == 5:                       # (B,T,H,W,3) video decode -> flatten frames
            image = image.reshape(-1, *image.shape[2:])
        n = image.shape[0]
        imgs = [image[i].cpu().numpy().astype(np.float64) * 255.0 for i in range(n)]
        alphas = [_normalize_alpha(_matte(image[i].cpu().numpy())) for i in range(n)]
        char_px = [imgs[i][alphas[i] > alpha_cut] for i in range(n)]

        # ---- stats ----
        stats = np.empty((n, 3))
        for i in range(n):
            y = char_px[i] @ LUMA
            chroma = char_px[i].max(axis=1) - char_px[i].min(axis=1)
            stats[i] = [np.percentile(y, 5), np.percentile(y, 15), np.percentile(chroma, 90)]

        # ---- detect (whole-clip median +/- mad_k*MAD, floor min_dev) ----
        SERIES = ["luma_p5", "luma_p15", "chroma_p90"]
        flags = np.zeros(n, dtype=bool)
        report = {t: [] for t in range(n)}
        for s in range(3):
            x = stats[:, s]
            med = np.median(x)
            mad = np.median(np.abs(x - med))
            thresh = max(mad_k * mad, min_dev)
            for t in range(n):
                dev = abs(x[t] - med)
                if dev > thresh:
                    flags[t] = True
                    report[t].append(f"{SERIES[s]}={x[t]:.2f} vs med {med:.2f} (dev {dev:.2f} > {thresh:.2f})")
        flagged = [t for t in range(n) if flags[t]]
        clean = [i for i in range(n) if not flags[i]]
        print(f"[DeflickerAuto] flagged {len(flagged)}/{n} frames", flush=True)
        for t in flagged:
            print(f"[DeflickerAuto]   frame {t}: " + "; ".join(report[t]), flush=True)
        if not flagged or not clean:
            print("[DeflickerAuto] nothing to correct — clip passes through untouched", flush=True)
            return (image,)                       # nothing to fix / no clean reference -> untouched

        # ---- correct (drift-weighted per-channel CDF match to the clean pool) ----
        def _cdf_correct(frame, alpha, qref3):
            """Apply the per-channel CDF LUT (source distribution -> reference quantiles) full-frame.
            The source CDF is ALPHA-WEIGHTED, not membership-cut: ambiguous edge pixels contribute in
            proportion to their matte confidence, so the fit can't be swung by which side of a binary
            cutoff they land on (BiRefNet is multi-stable on washed frames — binary fit sets differ by
            thousands of edge pixels between equally-plausible mattes, and rank maps amplify that into
            visible tone shifts). Background self-excludes at alpha~0; no threshold enters the fit.
            Full-frame application is safe: white maps onto the reference pool's own near-white top."""
            corr = np.empty_like(frame)
            w = alpha.ravel()
            for c in range(3):
                hist = np.bincount(frame[..., c].astype(np.intp).ravel(), weights=w, minlength=256)
                cdf = np.cumsum(hist)
                q = (cdf - 0.5 * hist) / max(cdf[-1], 1e-9)
                corr[..., c] = np.interp(q, QGRID, qref3[c])[frame[..., c].astype(np.intp)]
            return corr

        out = image.clone()
        for t in flagged:
            fw = {i: np.exp(-((i - t) ** 2) / (2 * time_sigma ** 2)) for i in clean}
            vals = np.concatenate([char_px[i] for i in clean], axis=0)
            wts = np.concatenate([np.full(len(char_px[i]), fw[i]) for i in clean])
            qref3 = [_weighted_quantile_fn(vals[:, c], wts) for c in range(3)]

            # FIXED-POINT MATTE ITERATION: the defective frame's own matte is unreliable — BiRefNet
            # is unstable and under-covers on exactly the low-contrast frames that get flagged, and
            # a LUT fitted from a partial matte only partially restores the frame. So iterate:
            # correct with the current alpha weighting (full-frame — background maps white->white),
            # re-matte the contrast-restored result (an in-distribution matting input), refit from
            # the new matte, until the matte's coverage stops changing (<1%). The fixed point is the
            # fully restored frame, whose matte has clean-frame quality regardless of how the source
            # clip was decoded.
            alpha_t, cov = alphas[t], -1
            for _ in range(5):
                corr = _cdf_correct(imgs[t], alpha_t, qref3)
                alpha_t = _normalize_alpha(_matte(np.clip(corr, 0, 255) / 255.0))
                new_cov = int((alpha_t > alpha_cut).sum())
                if cov >= 0 and abs(new_cov - cov) <= 0.01 * max(cov, 1):
                    cov = new_cov
                    break
                cov = new_cov
            corr = _cdf_correct(imgs[t], alpha_t, qref3)

            a = _composite_weights(alpha_t, alpha_cut)[..., None]
            fixed = np.clip(np.rint(a * corr + (1 - a) * imgs[t]), 0, 255) / 255.0
            out[t] = torch.from_numpy(fixed.astype(np.float32))
            print(f"[DeflickerAuto]   corrected frame {t} against the weighted clean pool "
                  f"(matte fixed-point: {cov} char px)", flush=True)
        return (out,)


NODE_CLASS_MAPPINGS = {"DeflickerAuto": DeflickerAuto}
NODE_DISPLAY_NAME_MAPPINGS = {"DeflickerAuto": "Deflicker Auto (BiRefNet + drift-aware histmatch)"}
