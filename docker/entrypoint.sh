#!/usr/bin/env bash
# Starts ComfyUI and the app, in that order, and supervises ComfyUI just enough to restart it on request.
#
# Two processes in one container is not the tidy arrangement, and it is the deliberate one: ComfyUI is INCLUDED in this
# image so a fresh `docker compose up` renders something. The app fetches the tag model itself if it is missing.
#
# The ONE thing supervised here is a restart, because ComfyUI reads its custom nodes and imports every module once, at
# startup -- so a patch applied from the settings page changes nothing until it is restarted, and inside this container
# the app is the only thing that could do it. The protocol is two files in $CONTROL_DIR:
#
#   comfy.pid          written here on every start; how the app finds the process
#   comfy-restarting   written by the app before it signals; how THIS script knows an exit was asked for
#
# Without the marker an exit is still a crash, and a crash still takes the container down -- which is the honest signal
# it has always been, and the container runtime's restart policy is the right place to decide what happens next.
set -euo pipefail

COMFY_DIR=/opt/ComfyUI
COMFY_PY=/opt/comfy-venv/bin/python
CONTROL_DIR="${ComfyUI__Supervisor:-/run/imagegen}"

log() { printf '[entrypoint] %s\n' "$1"; }

mkdir -p /data /data/logs "$CONTROL_DIR"
rm -f "$CONTROL_DIR/comfy-restarting"

COMFY_PID=

# --enable-cors-header is REQUIRED: the app's browser client talks to /forge on this container, but ComfyUI's own
# endpoints are reached directly for progress and previews, and it refuses cross-origin requests without it.
# Bound to 127.0.0.1 because only the app in this same container should reach it -- the queue gate is a guard, not a
# reason to expose the backend.
start_comfy() {
    cd "$COMFY_DIR"
    "$COMFY_PY" -X utf8 main.py --listen 127.0.0.1 --port 8188 --enable-cors-header &
    COMFY_PID=$!
    printf '%s' "$COMFY_PID" > "$CONTROL_DIR/comfy.pid"
    log "ComfyUI started (pid $COMFY_PID)"
}

log "starting ComfyUI"
start_comfy

# The app runs in the BACKGROUND, not via `exec`, so THIS shell stays PID 1 and remains the parent of BOTH ComfyUI
# and the app -- which is what lets `wait -n` below block on either with no polling. (An earlier version exec'd the
# app and ran the supervisor in a `supervise &` subshell; ComfyUI was then a sibling of that subshell rather than a
# child, so its `wait` returned immediately with "not a child of this shell" and the container stopped on boot.)
log "starting ImageGen on :8080"
cd /app
dotnet ImageGen.Web.dll &
APP_PID=$!

# `docker stop` sends TERM here; forward it to both so shutdown is clean rather than a 10-second kill. The marker is
# cleared first so the loop below reads this as the shutdown it is, not as a restart.
trap 'log "shutting down"; rm -f "$CONTROL_DIR/comfy-restarting"; kill -TERM "$COMFY_PID" "$APP_PID" 2>/dev/null || true; exit 0' TERM INT

# Block until EITHER child exits (no polling, no sleeps), then decide what it means:
#   * ComfyUI gone + the restart marker  -> a restart was asked for; bring it back and keep going.
#   * ComfyUI gone, no marker            -> a crash; take the container down, its backend is gone.
#   * the app gone                       -> nothing left to serve; take the container down.
# `|| true` because a child exiting non-zero (a crash, or the SIGTERM of a requested restart) must NOT trip `set -e`
# and skip the decision below.
while :; do
    wait -n || true

    if ! kill -0 "$COMFY_PID" 2>/dev/null; then
        if [ -e "$CONTROL_DIR/comfy-restarting" ]; then
            rm -f "$CONTROL_DIR/comfy-restarting"
            log "restart requested; starting ComfyUI again"
            start_comfy
            continue
        fi
        log "ComfyUI exited; stopping the container"
        rm -f "$CONTROL_DIR/comfy.pid"
        kill -TERM "$APP_PID" 2>/dev/null || true
        exit 1
    fi

    if ! kill -0 "$APP_PID" 2>/dev/null; then
        log "the app exited; stopping the container"
        kill -TERM "$COMFY_PID" 2>/dev/null || true
        exit 1
    fi
done
