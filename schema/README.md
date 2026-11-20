# Catalog format (schema 1)

`catalog.schema.json` is the format of `catalog.json`, as JSON Schema 2020-12. Rules the schema can't express are checked by `scripts/validate.cs`:
- IDs are unique.
- `requires`, `optional` and `conflicts` name existing IDs.
- No two entries claim the same folder.

## Top level
| Field | What |
|---|---|
| `schemaVersion` | Always `1` for this format. The app refuses a catalog whose `schemaVersion` it doesn't know. |
| `version` | The catalog's own version (`1.4.0`). Bump it whenever entries change; the app uses it to say what's new. |
| `forever` | The client the catalog was checked against: `gameVersion` (`1.60.1`), `interface` (`16001`), `curseforgeVersionTypeId` (CurseForge's ID for that game version). Updated by hand when the client patches. |
| `addons` | The entries. |

## An entry
| Field | Required | What |
|---|---|---|
| `id` | yes | Lowercase words joined by hyphens (`baganator`, `wim`). Stable: never changed or reused. The app's library refers to it. |
| `name` | yes | Display name. |
| `summary` | yes | One sentence, in our own words. |
| `sources` | yes | Where to get it, in order of preference (see below). At least one. |
| `folders` | yes | Every top-level AddOns folder it installs. Several-folder add-ons list all of them (Questie, QuestieDB, QuestieTrace); they're always installed, updated and removed together. Never a `Spoken*` folder. |
| `tocPath` | | The `.toc` to read the Interface from, relative to the zip's top level (`Questie_Forever.toc`, `OpenMap/OpenMap.toc`). Without it, the app looks for `<Folder>_Forever.toc`, `<Folder>-Forever.toc`, then `<Folder>.toc` in the first folder. |
| `author` | | As the add-on credits it. |
| `tags` | | Lowercase words for Browse's filters (`bags`, `quests`, `chat`). |
| `homepage` | | The add-on's main page, `https://` only. |
| `requires` | | IDs that must be installed too. Browse offers them together. |
| `optional` | | IDs it works with when they're there. Listed, not added. |
| `conflicts` | | IDs it overlaps with. A warning, never a blocker. |
| `savedVariables` | | Its SavedVariables names, so uninstall can say which WTF files are its. |
| `notes` | | Anything worth knowing ("the author disabled CurseForge API downloads, so Wago comes first"). |
| `withdrawn` | | Releases the app never offers: `{ "release": "861", "reason": "…" }`. `release` is a GitHub tag, CurseForge file ID or Wago release, as the source names it. |
| `overrides` | | Changes from a release on: `{ "releaseFrom": "900", "folders": [...] }` and/or `tocPath`. Rare. |

## Sources
| `type` | Fields |
|---|---|
| `github` | `repo` (`owner/name`), `assetPattern` (regex the asset's file name must match), optional `excludePattern`. The release GitHub flags as **Latest** is used, never a pre-release. |
| `curseforge` | `projectId`. The newest stable (release-type) file that lists `forever.gameVersion`. |
| `wago` | `slug`, as in `https://addons.wago.io/addons/<slug>`. Newest stable release. |

Regexes use .NET syntax, matched against the file name only.

## Example
```json
{
  "id": "baganator",
  "name": "Baganator",
  "author": "plusmouse",
  "summary": "Bag and bank replacement with search and sorting.",
  "tags": ["bags", "inventory"],
  "sources": [
    { "type": "wago", "slug": "baganator" },
    { "type": "curseforge", "projectId": 914482 }
  ],
  "folders": ["Baganator"],
  "tocPath": "Baganator/Baganator.toc",
  "requires": ["syndicator"]
}
```
