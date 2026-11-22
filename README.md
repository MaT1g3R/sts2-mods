# Slay the Spire 2 Mods

Monorepo for Slay the Spire 2 mods:

- [`run-resumer/`](./run-resumer) — Run Resumer (`RunResumer`)
- [`sts2-calculator/`](./sts2-calculator) — In-game Calculator (`Sts2Calculator`)
- [`sts2-no-sentry/`](./sts2-no-sentry) — Disable Sentry / Godot GDExtension shutdown workaround (`NoSentry`)

## Usage

All projects target `.NET 9.0` and resolve the game installation via `STS2_GAME_DIR` (or `-p:Sts2GameDir=...`), falling back to standard Steam install paths.

The top-level [`Makefile`](./Makefile) auto-detects mods from top-level directories containing a `.csproj` file, so adding a new mod requires no `Makefile` changes.

```bash
# List detected mods and available targets
make help
make list

# 1. Build and test a mod without deploying
make build-<mod>          # or: make build MOD=<mod>
make build-all            # build and test all detected mods

# 2. Build, test, and deploy a mod to the local STS2 mods/ directory
make deploy-<mod>         # or: make deploy MOD=<mod>
make deploy-all           # build, test, and deploy all detected mods

# 3. Prepare (stage) a Steam Workshop release for a mod
make steam-prepare-<mod>  # or: make steam-prepare MOD=<mod>

# 4. Upload a mod to Steam Workshop (requires ModUploader on PATH and Steam running)
make steam-upload-<mod>   # or: make steam-upload MOD=<mod>
```

### Options

- `CONFIGURATION=Release` (default) or `CONFIGURATION=Debug`
- `STS2_GAME_DIR=/path/to/Slay the Spire 2` to override the game install path

### Adding a New Mod

1. Create a top-level directory `<my-mod>/` with `<MyMod>.csproj` and `<MyMod>.json` (supporting `-p:DeployToMods=true|false`).
2. Add optional test projects under `<my-mod>/` — both `dotnet test` (`IsTestProject`/`Microsoft.NET.Test.Sdk`) and console test runners (`OutputType=Exe` under `tests/`/`Tests/`) are detected automatically.
3. For Steam Workshop publishing, add `<my-mod>/steam/` containing `workshop.json`, `steam_appid.txt` (`2868840`), and `image.png` (< 1 MB), then run `make steam-prepare-<my-mod>` / `make steam-upload-<my-mod>` (backed by [`scripts/steam-upload.sh`](./scripts/steam-upload.sh)).
