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
| `folders` | yes | Every top-level AddOns folder it installs. Several-folder add-ons list all of them (Questie, QuestieDB, QuestieTrace); they're always installed, updated and removed together. No paths and no leading dot. |
| `tocPath` | | The `.toc` to read the Interface from, relative to the zip's top level (`Questie_Forever.toc`, `OpenMap/OpenMap.toc`). Without it, the app looks for `<Folder>_Forever.toc`, `<Folder>-Forever.toc`, `<Folder>_Camelot.toc`, `<Folder>-Camelot.toc`, then `<Folder>.toc` in the first folder (Camelot is the Forever client's own name). |
| `author` | | One name: the add-on's main author, or the owner of its listing on its first source (the Wago or CurseForge author, else the GitHub owner). Not a list of contributors or a group: the validator refuses a comma, a semicolon, "&", "and others" or "contributors". |
| `tags` | | From the [fixed list](#tags), main tag first (see below). |
| `homepage` | | The add-on's main page, `https://` only. |
| `requires` | | IDs that must be installed too. Browse offers them together. |
| `optional` | | IDs it works with when they're there. Listed, not added. |
| `conflicts` | | IDs it overlaps with. A warning, never a blocker. |
| `savedVariables` | | Its SavedVariables names, so uninstall can say which WTF files are its. |
| `notes` | | Anything worth knowing ("the author disabled CurseForge API downloads, so Wago comes first"). |
| `withdrawn` | | Releases the app never offers: `{ "release": "861", "reason": "…" }`. `release` is a GitHub tag, CurseForge file ID or Wago release, as the source names it. |
| `overrides` | | Changes from a release on: `{ "releaseFrom": "900", "folders": [...] }` and/or `tocPath`. Rare. |

## Tags
`tags` come from this list, and the validator refuses any other. **The first tag is the add-on's main one:** the app shows its icon on the add-on's cards when the add-on has no `icon` of its own, so put first what the add-on is mainly for. Add more only when they help someone find it (Questie: `quests`, `map`).

| Tag | For add-ons about |
|---|---|
| `auction-house` | Buying, selling and pricing at the auction house. |
| `bags` | Bags, the bank and items across characters. |
| `chat` | Chat windows, whispers and messages. |
| `class` | Your class: its spells and abilities, trainers and talents. |
| `combat` | Fights: damage and threat meters, cooldowns, combat text. |
| `dungeons` | Dungeons and raids: their bosses, abilities and groups. |
| `loot` | Loot tables, drops and rolling for loot. |
| `map` | The world map and the minimap. |
| `professions` | Professions and gathering: recipes, skill-ups, herb and ore nodes. |
| `quests` | Quests, objectives and quest givers. |
| `ui` | The game's own interface: moving, hiding and arranging its windows, bars and buttons. |

Adding a tag is a schema change, made in a PR with the app's icon for it. The [Add an add-on](../.github/ISSUE_TEMPLATE/addon.yml) form offers the same list; the validator's self-test checks that the two match.

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
  "tags": ["bags"],
  "sources": [
    { "type": "wago", "slug": "baganator" },
    { "type": "curseforge", "projectId": 914482 }
  ],
  "folders": ["Baganator"],
  "tocPath": "Baganator/Baganator.toc",
  "requires": ["syndicator"]
}
```
