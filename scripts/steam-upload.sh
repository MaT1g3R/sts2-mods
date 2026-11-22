#!/usr/bin/env sh
# Shared Steam Workshop staging and upload script for all mods in this repo.
#
# Usage:
#   scripts/steam-upload.sh <mod-dir> [--stage-only] [--skip-build]
#
# Environment variables:
#   CONFIGURATION   Build configuration (default: Release)
#   STS2_GAME_DIR   Optional override for Slay the Spire 2 install path
#
# To onboard a future mod to Steam Workshop:
#   1. Create <mod-dir>/steam/ containing:
#        - workshop.json    (Workshop title, description, visibility, changeNote, tags)
#        - steam_appid.txt  (2868840 for Slay the Spire 2)
#        - image.png        (preview image, must be < 1 MB)
#   2. Run `make steam-prepare-<mod-dir>` or `./scripts/steam-upload.sh <mod-dir> --stage-only`
#   3. Run `make steam-upload-<mod-dir>` or `./scripts/steam-upload.sh <mod-dir>`
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd -P)
CONFIGURATION=${CONFIGURATION:-Release}

usage() {
	cat >&2 <<'EOF'
Usage: scripts/steam-upload.sh <mod-dir> [--stage-only] [--skip-build]

Options:
  --stage-only   Build, test, and stage <mod-dir>/steam/content/ without uploading
  --skip-build   Skip running `make build-<mod>` (assumes mod is already built)
  -h, --help     Show this help message
EOF
}

if [ "$#" -lt 1 ]; then
	usage
	exit 1
fi

MOD_ARG=""
STAGE_ONLY=0
SKIP_BUILD=0

for arg in "$@"; do
	case "$arg" in
		--stage-only)
			STAGE_ONLY=1
			;;
		--skip-build)
			SKIP_BUILD=1
			;;
		-h|--help)
			usage
			exit 0
			;;
		-*)
			echo "Error: Unknown option '$arg'." >&2
			usage
			exit 1
			;;
		*)
			if [ -n "$MOD_ARG" ]; then
				echo "Error: Unexpected extra argument '$arg'." >&2
				usage
				exit 1
			fi
			MOD_ARG="$arg"
			;;
	esac
done

if [ -z "$MOD_ARG" ]; then
	echo "Error: <mod-dir> is required." >&2
	usage
	exit 1
fi

# Normalize mod directory name (strip leading ./ and trailing /)
MOD_NAME=$(printf '%s' "$MOD_ARG" | sed 's#^\./##; s#/*$##')
MOD_DIR="$REPO_ROOT/$MOD_NAME"
STEAM_DIR="$MOD_DIR/steam"
CONTENT_DIR="$STEAM_DIR/content"

if [ ! -d "$MOD_DIR" ]; then
	echo "Error: Mod directory '$MOD_NAME' does not exist." >&2
	exit 1
fi

if [ ! -d "$STEAM_DIR" ]; then
	echo "Error: $MOD_NAME/steam/ does not exist." >&2
	echo "Create $MOD_NAME/steam/ with workshop.json, steam_appid.txt, and image.png (< 1 MB) first." >&2
	exit 1
fi

for REQUIRED_META in workshop.json steam_appid.txt image.png; do
	if [ ! -f "$STEAM_DIR/$REQUIRED_META" ]; then
		echo "Error: Missing required Steam file at $MOD_NAME/steam/$REQUIRED_META" >&2
		exit 1
	fi
done

IMAGE_BYTES=$(wc -c < "$STEAM_DIR/image.png")
if [ "$IMAGE_BYTES" -ge 1048576 ]; then
	echo "Error: $MOD_NAME/steam/image.png is $IMAGE_BYTES bytes; Steam Workshop requires < 1MB (1048576 bytes)." >&2
	exit 1
fi

# Locate mod manifest (<ModId>.json containing an "id" property)
MANIFEST_PATH=""
for candidate in "$MOD_DIR"/*.json; do
	if [ -f "$candidate" ] && grep -qE '"id"[[:space:]]*:' "$candidate"; then
		MANIFEST_PATH="$candidate"
		break
	fi
done

if [ -z "$MANIFEST_PATH" ]; then
	echo "Error: No mod manifest (*.json with an \"id\" field) found in $MOD_NAME/." >&2
	exit 1
fi

MOD_ID=$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$MANIFEST_PATH" | head -n 1)
if [ -z "$MOD_ID" ]; then
	MOD_ID=$(basename "$MANIFEST_PATH" .json)
fi

if [ "$SKIP_BUILD" -eq 0 ]; then
	make -C "$REPO_ROOT" --no-print-directory "build-$MOD_NAME" CONFIGURATION="$CONFIGURATION"
fi

# Locate built output directory under bin/<CONFIGURATION>/net*
OUT_DIR=""
for tfm_dir in "$MOD_DIR/bin/$CONFIGURATION"/net*; do
	if [ -d "$tfm_dir" ]; then
		OUT_DIR="$tfm_dir"
		break
	fi
done

if [ -z "$OUT_DIR" ]; then
	echo "Error: Build output directory not found under $MOD_NAME/bin/$CONFIGURATION/." >&2
	exit 1
fi

MAIN_DLL="$OUT_DIR/$MOD_ID.dll"
if [ ! -f "$MAIN_DLL" ]; then
	echo "Error: Built mod DLL not found at $MAIN_DLL" >&2
	exit 1
fi

echo "==> Staging $MOD_ID release content into $CONTENT_DIR..."
mkdir -p "$CONTENT_DIR"
rm -f "$CONTENT_DIR"/*.dll "$CONTENT_DIR"/*.pdb "$CONTENT_DIR"/*.json "$CONTENT_DIR"/*.pck

for dll in "$OUT_DIR"/*.dll; do
	if [ -f "$dll" ]; then
		cp "$dll" "$CONTENT_DIR/"
	fi
done

cp "$MANIFEST_PATH" "$CONTENT_DIR/"

PCK_PATH="$MOD_DIR/$MOD_ID.pck"
if [ -f "$PCK_PATH" ]; then
	cp "$PCK_PATH" "$CONTENT_DIR/"
elif grep -qE '"has_pck"[[:space:]]*:[[:space:]]*true' "$MANIFEST_PATH"; then
	echo "Error: Manifest sets \"has_pck\": true, but $MOD_NAME/$MOD_ID.pck was not found." >&2
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

echo "==> Uploading $MOD_ID to Steam Workshop..."
cd "$STEAM_DIR"
ModUploader upload -w .
