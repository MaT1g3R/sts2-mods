# Steam Workshop Mod

## Directory layout
* `workshop.json` -- The config describing the Steam Workshop mod.
* `description.txt` -- Plain/BBCode copy of the Steam Workshop description for easy editing.
* `content/` -- Staging directory for the mod files uploaded to Steam Workshop (`RunHistoryPlus.dll` and `RunHistoryPlus.json`, populated automatically by `make steam-prepare-run-history-plus`).
* `image.png` -- The preview image shown on the Steam Workshop (must be less than 1MB).
* `steam_appid.txt` -- Steam App ID for Slay the Spire 2 (`2868840`).
* `mod_id.txt` -- Created/used by `ModUploader` once the Workshop item exists so subsequent runs update the same item instead of creating a new one.
* `README.md` -- This readme document.

## Releasing

### Prerequisites
- Steam must be running and logged into the Workshop owner account.
- `ModUploader` must be installed and available on `PATH`.
- Slay the Spire 2 assemblies must be discoverable by `RunHistoryPlus.csproj` (via default Steam paths, `STS2_GAME_DIR`, or `RunHistoryPlus.props.user`).

### Initial upload or update
1. Update the version in `../RunHistoryPlus.json` and the `changeNote` (and `visibility` if desired) in `workshop.json`.
2. From the repository root, run:
   ```sh
   make steam-upload-run-history-plus
   ```
   (or `./scripts/steam-upload.sh run-history-plus`). Use `make steam-prepare-run-history-plus` (or `--stage-only`) to build, test, and stage `steam/content/` without invoking `ModUploader`.
3. On the very first upload (when `mod_id.txt` does not exist yet), `ModUploader` creates a new Steam Workshop item. Save the resulting Workshop ID into `steam/mod_id.txt` if `ModUploader` does not write it automatically, and commit `mod_id.txt`.

## `workshop.json` Properties

Most properties can be substituted with `null` or removed from the JSON if you wish for them to remain unchanged after the initial upload.

```json
{
  "title": "",
  "description": "",
  "visibility": "private",
  "changeNote": "",
  "tags": [],
  "dependencies": [],
  "minBranch": "public-beta",
  "maxBranch": "public"
}
```
- `visibility`: `"private"`, `"public"`, `"unlisted"`, or `"friends_only"`.
- `tags`: Workshop tags (note: `"Tools & APIs"` is reserved for mods that function as tools or APIs).
- `dependencies`: List of Workshop item IDs that this mod depends on.
- `minBranch` / `maxBranch`: Optional branch bounds; omit or set to `null` to support all branches.
