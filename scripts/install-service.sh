#!/usr/bin/env bash
# Install Highway as a systemd service and start it.
# Usage:  sudo bash scripts/install-service.sh [unit-name]   (default: highway)
#   One line runs at a time (079): if the unit already runs another line (server/<major>/), it is stopped and
#   re-pointed at this one; the other line's data and config are left untouched, so running its install script again
#   rolls back. Run multiple brokers on one host by giving each a distinct unit name and a config with unique
#   server.port / dashboard.port / server.dataDir.
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
LINE="$(cat "$ROOT/LINE" 2>/dev/null || echo "(unversioned)")"

# 079 R4: re-point the one unit. A unit that runs another line is stopped and disabled first, and named.
if [ -f "$UNIT_PATH" ]; then
    OLD_ROOT="$(sed -n 's#^WorkingDirectory=##p' "$UNIT_PATH" | head -1)"
    if [ "$OLD_ROOT" = "$ROOT" ]; then
        echo "'${UNIT_NAME}' already runs this line ($ROOT); reinstalling it."
    else
        echo "'${UNIT_NAME}' runs another line: ${OLD_ROOT:-unknown}"
        echo "Replacing it with line $LINE ($ROOT). To go back later, run that line's scripts/install-service.sh."
    fi
    systemctl stop "$UNIT_NAME" || true
    systemctl disable "$UNIT_NAME" >/dev/null 2>&1 || true
fi

# 079 R5: a port still held now belongs to a broker started by hand (or another program) - refuse, naming it.
for PORT in $("$ROOT/bin/highways" --validate --config "$ROOT/config/highway.json" 2>/dev/null | sed -n 's/^ \+port \+: \+\([0-9]\+\).*/\1/p'); do
    HOLDER="$(ss -ltnpH "sport = :$PORT" 2>/dev/null | head -1)"
    if [ -n "$HOLDER" ]; then
        echo "Port $PORT is already in use: $HOLDER" >&2
        echo "Stop it, then run this script again." >&2
        exit 1
    fi
done

# Bake the install root into the unit (the template ships with __ROOT__ placeholders).
sed "s#__ROOT__#${ROOT}#g" "$TEMPLATE" > "$UNIT_PATH"

systemctl daemon-reload
systemctl enable "$UNIT_NAME"
systemctl start "$UNIT_NAME"

echo "Installed and started '${UNIT_NAME}': Highway line $LINE from $ROOT."
echo "  status: systemctl status ${UNIT_NAME}"
echo "  logs  : journalctl -u ${UNIT_NAME} -f"
systemctl --no-pager status "$UNIT_NAME" || true
