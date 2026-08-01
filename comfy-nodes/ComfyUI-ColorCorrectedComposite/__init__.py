"""
ComfyUI-ColorCorrectedComposite — ImageCompositeMasked with a measured color correction.

An inpaint/outpaint graph that pastes the ORIGINAL pixels back over everything outside
the mask exposes any exposure/tint drift in the generated region as a hard seam at the
mask boundary. When the sampler regenerates the WHOLE frame from inpaint conditioning
(reference FLUX.1 Fill behaviour — no latent noise mask), the pixels outside the mask
are the model's reproduction of known content, so the drift is directly measurable
there: fit source→destination on those pixels, apply the fit to the whole source, then
composite. The correction is computed per image from thousands of pixel pairs — there
are no tuned constants.

correction_method (fit computed on pixels where mask < 0.0001):
  None    — plain ImageCompositeMasked behaviour.
  Uniform — mean HSV offset (hue untouched).
  Linear  — per-channel linear regression (m*x+b) on HSV S and V.
  Linear2 — like Linear but fits S*V and V, then derives S; stable when dark pixels
            would make a raw S fit blow up. The method SwarmUI ships as its best.

Adapted from SwarmUI (MIT), src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/
SwarmComfyCommon/SwarmImages.py (SwarmImageCompositeMaskedColorCorrecting); HSV
conversions from https://github.com/limacv/RGB_HSV_HSL.
"""
import torch
import comfy.utils
from nodes import MAX_RESOLUTION


def _rgb2hsv(rgb: torch.Tensor) -> torch.Tensor:
    cmax, cmax_idx = torch.max(rgb, dim=1, keepdim=True)
    cmin = torch.min(rgb, dim=1, keepdim=True)[0]
    delta = cmax - cmin
    hsv_h = torch.empty_like(rgb[:, 0:1, :, :])
    cmax_idx[delta == 0] = 3
    hsv_h[cmax_idx == 0] = (((rgb[:, 1:2] - rgb[:, 2:3]) / delta) % 6)[cmax_idx == 0]
    hsv_h[cmax_idx == 1] = (((rgb[:, 2:3] - rgb[:, 0:1]) / delta) + 2)[cmax_idx == 1]
    hsv_h[cmax_idx == 2] = (((rgb[:, 0:1] - rgb[:, 1:2]) / delta) + 4)[cmax_idx == 2]
    hsv_h[cmax_idx == 3] = 0.0
    hsv_h /= 6.0
    hsv_s = torch.where(cmax == 0, torch.tensor(0.0).type_as(rgb), delta / cmax)
    return torch.cat([hsv_h, hsv_s, cmax], dim=1)


def _hsv2rgb(hsv: torch.Tensor) -> torch.Tensor:
    hsv_h, hsv_s, hsv_v = hsv[:, 0:1], hsv[:, 1:2], hsv[:, 2:3]
    _c = hsv_v * hsv_s
    _x = _c * (-torch.abs(hsv_h * 6.0 % 2.0 - 1) + 1.0)
    _m = hsv_v - _c
    _o = torch.zeros_like(_c)
    idx = (hsv_h * 6.0).type(torch.uint8)
    idx = (idx % 6).expand(-1, 3, -1, -1)
    rgb = torch.empty_like(hsv)
    rgb[idx == 0] = torch.cat([_c, _x, _o], dim=1)[idx == 0]
    rgb[idx == 1] = torch.cat([_x, _c, _o], dim=1)[idx == 1]
    rgb[idx == 2] = torch.cat([_o, _c, _x], dim=1)[idx == 2]
    rgb[idx == 3] = torch.cat([_o, _x, _c], dim=1)[idx == 3]
    rgb[idx == 4] = torch.cat([_x, _o, _c], dim=1)[idx == 4]
    rgb[idx == 5] = torch.cat([_c, _o, _x], dim=1)[idx == 5]
    return rgb + _m


def _linear_fit(source_component: torch.Tensor, dest_component: torch.Tensor, thresholded: torch.Tensor) -> torch.Tensor:
    thresholded_sum = thresholded.sum()
    source_mean = (source_component * thresholded).sum(dim=[0, 2, 3]) / thresholded_sum
    dest_mean = (dest_component * thresholded).sum(dim=[0, 2, 3]) / thresholded_sum
    source_mean = source_mean.reshape(1, -1, 1, 1)
    dest_mean = dest_mean.reshape(1, -1, 1, 1)
    source_deviation = (source_component - source_mean) * thresholded
    dest_deviation = (dest_component - dest_mean) * thresholded
    numerator = torch.sum(source_deviation * dest_deviation, (0, 2, 3))
    denominator = torch.sum(source_deviation * source_deviation, (0, 2, 3))
    # All-same-color source region: fall back to a pure offset (m = 1).
    m = torch.where(denominator != 0, numerator / denominator, torch.ones_like(numerator))
    m = m.reshape(1, -1, 1, 1)
    b = dest_mean - source_mean * m
    return (m * source_component + b).clamp(0, 1)


def _correct_uniform(source: torch.Tensor, dest: torch.Tensor, thresholded: torch.Tensor) -> torch.Tensor:
    thresholded_sum = thresholded.sum()
    source_hsv = _rgb2hsv(source)
    dest_hsv = _rgb2hsv(dest)
    diff = ((dest_hsv - source_hsv) * thresholded).sum(dim=[0, 2, 3]) / thresholded_sum
    diff[0] = 0.0
    return _hsv2rgb((source_hsv + diff.reshape(1, -1, 1, 1)).clamp(0, 1))


def _correct_linear(source: torch.Tensor, dest: torch.Tensor, thresholded: torch.Tensor) -> torch.Tensor:
    source_hsv = _rgb2hsv(source)
    dest_hsv = _rgb2hsv(dest)
    s = _linear_fit(source_hsv[:, 1:2], dest_hsv[:, 1:2], thresholded)
    v = _linear_fit(source_hsv[:, 2:3], dest_hsv[:, 2:3], thresholded)
    return _hsv2rgb(torch.cat([source_hsv[:, 0:1], s, v], dim=1))


def _correct_linear2(source: torch.Tensor, dest: torch.Tensor, thresholded: torch.Tensor) -> torch.Tensor:
    source_hsv = _rgb2hsv(source)
    dest_hsv = _rgb2hsv(dest)
    sv = _linear_fit(source_hsv[:, 1:2] * source_hsv[:, 2:3], dest_hsv[:, 1:2] * dest_hsv[:, 2:3], thresholded)
    v = _linear_fit(source_hsv[:, 2:3], dest_hsv[:, 2:3], thresholded)
    s = torch.zeros_like(sv)
    s[v != 0] = (sv[v != 0] / v[v != 0])
    return _hsv2rgb(torch.cat([source_hsv[:, 0:1], s.clamp(0, 1), v], dim=1))


_METHODS = {"Uniform": _correct_uniform, "Linear": _correct_linear, "Linear2": _correct_linear2}


class ImageCompositeMaskedColorCorrected:
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "destination": ("IMAGE",),
                "source": ("IMAGE",),
                "x": ("INT", {"default": 0, "min": 0, "max": MAX_RESOLUTION, "step": 1}),
                "y": ("INT", {"default": 0, "min": 0, "max": MAX_RESOLUTION, "step": 1}),
                "mask": ("MASK",),
                "correction_method": (["None", "Uniform", "Linear", "Linear2"], {"default": "Linear2"}),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "composite"
    CATEGORY = "image"
    TITLE = "Image Composite Masked (color corrected)"
    DESCRIPTION = ("ImageCompositeMasked that first fits a color correction on the pixels outside the mask "
                   "(where source and destination should agree) and applies it to the source. For inpaint "
                   "paste-back where the generated frame drifts in exposure/tint.")

    def composite(self, destination, source, x, y, mask, correction_method):
        destination = destination.clone().movedim(-1, 1)
        source = source.clone().movedim(-1, 1).to(destination.device)
        source = comfy.utils.repeat_to_batch_size(source, destination.shape[0])

        x = max(-source.shape[3], min(x, destination.shape[3]))
        y = max(-source.shape[2], min(y, destination.shape[2]))
        left, top = x, y
        right, bottom = left + source.shape[3], top + source.shape[2]

        mask = mask.to(destination.device, copy=True)
        mask = torch.nn.functional.interpolate(
            mask.reshape((-1, 1, mask.shape[-2], mask.shape[-1])),
            size=(source.shape[2], source.shape[3]), mode="bilinear")
        mask = comfy.utils.repeat_to_batch_size(mask, source.shape[0])

        visible_width = destination.shape[3] - left + min(0, x)
        visible_height = destination.shape[2] - top + min(0, y)
        mask = mask[:, :, :visible_height, :visible_width]
        inverse_mask = torch.ones_like(mask) - mask

        source_section = source[:, :, :visible_height, :visible_width]
        dest_section = destination[:, :, top:bottom, left:right]

        if correction_method in _METHODS:
            # Fit only on FULLY-outside pixels (mask < 0.0001): inside is generated (no truth),
            # and band pixels are blends. Skip when the mask leaves too few pixels to fit on.
            thresholded = ((inverse_mask.clamp(0, 1) - 0.9999).clamp(0, 1)) * 10000
            if thresholded.sum() > 50:
                source_section = _METHODS[correction_method](source_section, dest_section, thresholded)

        destination[:, :, top:bottom, left:right] = mask * source_section + inverse_mask * dest_section
        return (destination.movedim(1, -1),)


NODE_CLASS_MAPPINGS = {"ImageCompositeMaskedColorCorrected": ImageCompositeMaskedColorCorrected}
NODE_DISPLAY_NAME_MAPPINGS = {"ImageCompositeMaskedColorCorrected": "Image Composite Masked (color corrected)"}
__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
