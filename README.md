# Slay the Spire 2 Mods

Monorepo for Slay the Spire 2 mods:

- [`run-resumer/`](./run-resumer) — Run Resumer (`RunResumer`)
- [`sts2-calculator/`](./sts2-calculator) — In-game Calculator (`Sts2Calculator`)
- [`sts2-no-sentry/`](./sts2-no-sentry) — Disable Sentry / Godot GDExtension shutdown workaround (`NoSentry`)

## Building

All projects target `.NET 9.0` and resolve the game installation via `STS2_GAME_DIR` (or `-p:Sts2GameDir=...`), falling back to standard Steam install paths.

```bash
# Build all mods and tests without deploying
dotnet build Sts2Mods.sln -c Release -p:DeployToMods=false

# Run tests
dotnet test sts2-calculator/Tests/Sts2Calculator.Tests.csproj -c Release -p:DeployToMods=false
dotnet run --project run-resumer/tests/RunResumer.Tests/RunResumer.Tests.csproj -c Release -p:DeployToMods=false
```

To deploy an individual mod into your game's `mods/` directory:

```bash
dotnet build run-resumer/RunResumer.csproj -c Release -p:DeployToMods=true
dotnet build sts2-calculator/Sts2Calculator.csproj -c Release -p:DeployToMods=true
dotnet build sts2-no-sentry/NoSentry.csproj -c Release -p:DeployToMods=true
```
