"""Frozen two-stage early-denoise correction for the Ideogram 4 conditional model."""

from __future__ import annotations

from pathlib import Path

import torch
import torch.nn.functional as F
from comfy.ldm.ideogram4.model import Ideogram4Transformer
from safetensors.torch import load_file


TENSOR_FILE = Path(__file__).resolve().parent / "models" / "debanner_two_stage_v1.safetensors"


class DebannerTwoStagePatch:
    """Apply the validated Stage-2 and Stage-1 directions on the first conditional pass."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "model": ("MODEL",),
                "enabled": ("BOOLEAN", {"default": True}),
                "stage1_strength": (
                    "FLOAT", {"default": 0.4, "min": 0.0, "max": 2.0, "step": 0.01}
                ),
                "stage2_strength": (
                    "FLOAT",
                    {
                        "default": 0.6422342360019688,
                        "min": 0.0,
                        "max": 3.0,
                        "step": 0.01,
                    },
                ),
            }
        }

    RETURN_TYPES = ("MODEL",)
    FUNCTION = "patch"
    CATEGORY = "model_patches/ideogram4"

    def patch(
        self,
        model,
        enabled=True,
        stage1_strength=0.4,
        stage2_strength=0.6422342360019688,
    ):
        stage1_strength = float(stage1_strength)
        stage2_strength = float(stage2_strength)

        # This is deliberately stricter than installing a zero-strength callback: callers
        # testing the off state receive the original MODEL object unchanged.
        if not enabled or (stage1_strength == 0.0 and stage2_strength == 0.0):
            return (model,)

        if not TENSOR_FILE.is_file():
            raise FileNotFoundError(f"Bundled direction file is missing: {TENSOR_FILE}")

        stored = load_file(str(TENSOR_FILE), device="cpu")
        directions = {}
        strengths = {}
        for block in range(20, 25):
            directions[block] = stored[f"stage2.block_{block:02d}"].float().contiguous()
            strengths[block] = stage2_strength
        for block in range(25, 29):
            directions[block] = stored[f"stage1.block_{block:02d}"].float().contiguous()
            strengths[block] = stage1_strength

        patched = model.clone()
        state = {"step_index": -1, "pass_index": -1, "sigma": None, "cache": {}}

        def block_patch(args):
            h = args["img"]
            sigma = float(
                args["sigma"].detach().reshape(-1)[0].to(device="cpu", dtype=torch.float32)
            )
            if state["sigma"] is not None and sigma > state["sigma"] + 1e-7:
                state["step_index"] = -1
                state["pass_index"] = -1
                state["sigma"] = None
            if state["sigma"] is None or abs(sigma - state["sigma"]) > 1e-7:
                state["step_index"] += 1
                state["pass_index"] = -1
                state["sigma"] = sigma

            block_index = int(args["block_index"])
            if block_index == 0:
                state["pass_index"] += 1
            if (
                state["step_index"] != 0
                or state["pass_index"] != 0
                or block_index not in directions
                or strengths[block_index] == 0.0
            ):
                return {"img": h}

            image_shape = args.get("image_shape")
            if image_shape is None:
                raise RuntimeError("Ideogram 4 hook did not provide image_shape")
            grid_height, grid_width = (int(value) for value in image_shape)
            cache_key = (
                block_index,
                grid_height,
                grid_width,
                h.device.type,
                h.device.index,
                h.dtype,
            )
            spatial_direction = state["cache"].get(cache_key)
            if spatial_direction is None:
                source = directions[block_index].permute(0, 3, 1, 2)
                spatial_direction = F.interpolate(
                    source,
                    size=(grid_height, grid_width),
                    mode="nearest",
                )
                spatial_direction = spatial_direction.permute(0, 2, 3, 1).reshape(
                    1,
                    grid_height * grid_width,
                    -1,
                )
                spatial_direction = spatial_direction.to(device=h.device, dtype=h.dtype)
                state["cache"][cache_key] = spatial_direction

            image_offset = int(args["img_offset"])
            token_count = grid_height * grid_width
            if h.shape[-1] != spatial_direction.shape[-1]:
                raise RuntimeError(
                    "Bundled direction channel count does not match model activation"
                )

            out = h.clone()
            image_tokens = out[:, image_offset : image_offset + token_count]
            moved = image_tokens - strengths[block_index] * spatial_direction
            token_norms = torch.linalg.vector_norm(image_tokens, dim=-1, keepdim=True)
            moved = F.normalize(moved, p=2, dim=-1) * token_norms
            out[:, image_offset : image_offset + token_count] = moved
            return {"img": out}

        patched.set_model_patch(block_patch, "ideogram4_block")
        return (patched,)


if getattr(Ideogram4Transformer, "supports_ideogram4_block_patch", False):
    NODE_CLASS_MAPPINGS = {"DebannerTwoStagePatch": DebannerTwoStagePatch}
    NODE_DISPLAY_NAME_MAPPINGS = {
        "DebannerTwoStagePatch": "Ideogram 4 Two-Stage Correction",
    }
else:
    # Keeping the node out of object_info makes the catalogue presence check hide
    # the workflow until its paired reversible core hook is installed.
    NODE_CLASS_MAPPINGS = {}
    NODE_DISPLAY_NAME_MAPPINGS = {}
