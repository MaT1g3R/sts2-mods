# Slay the Spire 2 Mods

Monorepo for Slay the Spire 2 mods:

- [`run-history-plus/`](./run-history-plus) — Run History Plus (`RunHistoryPlus`): filtering (outcome, character, game type, player count `1–4+`, relics), sorting, run selector, and click-to-copy seed
- [`run-resumer/`](./run-resumer) — Run Resumer (`RunResumer`): compact per-floor snapshots, right-click floor replay, `.zip` run export/import, and per-profile disk space management in Mod Settings
- [`sts2-calculator/`](./sts2-calculator) — In-game Calculator (`Sts2Calculator`)
- [`sts2-no-sentry/`](./sts2-no-sentry) — Disable Sentry / Godot GDExtension shutdown workaround (`NoSentry`)

Developer tooling (excluded from auto-detected publishable mods):

- [`tools/sts2-hot-reloader/`](./tools/sts2-hot-reloader) — Dev-only hot-reload host (`Sts2HotReloader`) that loads mods from `dev-mods/` inside collectible `AssemblyLoadContext`s without restarting STS2 from Steam

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

# 3. Live hot-reload workflow (no Steam restart required)
make hot-install          # one-time install of Sts2HotReloader into <game>/mods/
make hot-deploy-<mod>     # build and hot-reload a mod in the running game
make hot-deploy-all       # switch all mods to hot-reload mode
make hot-watch-<mod>      # watch .cs/.csproj/.proto files and hot-reload on save
make hot-disable-<mod>    # unload and remove a mod from dev-mods/ (or hot-disable-all)

# 4. Prepare (stage) a Steam Workshop release for a mod
make steam-prepare-<mod>  # or: make steam-prepare MOD=<mod>

# 5. Upload a mod to Steam Workshop (requires ModUploader on PATH and Steam running)
make steam-upload-<mod>   # or: make steam-upload MOD=<mod>

# 6. Inspect or bump SemVer versions (manifest JSON, .csproj <Version>, and workshop.json changeNote)
make versions
make bump BUMP=patch MODS="run-history-plus run-resumer"
make bump-all BUMP=minor NOTE="Support STS2 v0.115.0" DRY_RUN=1
./scripts/bump-version.py patch run-history-plus sts2-calculator
```

### Hot Reloading (Dev)

1. Run `make hot-deploy-all` (or `make hot-deploy-<mod>`) once before starting STS2 from Steam. This installs `Sts2HotReloader`, removes any normal `<game>/mods/<ModId>/` copy to avoid double-loading, and stages the mod into `<game>/mods/Sts2HotReloader/dev-mods/<ModId>/`.
2. While the game is running, run `make hot-deploy-<mod>` after edits—or leave `make hot-watch-<mod>` running to rebuild and reload automatically on save.
3. Press **`F9`** or **`Ctrl+Shift+R`** in-game at any time to force-reload all enabled dev mods. Hot-reloaded mods also appear in STS2's **Installed Mods** screen and can be toggled on/off live via their checkboxes.
4. When finished developing, run `make deploy-<mod>` to automatically clear `dev-mods/<ModId>/` and deploy normally to `<game>/mods/<ModId>/`.

### Options

- `CONFIGURATION=Release` (default) or `CONFIGURATION=Debug`
- `STS2_GAME_DIR=/path/to/Slay the Spire 2` to override the game install path

### Adding a New Mod

1. Create a top-level directory `<my-mod>/` with `<MyMod>.csproj` and `<MyMod>.json` (supporting `-p:DeployToMods=true|false`).
2. Add optional test projects under `<my-mod>/` — both `dotnet test` (`IsTestProject`/`Microsoft.NET.Test.Sdk`) and console test runners (`OutputType=Exe` under `tests/`/`Tests/`) are detected automatically.
3. To support live hot reloading via `Sts2HotReloader`, expose a `public static void Shutdown()` method on the `[ModInitializer]` type that unpatches Harmony and frees any injected Godot nodes/signals.
4. For Steam Workshop publishing, add `<my-mod>/steam/` containing `workshop.json`, `steam_appid.txt` (`2868840`), and `image.png` (< 1 MB), then run `make steam-prepare-<my-mod>` / `make steam-upload-<my-mod>` (backed by [`scripts/steam-upload.sh`](./scripts/steam-upload.sh)).
