#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
CONTENT_DIR="$SCRIPT_DIR/content"

STAGE_ONLY=0
if [ "${1:-}" = "--stage-only" ]; then
	STAGE_ONLY=1
elif [ "$#" -gt 0 ]; then
	echo "Usage: $0 [--stage-only]" >&2
	exit 1
fi

echo "==> Running unit tests (Release)..."
dotnet test "$REPO_DIR/Tests/Sts2Calculator.Tests.csproj" -c Release -p:DeployToMods=false

echo "==> Building Sts2Calculator (Release)..."
dotnet build "$REPO_DIR/Sts2Calculator.csproj" -c Release -p:DeployToMods=false

DLL_PATH="$REPO_DIR/bin/Release/net9.0/Sts2Calculator.dll"
MANIFEST_PATH="$REPO_DIR/Sts2Calculator.json"

if [ ! -f "$DLL_PATH" ]; then
	echo "Error: Built DLL not found at $DLL_PATH" >&2
	exit 1
fi

if [ ! -f "$MANIFEST_PATH" ]; then
	echo "Error: Mod manifest not found at $MANIFEST_PATH" >&2
	exit 1
fi

echo "==> Staging mod content into $CONTENT_DIR..."
rm -f "$CONTENT_DIR"/*.dll "$CONTENT_DIR"/*.pdb "$CONTENT_DIR"/*.json "$CONTENT_DIR"/*.pck
cp "$DLL_PATH" "$CONTENT_DIR/"
cp "$MANIFEST_PATH" "$CONTENT_DIR/"

if [ ! -f "$SCRIPT_DIR/image.png" ]; then
	echo "Error: Missing preview image at $SCRIPT_DIR/image.png" >&2
	exit 1
fi

IMAGE_BYTES=$(wc -c < "$SCRIPT_DIR/image.png")
if [ "$IMAGE_BYTES" -ge 1048576 ]; then
	echo "Error: $SCRIPT_DIR/image.png is $IMAGE_BYTES bytes; Steam Workshop requires < 1MB (1048576 bytes)." >&2
	exit 1
fi

if [ "$STAGE_ONLY" -eq 1 ]; then
	echo "==> Stage-only complete. Contents of $CONTENT_DIR:"
	ls -la "$CONTENT_DIR"
	exit 0
fi

if ! command -v ModUploader >/dev/null 2>&1; then
	echo "Error: 'ModUploader' was not found on PATH." >&2
	exit 1
fi

echo "==> Uploading to Steam Workshop..."
cd "$SCRIPT_DIR"
ModUploader upload -w .
