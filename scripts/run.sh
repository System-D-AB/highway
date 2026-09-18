#!/usr/bin/env bash
# Run the Highway broker in the foreground (for testing / manual runs).
# Usage:  bash scripts/run.sh [extra highways args]
# The distribution's config/highway.json is passed automatically unless you
# supply your own --config.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="$ROOT/bin/highways"

if [ ! -x "$BIN" ]; then
    chmod +x "$BIN" 2>/dev/null || true
fi
if [ ! -f "$BIN" ]; then
    echo "Could not find the highways binary at '$BIN'." >&2
    exit 1
fi

CFG="$ROOT/config/highway.json"
ARGS=()
case " $* " in
    *" --config "*) ;;                                   # caller supplied one
    *) [ -f "$CFG" ] && ARGS+=(--config "$CFG") ;;
esac

exec "$BIN" "${ARGS[@]}" "$@"
