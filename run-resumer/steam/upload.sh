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

echo "==> Building RunResumer (Release)..."
dotnet build "$REPO_DIR/RunResumer.csproj" -c Release -p:DeployToMods=false

echo "==> Running verification tests (Release)..."
dotnet run --project "$REPO_DIR/tests/RunResumer.Tests/RunResumer.Tests.csproj" -c Release -p:DeployToMods=false

OUT_DIR="$REPO_DIR/bin/Release/net9.0"
LOADER_DLL="$OUT_DIR/RunResumer.dll"
CORE_DLL="$OUT_DIR/RunResumer.Core.dll"
PROTOBUF_DLL="$OUT_DIR/Google.Protobuf.dll"
MANIFEST_PATH="$REPO_DIR/RunResumer.json"

for REQUIRED_FILE in "$LOADER_DLL" "$CORE_DLL" "$PROTOBUF_DLL" "$MANIFEST_PATH"; do
	if [ ! -f "$REQUIRED_FILE" ]; then
		echo "Error: Required release file not found at $REQUIRED_FILE" >&2
		exit 1
	fi
done

echo "==> Staging mod content into $CONTENT_DIR..."
rm -f "$CONTENT_DIR"/*.dll "$CONTENT_DIR"/*.pdb "$CONTENT_DIR"/*.json "$CONTENT_DIR"/*.pck
cp "$LOADER_DLL" "$CONTENT_DIR/"
cp "$CORE_DLL" "$CONTENT_DIR/"
cp "$PROTOBUF_DLL" "$CONTENT_DIR/"
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
