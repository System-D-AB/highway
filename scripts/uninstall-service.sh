#!/usr/bin/env bash
# Stop and remove the Highway systemd service.
# Usage:  sudo bash scripts/uninstall-service.sh [unit-name]   (default: highway)
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "This must run as root (sudo bash scripts/uninstall-service.sh)." >&2
    exit 1
fi

UNIT_NAME="${1:-highway}"
UNIT_PATH="/etc/systemd/system/${UNIT_NAME}.service"

systemctl stop "$UNIT_NAME" 2>/dev/null || true
systemctl disable "$UNIT_NAME" 2>/dev/null || true
rm -f "$UNIT_PATH"
systemctl daemon-reload

echo "Removed '${UNIT_NAME}'. (Data and logs under the install directory are left intact.)"
