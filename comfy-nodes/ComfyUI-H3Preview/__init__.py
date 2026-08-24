"""Whole-shot animated PNG previews for MiniMax H3 sampling."""

from __future__ import annotations

import io
import logging
import struct

import torch
import torch.nn.functional as F
from PIL import Image

import comfy.patcher_extension
import comfy.utils
import latent_preview
import server
from protocol import BinaryEventTypes


log = logging.getLogger(__name__)

_MAX_PREVIEW_FRAMES = 24
_MAX_PREVIEW_EDGE = 384
_PNG_FORMAT_NUMBER = 2
_WRAPPER_KEY = "imagegen_h3_animated_preview"


def _video_stream(x0, latent_shapes=None):
    """Return H3's [B,C,T,H,W] video stream from the sampler's packed AV latent."""
    if x0 is None:
        return None
    if getattr(x0, "is_nested", False):
        return x0.tensors[0]
    if x0.ndim == 5:
        return x0
    if latent_shapes and len(latent_shapes) > 1:
        return comfy.utils.unpack_latents(x0, list(latent_shapes))[0]
    return None


def _pick_frames(video, maximum):
    count = int(video.shape[2])
    if count <= maximum:
        return video
    indexes = torch.linspace(
        0, count - 1, maximum, device=video.device
    ).round().long().unique()
    return video[:, :, indexes]


def _pixel_frame_count(latent_frames):
    """Invert H3's 17k+5 pixel-frame -> 5k+2 latent-frame cadence."""
    if latent_frames <= 2:
        return 5
    quotient, remainder = divmod(int(latent_frames) - 2, 5)
    if remainder == 0:
        return 17 * quotient + 5
    return max(1, int(round(latent_frames * 17.0 / 5.0)))


def _latent_rgb(video, latent_format):
    factors = getattr(latent_format, "latent_rgb_factors", None)
    if factors is None:
        raise ValueError("H3's latent format does not expose latent_rgb_factors")

    weight = torch.tensor(factors, device=video.device, dtype=torch.float32).transpose(0, 1)
    bias_values = getattr(latent_format, "latent_rgb_factors_bias", None)
    bias = (
        torch.tensor(bias_values, device=video.device, dtype=torch.float32)
        if bias_values is not None
        else None
    )

    # [B,C,T,H,W] -> [B*T,H,W,C], then ComfyUI's usual latent-to-RGB projection.
    moved = video.movedim(2, 1)
    channels = int(weight.shape[1])
    flattened = moved.reshape((-1,) + tuple(moved.shape[-3:]))[:, :channels].float()
    return ((F.linear(flattened.movedim(1, -1), weight, bias) + 1.0) / 2.0).clamp(0, 1)


def _pil_frames(images):
    frames = []
    for frame in images:
        array = (frame * 255.0).to(torch.uint8).cpu().numpy()
        image = Image.fromarray(array, mode="RGB")
        longest = max(image.size)
        if longest > 0 and longest != _MAX_PREVIEW_EDGE:
            scale = _MAX_PREVIEW_EDGE / float(longest)
            size = (
                max(1, int(round(image.width * scale))),
                max(1, int(round(image.height * scale))),
            )
            resample = Image.Resampling.BICUBIC if scale > 1.0 else Image.Resampling.LANCZOS
            image = image.resize(size, resample)
        frames.append(image)
    return frames


def _encode_apng(frames, duration_ms):
    if not frames:
        return None
    output = io.BytesIO()
    frames[0].save(
        output,
        format="PNG",
        save_all=True,
        append_images=frames[1:],
        duration=max(1, int(round(duration_ms))),
        loop=0,
        disposal=2,
        blend=0,
        optimize=False,
        compress_level=1,
    )
    return output.getvalue()


def _send_png(png_bytes):
    # PromptServer adds the outer PREVIEW_IMAGE event header. The inner uint32 says PNG,
    # yielding the exact [event=1][format=2][PNG bytes] envelope the app already consumes.
    prompt_server = server.PromptServer.instance
    payload = struct.pack(">I", _PNG_FORMAT_NUMBER) + png_bytes
    prompt_server.send_sync(
        BinaryEventTypes.PREVIEW_IMAGE,
        payload,
        prompt_server.client_id,
    )


def _suppressed_preview_image(*_args, **_kwargs):
    return None


class _H3PreviewWrapper:
    def __init__(self, preview_every, fps):
        self.preview_every = int(preview_every)
        self.fps = max(0.1, float(fps))

    def __call__(
        self,
        executor,
        noise,
        latent_image,
        sampler,
        sigmas,
        denoise_mask,
        callback,
        disable_pbar,
        seed,
        **kwargs,
    ):
        guider = executor.class_obj
        latent_format = guider.model_patcher.model.latent_format
        latent_shapes = kwargs.get("latent_shapes")
        warned = False

        # prepare_callback has already captured a previewer by the time OUTER_SAMPLE runs.
        # Suppress every concrete implementation during this sampler while preserving the
        # callback itself: it still owns progress reporting and SamplerCustomAdvanced's x0.
        previous_methods = []
        targets = [latent_preview.LatentPreviewer]
        subclasses = list(latent_preview.LatentPreviewer.__subclasses__())
        while subclasses:
            target = subclasses.pop()
            targets.append(target)
            subclasses.extend(target.__subclasses__())
        for target in targets:
            if "decode_latent_to_preview_image" in target.__dict__:
                previous_methods.append(
                    (target, target.__dict__["decode_latent_to_preview_image"])
                )
                target.decode_latent_to_preview_image = _suppressed_preview_image

        def combined(step, x0, x, total_steps):
            nonlocal warned
            if callback is not None:
                callback(step, x0, x, total_steps)

            completed_step = int(step) + 1
            if completed_step % self.preview_every != 0:
                return

            try:
                video = _video_stream(x0, latent_shapes)
                if video is None or video.ndim != 5:
                    return
                pixel_frames = _pixel_frame_count(int(video.shape[2]))
                selected = _pick_frames(video, _MAX_PREVIEW_FRAMES)
                frames = _pil_frames(_latent_rgb(selected, latent_format))
                # Evenly sampled preview frames span the complete final shot duration.
                duration_ms = 1000.0 * pixel_frames / (self.fps * max(1, len(frames)))
                png = _encode_apng(frames, duration_ms)
                if png is not None:
                    _send_png(png)
            except Exception as error:  # A preview must never take the generation down.
                if not warned:
                    warned = True
                    log.warning(
                        "H3 animated preview failed; generation will continue without it: %r",
                        error,
                        exc_info=True,
                    )

        try:
            return executor(
                noise,
                latent_image,
                sampler,
                sigmas,
                denoise_mask,
                combined,
                disable_pbar,
                seed,
                **kwargs,
            )
        finally:
            for target, previous in previous_methods:
                target.decode_latent_to_preview_image = previous


class H3AnimatedPreview:
    """Attach a lightweight whole-shot APNG preview to an H3 MODEL."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "model": ("MODEL",),
                "preview_every": (
                    "INT",
                    {
                        "default": 4,
                        "min": 0,
                        "max": 100,
                        "step": 1,
                        "tooltip": "Sampler-step interval; 0 disables animated previews.",
                    },
                ),
                "fps": (
                    "FLOAT",
                    {"default": 24.0, "min": 0.1, "max": 120.0, "step": 0.1},
                ),
            }
        }

    RETURN_TYPES = ("MODEL",)
    FUNCTION = "patch"
    CATEGORY = "model_patches/minimax_h3"

    def patch(self, model, preview_every=4, fps=24.0):
        interval = int(preview_every)
        if interval <= 0:
            return (model,)

        patched = model.clone()
        wrapper = _H3PreviewWrapper(interval, fps)
        comfy.patcher_extension.add_wrapper_with_key(
            comfy.patcher_extension.WrappersMP.OUTER_SAMPLE,
            _WRAPPER_KEY,
            wrapper,
            patched.model_options,
            is_model_options=True,
        )

        # Compatibility with revisions that still source OUTER_SAMPLE wrappers from
        # ModelPatcher rather than model_options. Avoid double-registration when not needed.
        registered = comfy.patcher_extension.get_all_wrappers(
            comfy.patcher_extension.WrappersMP.OUTER_SAMPLE,
            patched.model_options,
            is_model_options=True,
        )
        if wrapper not in registered and hasattr(patched, "add_wrapper_with_key"):
            patched.add_wrapper_with_key(
                comfy.patcher_extension.WrappersMP.OUTER_SAMPLE,
                _WRAPPER_KEY,
                wrapper,
            )
        return (patched,)


NODE_CLASS_MAPPINGS = {"H3AnimatedPreview": H3AnimatedPreview}
NODE_DISPLAY_NAME_MAPPINGS = {
    "H3AnimatedPreview": "MiniMax H3 Animated Preview",
}
