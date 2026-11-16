# RunResumer — Slay the Spire 2 Mod

RunResumer captures compact per-floor snapshots during singleplayer Slay the Spire 2 runs and lets players right-click any floor node in Run History to practice from that floor without affecting active saves, unlocks, stats, or run history.

## Architecture

- **`RunResumer.csproj` / `RunResumer.json`**: `.NET 9` STS2 native mod (`id: "RunResumer"`, `affects_gameplay: false`). Builds the thin loader (`RunResumer.dll`) and `src/RunResumer.Core/RunResumer.Core.csproj` (which compiles `Protos/run_archive.proto` via `Grpc.Tools`), deploying `RunResumer.dll`, `RunResumer.Core.dll`, `Google.Protobuf.dll`, and `RunResumer.json` to `<game>/mods/RunResumer/`.
- **`src/RunResumer.Loader/` (`AssemblyResolver.cs`, `RunResumerEntry.cs`)**: Thin bootstrapper with zero static dependency on `Google.Protobuf` or `RunResumer.Core` so `ModManager.TryLoadMod`'s `assembly.GetTypes()` scan succeeds before `[ModInitializer("Initialize")]` runs; `Initialize()` installs the mod-directory `AssemblyLoadContext.Resolving` / `AppDomain.AssemblyResolve` handlers, loads `RunResumer.Core.dll`, and invokes `RunResumerCoreEntry.Initialize()`.
- **`src/RunResumer.Core/RunResumerCoreEntry.cs`**: Applies Harmony patches (`com.vmservice.runresumer`) across `RunResumer.Core`.
- **`src/RunResumer.Core/Protos/run_archive.proto` & `src/RunResumer.Core/Storage/RunArchiveCodec.cs`**:
  - Stores one Brotli-compressed `RunArchive` per run (`run_resumer/<StartTime>.rrpb` inside the local profile directory, outside Steam Cloud quota).
  - Deduplicates static run metadata, per-file string tables (`model_ids`, `prop_names`, `epoch_ids`), `ActStatic` room lists, `ActMap`s, `RelicGrabBag`s, `MapDrawings`, discovery lists, decks, relics, and potions.
  - Does not duplicate `MapPointHistory` per floor; on load, reconstructs `SerializableRun.MapPointHistory` by slicing the first `floorNum - 1` entries from the `RunHistory` loaded by Run History.
  - Uses string `ModelId` tables rather than STS2's `ModelIdSerializationCache` integer IDs so archives stay independent of model registration order.
- **`src/RunResumer.Core/Storage/RunArchiveStorage.cs`**:
  - Handles atomic `.tmp` + rename writes, in-memory floor index caching, truncation of floors `>= floorNum` when a floor is reloaded, and orphan `.rrpb` cleanup (preserving files matching `.run` histories or an active `current_run.save`).
- **`src/RunResumer.Core/Replay/ReplaySession.cs`**:
  - Loads a reconstructed `SerializableRun` and launches it via `RunManager.SetUpSavedSingleplayer` + `NGame.LoadRun` in practice/sandbox mode.
- **`src/RunResumer.Core/Patches/`**:
  - `SaveRunCapturePatch.cs`: Hooks `RunSaveManager.SaveRun(SerializableRun, bool)` to capture singleplayer floor-entry snapshots (`PreFinishedRoom == null`, `ShouldSave == true`).
  - `ReplayModePatches.cs`: Forces `ShouldSave = false` in `RunManager.InitializeShared`, skips `SaveManager.IncrementNumReloads` and `DailyRunUtility.UploadScore`, redirects `NGame.GoToTimelineAfterRun` back to main menu, reopens Compendium → Run History on the replayed run via `NMainMenu._Ready`, and hooks profile/history lifecycle for cache invalidation and orphan cleanup.
  - `RunHistoryUiPatches.cs`: Appends replay availability hints to `NMapPointHistoryHoverTip` in Run History and triggers immediate replay launch on right-click (`MouseButton.Right`) on `NMapPointHistoryEntry`.

## Build, Test, and Deploy

Set `STS2_GAME_DIR` (or `-p:Sts2GameDir=...`) if the game is not at `$HOME/.steam/steam/steamapps/common/Slay the Spire 2`.

```bash
# Build
dotnet build -c Release

# Run verification tests (Harmony target resolution + 50-floor round-trip & compression)
dotnet run --project tests/RunResumer.Tests/RunResumer.Tests.csproj -c Release

# Build and deploy to <game>/mods/RunResumer/
dotnet build -c Release -p:DeployToMods=true
```

## Guidelines & Invariants

- **Keep `src/RunResumer.Core/**` and `tests/**` excluded in `RunResumer.csproj`**: The loader project only compiles `src/RunResumer.Loader/**/*.cs` and must not statically reference `Google.Protobuf` or `RunResumer.Core`.
- **Keep practice replays non-destructive**: Any new entry point or end-of-run hook touched during `ReplaySession.IsActive` must never mutate `current_run.save`, `progress.save`, `history/*.run`, or daily leaderboards.
- **Singleplayer only**: `RunArchiveStorage` and `ReplaySession` guard on `Players.Count == 1`; multiplayer runs are intentionally excluded.
- **Re-verify after STS2 updates**: Re-run `RunResumer.Tests` after any game update to confirm all 12 Harmony targets and `SerializableRun` fields still match `sts2.dll`.
- **No machine-local paths**: Never commit user-specific paths or local overrides; keep `STS2_GAME_DIR` configurable via environment variable or MSBuild property.
