#!/usr/bin/env bash
# Register ImageGen as a systemd service that starts at boot.
set -euo pipefail

SELF="$(readlink -f "$0")"
ROOT="$(dirname "$SELF")"

# --- self-elevate ---
if [ "$(id -u)" -ne 0 ]; then
  exec sudo "$SELF" "$@"
fi

RUN_USER="${SUDO_USER:-root}"
EXE="$ROOT/bin/ImageGen.Web"
if [ ! -x "$EXE" ]; then
  echo "ERROR: $EXE not found. Run Install.sh from the app folder." >&2
  exit 1
fi

cat > /etc/systemd/system/imagegen.service <<EOF
[Unit]
Description=ImageGen
After=network.target

[Service]
Type=simple
User=$RUN_USER
WorkingDirectory=$ROOT/bin
Environment=IMAGEGEN_OPEN_BROWSER=0
ExecStart=$ROOT/bin/ImageGen.Web
Restart=always

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now imagegen.service

echo "Installed systemd service 'imagegen' (starts at boot, running as $RUN_USER). Remove it with Uninstall.sh."
