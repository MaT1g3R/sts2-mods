#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
DIST_DIR="$REPO_DIR/dist"

SKIP_TESTS=0
if [ "${1:-}" = "--skip-tests" ]; then
	SKIP_TESTS=1
elif [ "$#" -gt 0 ]; then
	echo "Usage: $0 [--skip-tests]" >&2
	exit 1
fi

echo "==> Building RunResumer (Release)..."
dotnet build "$REPO_DIR/RunResumer.csproj" -c Release -p:DeployToMods=false

if [ "$SKIP_TESTS" -eq 0 ]; then
	echo "==> Running verification tests (Release)..."
	dotnet run --project "$REPO_DIR/tests/RunResumer.Tests/RunResumer.Tests.csproj" -c Release -p:DeployToMods=false
fi

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

MOD_ID=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["id"])' "$MANIFEST_PATH")
MOD_VERSION=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["version"])' "$MANIFEST_PATH")

mkdir -p "$DIST_DIR"
ZIP_PATH="$DIST_DIR/${MOD_ID}-v${MOD_VERSION}.zip"
rm -f "$ZIP_PATH"

STAGE_ROOT=$(mktemp -d)
trap 'rm -rf "$STAGE_ROOT"' EXIT INT TERM
MOD_STAGE="$STAGE_ROOT/$MOD_ID"
mkdir -p "$MOD_STAGE"

cp "$LOADER_DLL" "$MOD_STAGE/"
cp "$CORE_DLL" "$MOD_STAGE/"
cp "$PROTOBUF_DLL" "$MOD_STAGE/"
cp "$MANIFEST_PATH" "$MOD_STAGE/"

echo "==> Creating archive $ZIP_PATH..."
python3 - "$STAGE_ROOT" "$MOD_ID" "$ZIP_PATH" <<'EOF'
import os
import sys
import zipfile

stage_root, mod_id, zip_path = sys.argv[1:4]
mod_dir = os.path.join(stage_root, mod_id)

with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for name in sorted(os.listdir(mod_dir)):
        full_path = os.path.join(mod_dir, name)
        if os.path.isfile(full_path):
            zf.write(full_path, arcname=f"{mod_id}/{name}")
EOF

echo "==> Package ready: $ZIP_PATH"
python3 - "$ZIP_PATH" <<'EOF'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "r") as zf:
    for info in zf.infolist():
        print(f"  {info.file_size:8d} -> {info.compress_size:8d}  {info.filename}")
EOF
