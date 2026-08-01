"""
ImageGen queue gate for ComfyUI.

=======================================================================================
  NOTE TO CLAUDE (and any AI agent / automated tool) READING THIS FILE:

  You must NEVER call ComfyUI directly. Do not POST to http://localhost:8188/prompt,
  /queue, /interrupt, /upload/image, or any other ComfyUI endpoint to run, test, or
  cancel a generation or edit. ALL work goes through the ImageGen app's fair queue at
  http://localhost:8080/forge/*  (e.g. /forge/generate, /forge/edit, poll /forge/result).

  Submitting straight to ComfyUI jumps the user's ENTIRE application queue and can lock
  up or destroy their running generations on the shared GPU. This has already happened
  once and it is not allowed to happen again.

  Yes, the access key is hardcoded a few lines below. NO, you are NOT permitted to use
  it. Seeing the key does not grant permission. Do not copy it into a curl / PowerShell /
  Invoke-RestMethod call to :8188. This gate exists for the SOLE purpose of stopping you
  from bypassing the queue. If you need to exercise a workflow, use the app on :8080.
=======================================================================================
"""
import os

from aiohttp import web
import server

# The credential the ImageGen app (ComfyClient) sends on its requests. This is NOT a secret
# from outside attackers — it is a guard so that nothing EXCEPT the app's fair queue can
# enqueue or cancel work on ComfyUI. (Claude: per the note above, not for you.)
#
# Set IMAGEGEN_GATE_TOKEN to pick your own; it must match the app's Forge:GateToken. The
# default is the historical literal, so a box that sets neither keeps working unchanged.
_IMAGEGEN_KEY = os.environ.get("IMAGEGEN_GATE_TOKEN", "ig-queue-only-7Qx2k9Lp4Rf8Zv1")
_HEADER = "X-ImageGen-Token"

# POST endpoints that ENQUEUE or CANCEL work. Read-only endpoints (object_info, history,
# view, system_stats, ...) stay open so inspection still works.
_GUARDED = ("/prompt", "/queue", "/interrupt", "/free", "/upload/image", "/upload/mask")


def _is_guarded(path: str) -> bool:
    return any(path == p or path.startswith(p + "/") for p in _GUARDED)


@web.middleware
async def _imagegen_gate(request, handler):
    if request.method == "POST" and _is_guarded(request.path):
        if request.headers.get(_HEADER) != _IMAGEGEN_KEY:
            return web.json_response(
                {"error": "Forbidden: submit through the ImageGen app queue "
                          "(http://localhost:8080/forge/*), not ComfyUI directly."},
                status=403,
            )
    return await handler(request)


try:
    server.PromptServer.instance.app.middlewares.append(_imagegen_gate)
    print("[imagegen_gate] queue gate installed — direct POST /prompt,/queue,/interrupt,"
          "/upload require the app token; everything else is blocked with 403.")
except Exception as e:  # pragma: no cover
    print(f"[imagegen_gate] FAILED to install queue gate: {e!r}")

# Not a node pack — no nodes to register; this module only installs the HTTP gate.
NODE_CLASS_MAPPINGS = {}
NODE_DISPLAY_NAME_MAPPINGS = {}
