#!/bin/sh
set -eu

# Watch a mod's source files and automatically run scripts/hot-deploy.sh on every change.
# Usage: sh scripts/hot-watch.sh <mod-dir> [configuration]

if [ "$#" -lt 1 ]; then
    printf 'Usage: %s <mod-dir> [configuration]\n' "$0" >&2
    exit 1
fi

MOD_DIR=$1
CONFIGURATION=${2:-Release}

if [ ! -d "$MOD_DIR" ]; then
    printf 'Error: Mod directory not found: %s\n' "$MOD_DIR" >&2
    exit 1
fi

MARKER=$(mktemp)
cleanup() {
    rm -f "$MARKER"
}
trap cleanup EXIT INT TERM

# Initial build & hot-deploy
sh scripts/hot-deploy.sh "$MOD_DIR" "$CONFIGURATION" || true
touch "$MARKER"

printf '==> Watching %s for changes (Ctrl+C to stop)...\n' "$MOD_DIR"

while :; do
    CHANGED=$(find "$MOD_DIR" \
        \( -name bin -o -name obj -o -name Tests -o -name tests -o -name steam -o -name dist \) -prune -o \
        \( -name '*.cs' -o -name '*.csproj' -o -name '*.proto' \) -type f -newer "$MARKER" -print | head -n 1)

    if [ -n "$CHANGED" ]; then
        # Brief settle pause for multi-file saves
        sleep 0.3
        touch "$MARKER"
        printf '\n==> Change detected (%s); rebuilding...\n' "$CHANGED"
        sh scripts/hot-deploy.sh "$MOD_DIR" "$CONFIGURATION" || true
    fi

    sleep 0.5
done
