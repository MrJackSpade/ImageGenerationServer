"""sketchKeras line extractor as a self-contained node.

The SketchKeras network and its highpass/threshold pipeline are adapted from higumax's
sketchKeras-pytorch (vendored under vendor/sketchKeras-pytorch, MIT), itself a port of
lllyasviel/sketchKeras. The model is fixed-size 512x512: it takes the 3 colour channels as a
batch of single-channel images and max-reduces the per-channel sketch responses. Output here is
dark-lines-on-white at the INPUT resolution (resized back internally), ready to thicken + multiply.

Weights: vendor/sketchKeras-pytorch/weights/model.pth (downloaded separately).
"""
from __future__ import annotations

import os
import numpy as np
import torch
import torch.nn as nn
import cv2

_WEIGHTS = os.path.join(os.path.dirname(__file__), "vendor", "sketchKeras-pytorch", "weights", "model.pth")


class SketchKeras(nn.Module):
    def __init__(self):
        super().__init__()
        def down(cin, cout, k, s):
            return [nn.ReflectionPad2d((1, 1, 1, 1)), nn.Conv2d(cin, cout, k, s),
                    nn.BatchNorm2d(cout, eps=1e-3, momentum=0), nn.ReLU()]
        self.downblock_1 = nn.Sequential(*down(1, 32, 3, 1))
        self.downblock_2 = nn.Sequential(*down(32, 64, 4, 2), *down(64, 64, 3, 1))
        self.downblock_3 = nn.Sequential(*down(64, 128, 4, 2), *down(128, 128, 3, 1))
        self.downblock_4 = nn.Sequential(*down(128, 256, 4, 2), *down(256, 256, 3, 1))
        self.downblock_5 = nn.Sequential(*down(256, 512, 4, 2))
        self.downblock_6 = nn.Sequential(*down(512, 512, 3, 1))

        def up(size, cin, cmid, cout):
            return [nn.Upsample(size), nn.ReflectionPad2d((1, 2, 1, 2)), nn.Conv2d(cin, cmid, 4, 1),
                    nn.BatchNorm2d(cmid, eps=1e-3, momentum=0), nn.ReLU(),
                    nn.ReflectionPad2d((1, 1, 1, 1)), nn.Conv2d(cmid, cout, 3, 1),
                    nn.BatchNorm2d(cout, eps=1e-3, momentum=0), nn.ReLU()]
        self.upblock_1 = nn.Sequential(*up((64, 64), 1024, 512, 256))
        self.upblock_2 = nn.Sequential(*up((128, 128), 512, 256, 128))
        self.upblock_3 = nn.Sequential(*up((256, 256), 256, 128, 64))
        self.upblock_4 = nn.Sequential(*up((512, 512), 128, 64, 32))

        self.last_pad = nn.ReflectionPad2d((1, 1, 1, 1))
        self.last_conv = nn.Conv2d(64, 1, 3, 1)

    def forward(self, x):
        d1 = self.downblock_1(x)
        d2 = self.downblock_2(d1)
        d3 = self.downblock_3(d2)
        d4 = self.downblock_4(d3)
        d5 = self.downblock_5(d4)
        d6 = self.downblock_6(d5)
        u1 = self.upblock_1(torch.cat((d5, d6), dim=1))
        u2 = self.upblock_2(torch.cat((d4, u1), dim=1))
        u3 = self.upblock_3(torch.cat((d3, u2), dim=1))
        u4 = self.upblock_4(torch.cat((d2, u3), dim=1))
        u5 = torch.cat((d1, u4), dim=1)
        return self.last_conv(self.last_pad(u5))


_MODEL = None


def _device():
    try:
        import comfy.model_management as mm
        return mm.get_torch_device()
    except Exception:
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")


def _get_model():
    global _MODEL
    if _MODEL is None:
        if not os.path.exists(_WEIGHTS):
            raise FileNotFoundError(f"sketchKeras weights missing: {_WEIGHTS}")
        m = SketchKeras()
        m.load_state_dict(torch.load(_WEIGHTS, map_location="cpu"))
        m.eval()
        _MODEL = m
    return _MODEL


def _sketch_one(rgb_u8: np.ndarray, thresh: float) -> np.ndarray:
    """rgb_u8 (H,W,3) -> dark-lines-on-white (H,W) uint8 at the same H,W."""
    H, W = rgb_u8.shape[:2]
    # resize longest edge to 512 (aspect kept), as the fixed-size net requires
    if W > H:
        nw, nh = 512, max(1, int(round(512 / W * H)))
    else:
        nw, nh = max(1, int(round(512 / H * W))), 512
    small = cv2.resize(rgb_u8, (nw, nh))
    # highpass = image - gaussian(image), normalised
    blurred = cv2.GaussianBlur(small, (0, 0), 3)
    highpass = small.astype(np.float32) - blurred.astype(np.float32)
    highpass /= 128.0
    m = float(np.max(highpass))
    if m > 1e-6:
        highpass /= m
    canvas = np.zeros((512, 512, 3), dtype=np.float32)
    canvas[0:nh, 0:nw, :] = highpass
    # 3 colour channels -> batch of 3 single-channel images
    x = torch.from_numpy(canvas.transpose(2, 0, 1)[:, None, :, :])  # (3,1,512,512)
    dev = _device()
    model = _get_model().to(dev)
    with torch.no_grad():
        pred = model(x.to(dev)).squeeze(1).cpu().numpy()           # (3,512,512)
    line = np.amax(pred, axis=0)                                    # (512,512), high = line
    line[line < thresh] = 0
    out = np.clip((1.0 - line) * 255.0, 0, 255).astype(np.uint8)   # dark lines on white
    out = out[:nh, :nw]
    return cv2.resize(out, (W, H), interpolation=cv2.INTER_AREA)


class SketchKerasLines:
    """sketchKeras line extraction. Outputs the source's lines as dark-on-white (RGB) at input size."""
    TITLE = "SketchKeras Lines"
    CATEGORY = "pixelharness"
    FUNCTION = "run"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("image",)

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "threshold": ("FLOAT", {"default": 0.1, "min": 0.0, "max": 1.0, "step": 0.01}),
            },
        }

    def run(self, image, threshold):
        batch = (image.clamp(0, 1).cpu().numpy() * 255.0 + 0.5).astype(np.uint8)
        out = []
        for i in range(batch.shape[0]):
            line = _sketch_one(batch[i], threshold)
            out.append(np.repeat(line[..., None], 3, axis=2))
        arr = np.stack(out, axis=0).astype(np.float32) / 255.0
        return (torch.from_numpy(arr),)


NODE_CLASS_MAPPINGS = {
    "SketchKerasLines": SketchKerasLines,
}
