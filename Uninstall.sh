#!/usr/bin/env bash
# Remove the ImageGen systemd service.
set -euo pipefail

SELF="$(readlink -f "$0")"

# --- self-elevate ---
if [ "$(id -u)" -ne 0 ]; then
  exec sudo "$SELF" "$@"
fi

systemctl disable --now imagegen.service || true
rm -f /etc/systemd/system/imagegen.service
systemctl daemon-reload

echo "Removed systemd service 'imagegen'."
