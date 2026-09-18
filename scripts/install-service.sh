#!/usr/bin/env bash
# Install Highway as a systemd service and start it.
# Usage:  sudo bash scripts/install-service.sh [unit-name]   (default: highway)
#   Run multiple instances on one host by giving each a distinct unit name and a
#   config with unique server.port / dashboard.port / server.dataDir.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "This must run as root (sudo bash scripts/install-service.sh)." >&2
    exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNIT_NAME="${1:-highway}"
UNIT_PATH="/etc/systemd/system/${UNIT_NAME}.service"
TEMPLATE="$ROOT/scripts/highway.service"

if [ ! -f "$TEMPLATE" ]; then
    echo "Unit template not found at '$TEMPLATE'." >&2
    exit 1
fi

chmod +x "$ROOT/bin/highways"

# Bake the install root into the unit (the template ships with __ROOT__ placeholders).
sed "s#__ROOT__#${ROOT}#g" "$TEMPLATE" > "$UNIT_PATH"

systemctl daemon-reload
systemctl enable "$UNIT_NAME"
systemctl start "$UNIT_NAME"

echo "Installed and started '${UNIT_NAME}'."
echo "  status: systemctl status ${UNIT_NAME}"
echo "  logs  : journalctl -u ${UNIT_NAME} -f"
systemctl --no-pager status "$UNIT_NAME" || true
