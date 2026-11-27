#!/bin/sh
set -eu

# Build a mod and deploy it into Sts2HotReloader/dev-mods/<AssemblyName>/ for live hot reload.
# Usage: sh scripts/hot-deploy.sh <mod-dir> [configuration] [--disable]

if [ "$#" -lt 1 ]; then
    printf 'Usage: %s <mod-dir> [configuration] [--disable]\n' "$0" >&2
    exit 1
fi

MOD_DIR=$1
CONFIGURATION=${2:-Release}
DISABLE_ONLY=0
if [ "${2:-}" = "--disable" ] || [ "${3:-}" = "--disable" ]; then
    DISABLE_ONLY=1
    if [ "${2:-}" = "--disable" ]; then
        CONFIGURATION="Release"
    fi
fi

resolve_game_dir() {
    if [ -n "${STS2_GAME_DIR:-}" ]; then
        printf '%s\n' "$STS2_GAME_DIR"
        return 0
    fi
    for candidate in \
        "$HOME/.steam/steam/steamapps/common/Slay the Spire 2" \
        "$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2" \
        "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
    do
        if [ -d "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    printf 'Error: Could not find Slay the Spire 2 directory. Set STS2_GAME_DIR.\n' >&2
    return 1
}

find_mod_csproj() {
    for f in "$1"/*.csproj; do
        if [ -f "$f" ]; then
            printf '%s\n' "$f"
            return 0
        fi
    done
    return 1
}

extract_assembly_name() {
    csproj=$1
    name=$(sed -n 's/.*<AssemblyName>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*<\/AssemblyName>.*/\1/p' "$csproj" | head -n 1)
    if [ -n "$name" ]; then
        printf '%s\n' "$name"
    else
        basename "$csproj" .csproj
    fi
}

GAME_DIR=$(resolve_game_dir)
MOD_CSPROJ=$(find_mod_csproj "$MOD_DIR") || {
    printf 'Error: No top-level .csproj found in %s/\n' "$MOD_DIR" >&2
    exit 1
}
ASSEMBLY_NAME=$(extract_assembly_name "$MOD_CSPROJ")
HOST_DIR="$GAME_DIR/mods/Sts2HotReloader"
DEV_MOD_DIR="$HOST_DIR/dev-mods/$ASSEMBLY_NAME"
VANILLA_MOD_DIR="$GAME_DIR/mods/$ASSEMBLY_NAME"

if [ "$DISABLE_ONLY" -eq 1 ]; then
    if [ -d "$DEV_MOD_DIR" ]; then
        rm -rf "$DEV_MOD_DIR"
        printf '==> Removed %s from hot-reload dev-mods/\n' "$ASSEMBLY_NAME"
    else
        printf '==> %s is not currently in dev-mods/\n' "$ASSEMBLY_NAME"
    fi
    exit 0
fi

# Ensure Sts2HotReloader host mod is installed
if [ ! -f "$HOST_DIR/Sts2HotReloader.dll" ] || [ ! -f "$HOST_DIR/Sts2HotReloader.json" ]; then
    printf '==> Installing Sts2HotReloader host mod to %s...\n' "$HOST_DIR"
    dotnet build "tools/sts2-hot-reloader/Sts2HotReloader.csproj" \
        -c "$CONFIGURATION" \
        -p:DeployToMods=true \
        -p:Sts2GameDir="$GAME_DIR"
fi

# Remove any normal mods/<AssemblyName> install so STS2 ModManager does not double-load it
SWITCHED_FROM_NORMAL=0
if [ -d "$VANILLA_MOD_DIR" ]; then
    rm -rf "$VANILLA_MOD_DIR"
    SWITCHED_FROM_NORMAL=1
fi

printf '==> Building %s (%s, %s) for hot reload...\n' "$MOD_DIR" "$MOD_CSPROJ" "$CONFIGURATION"
dotnet build "$MOD_CSPROJ" \
    -c "$CONFIGURATION" \
    -p:DeployToMods=false \
    -p:Sts2GameDir="$GAME_DIR"

OUT_DIR="$MOD_DIR/bin/$CONFIGURATION/net9.0"
if [ ! -d "$OUT_DIR" ]; then
    printf 'Error: Build output directory not found: %s\n' "$OUT_DIR" >&2
    exit 1
fi

mkdir -p "$DEV_MOD_DIR"

# Stage all output DLLs and PDBs (excluding game/runtime references)
for artifact in "$OUT_DIR"/*.dll "$OUT_DIR"/*.pdb; do
    [ -f "$artifact" ] || continue
    base=$(basename "$artifact")
    case "$base" in
        sts2.*|GodotSharp.*|0Harmony.*|Sentry.*)
            continue
            ;;
    esac
    cp "$artifact" "$DEV_MOD_DIR/$base.tmp"
    mv "$DEV_MOD_DIR/$base.tmp" "$DEV_MOD_DIR/$base"
done

# If the mod uses a thin loader + *.Core.dll split (e.g., RunResumer), point the host directly at the Core entry
CORE_DLL="$OUT_DIR/$ASSEMBLY_NAME.Core.dll"
rm -f "$DEV_MOD_DIR/hotreload.json"
if [ -f "$CORE_DLL" ]; then
    CORE_PDB="$MOD_DIR/src/$ASSEMBLY_NAME.Core/bin/$CONFIGURATION/net9.0/$ASSEMBLY_NAME.Core.pdb"
    if [ -f "$CORE_PDB" ]; then
        cp "$CORE_PDB" "$DEV_MOD_DIR/$ASSEMBLY_NAME.Core.pdb.tmp"
        mv "$DEV_MOD_DIR/$ASSEMBLY_NAME.Core.pdb.tmp" "$DEV_MOD_DIR/$ASSEMBLY_NAME.Core.pdb"
    fi
    cat > "$DEV_MOD_DIR/hotreload.cfg.tmp" <<EOF
{
  "entry_assembly": "$ASSEMBLY_NAME.Core.dll",
  "entry_type": "$ASSEMBLY_NAME.${ASSEMBLY_NAME}CoreEntry",
  "init_method": "Initialize",
  "shutdown_method": "Shutdown"
}
EOF
    mv "$DEV_MOD_DIR/hotreload.cfg.tmp" "$DEV_MOD_DIR/hotreload.cfg"
else
    rm -f "$DEV_MOD_DIR/hotreload.cfg"
fi

# Copy mod manifest as manifest.cfg (non-.json so ModManager does not double-load it)
MANIFEST_JSON="$MOD_DIR/$ASSEMBLY_NAME.json"
if [ -f "$MANIFEST_JSON" ]; then
    cp "$MANIFEST_JSON" "$DEV_MOD_DIR/manifest.cfg.tmp"
    mv "$DEV_MOD_DIR/manifest.cfg.tmp" "$DEV_MOD_DIR/manifest.cfg"
else
    rm -f "$DEV_MOD_DIR/manifest.cfg"
fi

# Atomic stamp write triggers a single reload in Sts2HotReloader
STAMP="$(date -u +%Y%m%dT%H%M%SZ)-$$-$(od -An -N4 -tx1 /dev/urandom 2>/dev/null | tr -d ' \n')"
printf '%s\n' "$STAMP" > "$DEV_MOD_DIR/reload.stamp.tmp"
mv "$DEV_MOD_DIR/reload.stamp.tmp" "$DEV_MOD_DIR/reload.stamp"

printf '==> Hot-deployed %s to %s\n' "$ASSEMBLY_NAME" "$DEV_MOD_DIR"
if [ "$SWITCHED_FROM_NORMAL" -eq 1 ]; then
    printf '    Note: Removed %s to prevent double-loading.\n' "$VANILLA_MOD_DIR"
    printf '    If the game was already running with the normal install, restart STS2 once; all future builds will hot-reload live.\n'
fi
