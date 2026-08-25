"""Whole-shot animated PNG previews for MiniMax H3 sampling."""

from __future__ import annotations

import io
import logging
import queue
import struct
import threading

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
_PREVIEW_STEPS_TYPE = "IMAGEGEN_INT_LIST"


def _normalize_preview_steps(value):
    """Return the unique positive completed-step numbers accepted by the sampler callback."""
    if value is None:
        return frozenset()
    if isinstance(value, (int, float)):
        value = [value]
    if not isinstance(value, (list, tuple, set, frozenset)):
        raise ValueError("preview_steps must be an integer list")

    steps = set()
    for item in value:
        if isinstance(item, bool) or int(item) != item:
            raise ValueError("preview_steps must contain integers")
        step = int(item)
        if step < 1 or step > 100:
            raise ValueError("preview_steps entries must be between 1 and 100")
        steps.add(step)
    return frozenset(steps)


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
        array = frame.numpy()
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


def _stage_cpu(images):
    """Queue a non-blocking GPU->pinned-CPU snapshot and return (buffer, completion event)."""
    # Quantize before transfer: the preview is 8-bit output, so moving float32 would spend 4x the PCIe bandwidth
    # and pinned memory only to discard those extra bits in Pillow.
    images = (images.detach() * 255.0).to(torch.uint8)
    if images.device.type != "cuda":
        return images.to("cpu"), None

    # A plain .cpu() synchronizes the sampler's CUDA stream here. On H3 that can turn preview extraction into
    # minutes of apparent per-step overhead. Pinned memory keeps the copy asynchronous; only the encoder thread
    # waits for it, while sampling is free to continue after the queued transfer.
    with torch.cuda.device(images.device):
        staged = torch.empty_like(images, device="cpu", pin_memory=True)
        staged.copy_(images, non_blocking=True)
        ready = torch.cuda.Event()
        ready.record(torch.cuda.current_stream(images.device))
    return staged, ready


class _PreviewEncoder:
    """Encode off the sampler thread, retaining at most one pending (latest) preview."""

    def __init__(self, fps):
        self.fps = max(0.1, float(fps))
        self.pending = queue.Queue(maxsize=1)
        self.closed = threading.Event()
        self.worker = threading.Thread(
            target=self._run,
            name="h3-apng-preview",
            daemon=True,
        )
        self.worker.start()

    def can_accept(self):
        # When encoding is slower than sampling, one future frame may wait behind the current encode. Further
        # sampler steps skip preview work entirely instead of repeatedly projecting/copying frames nobody will see.
        return not self.closed.is_set() and not self.pending.full()

    def submit(self, images, ready, pixel_frames):
        if self.closed.is_set():
            return
        try:
            self.pending.put_nowait((images, ready, int(pixel_frames)))
        except queue.Full:
            return

    def close(self):
        # Never wait for encoding at render completion. A frame still being encoded is now superseded by the durable
        # output and must not arrive after ComfyUI's terminal event (which would also poison page-recovery state).
        self.closed.set()
        try:
            while True:
                self.pending.get_nowait()
        except queue.Empty:
            pass

    def _run(self):
        while True:
            try:
                images, ready, pixel_frames = self.pending.get(timeout=0.1)
            except queue.Empty:
                if self.closed.is_set():
                    return
                continue

            try:
                if ready is not None:
                    ready.synchronize()
                if self.closed.is_set():
                    return
                frames = _pil_frames(images)
                duration_ms = 1000.0 * pixel_frames / (self.fps * max(1, len(frames)))
                png = _encode_apng(frames, duration_ms)
                if png is not None and not self.closed.is_set():
                    _send_png(png)
            except Exception as error:
                if not self.closed.is_set():
                    log.warning(
                        "H3 animated preview encoding failed; generation will continue without it: %r",
                        error,
                        exc_info=True,
                    )


def _suppressed_preview_image(*_args, **_kwargs):
    return None


class _H3PreviewWrapper:
    def __init__(self, preview_steps, fps):
        self.preview_steps = _normalize_preview_steps(preview_steps)
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
        encoder = _PreviewEncoder(self.fps)

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
            if completed_step not in self.preview_steps:
                return
            if not encoder.can_accept():
                return

            try:
                video = _video_stream(x0, latent_shapes)
                if video is None or video.ndim != 5:
                    return
                pixel_frames = _pixel_frame_count(int(video.shape[2]))
                selected = _pick_frames(video, _MAX_PREVIEW_FRAMES)
                images, ready = _stage_cpu(_latent_rgb(selected, latent_format))
                encoder.submit(images, ready, pixel_frames)
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
            encoder.close()
            for target, previous in previous_methods:
                target.decode_latent_to_preview_image = previous


class H3AnimatedPreview:
    """Attach a lightweight whole-shot APNG preview to an H3 MODEL."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "model": ("MODEL",),
                "preview_steps": (
                    _PREVIEW_STEPS_TYPE,
                    {
                        "default": {"__value__": [5]},
                        "tooltip": "Exact completed sampler steps; an empty list disables animated previews.",
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

    @classmethod
    def VALIDATE_INPUTS(cls, preview_steps=None, **_kwargs):
        try:
            _normalize_preview_steps(preview_steps)
            return True
        except (TypeError, ValueError) as error:
            return str(error)

    def patch(self, model, preview_steps=None, fps=24.0):
        steps = _normalize_preview_steps(preview_steps)
        if not steps:
            return (model,)

        patched = model.clone()
        wrapper = _H3PreviewWrapper(steps, fps)
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
