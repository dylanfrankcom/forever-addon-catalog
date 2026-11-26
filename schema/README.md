# Catalog format (schema 3)

`catalog.schema.json` is the format of `catalog.json`, as JSON Schema 2020-12. Rules the schema can't express are checked by `scripts/validate.cs`:
- IDs are unique.
- `requires`, `optional` and `conflicts` name existing IDs.
- Every entry's `author` is in `authors`, and every author is the author of at least one entry.
- An author's `name` is one name, and their `profiles` are on GitHub, Wago or CurseForge, at most one per site.
- No two entries claim the same folder.
- Each edition's `interface` is its `gameVersion`'s, a test client belongs to the live edition whose version it runs, `forever` in `editions` matches the `forever` block, every edition an entry lists is in `editions`, and its `upToDate` is right.
- `added` is a real day and not in the future.
- On a PR, compared with the base branch: changed entries or authors come with a `version` bump, and new entries have `added`.

## Top level
| Field | What |
|---|---|
| `schemaVersion` | Always `3` for this format. The app refuses a catalog whose `schemaVersion` it doesn't know and keeps using the copy it has. Format 2 (catalog 2.0.0) made `sponsor` a list; format 1's was one link. Format 3 (catalog 3.0.0) gave authors a record of their own (see [Changing the format](#changing-the-format)). |
| `version` | The catalog's own version (`1.4.0`). Bump it whenever entries change; the app uses it to say what's new. |
| `forever` | The client the catalog was checked against: `gameVersion` (`1.60.1`) and `interface` (`16001`). Updated by hand when the client patches. Kept for apps that don't read `editions` yet, and always the same as `editions.forever`. |
| `editions` | Every edition of the game the catalog covers (see [Editions](#editions)). Added in catalog 3.1.0. |
| `authors` | Every author an entry names, keyed by author ID (see below). |
| `addons` | The entries. |

## Editions
`editions` is keyed by a fixed edition ID, in Battle.net's order. The app takes every edition's names and current Interface from here, so the next Classic expansion renames an edition, or moves its Interface, without an app update. Adding an edition ID is a schema change.

| ID | `name` | `shortName` | `product` | `folder` |
|---|---|---|---|---|
| `retail` | World of Warcraft (Retail) | Retail | `wow` | `_retail_` |
| `classic` | Mists of Pandaria Classic | MoP Classic | `wow_classic` | `_classic_` |
| `anniversary` | Burning Crusade Anniversary | TBC Anniversary | `wow_anniversary` | `_anniversary_` |
| `classic_era` | World of Warcraft Classic | Classic Era | `wow_classic_era` | `_classic_era_` |
| `forever` | WoW: Forever | Forever | `wowf` | `_forever_` |

| Field | What |
|---|---|
| `name` | Battle.net's name, with "(Retail)" added to World of Warcraft. |
| `shortName` | For tight places: labels on rows and the edition switcher. |
| `product` | The product code in Battle.net's `.build.info`. |
| `folder` | The edition's folder in the `World of Warcraft` folder; its AddOns folder is `<folder>\Interface\AddOns`. |
| `gameVersion` | The live client's version (`12.1.5`). |
| `interface` | That version's Interface: major × 10000 + minor × 100 + patch (`12.1.5` is `120105`, `1.60.1` is `16001`). The validator checks the sum. |
| `interfaceSince` | The day the live client moved to this Interface, `YYYY-MM-DD`. An add-on whose latest checked release still lists the previous one stays listed for 60 days from this day, marked "Not updated for 12.1.5 yet", then only under "Include out of date" until a release lists the current one. Installed copies are never affected. |

### Test clients
PTR and beta clients are editions too, with two more fields: `of`, the live edition they belong to, and `kind` (`ptr`, `ptr2` or `beta`). A test client belongs to the edition whose version it runs, not the one its product is named after: the same major version, and the nearest minor when several share it (a PTR on 12.2.0 is Retail's; 1.15 is Classic Era's and 1.60 Forever's). When it moves to another edition's version, `of` changes with it. The validator checks both. A beta that isn't running keeps the version it last had; the app shows a test client only when it's installed.

| ID | `of` today | `name` | `product` | `folder` |
|---|---|---|---|---|
| `retail_ptr` | `retail` | World of Warcraft (Retail) PTR | `wowt` | `_ptr_` |
| `retail_ptr2` | `retail` | World of Warcraft (Retail) PTR 2 | `wowxptr` | `_xptr_` |
| `retail_beta` | `retail` | World of Warcraft (Retail) Beta | `wow_beta` | `_beta_` |
| `classic_ptr` | `classic` | Mists of Pandaria Classic PTR | `wow_classic_ptr` | `_classic_ptr_` |
| `classic_era_ptr` | `anniversary` (it runs 2.5.6) | Burning Crusade Anniversary PTR | `wow_classic_era_ptr` | `_classic_era_ptr_` |
| `classic_beta` | `forever` (it runs 1.60.1) | WoW: Forever Beta | `wow_classic_beta` | `_classic_beta_` |

An entry's `editions` says which editions it supports, keyed by edition ID, each with the `interface` its latest checked release lists for that edition and whether that's the edition's current one (`upToDate`). The catalog's generator writes it; an entry without it is a Forever add-on, as in format 3 before 3.1.0.

```json
"editions": {
  "retail": { "interface": "120105", "upToDate": true },
  "classic_era": { "interface": "11508", "upToDate": false }
}
```

## Checked releases: versions.json
`versions.schema.json` is the format of `versions.json`, which the release check writes: the latest checked release of each add-on for each edition, so the app finds updates without asking GitHub. `addons` is keyed by catalog ID, then by edition ID.

| Field | What |
|---|---|
| `release` | The release as the source names it (a GitHub tag). |
| `file` | The release file's name. |
| `url` | Where it downloads from, `https://` only. |
| `sha256` | The file's SHA-256 in lowercase hex. The app refuses a download that doesn't match. |
| `interface` | The Interface the file's `.toc` lists for this edition. |
| `published` | The day the release was published. |

One file for several editions is listed under each of them, with the same hash and link (the validator checks). A release with one file per edition lists each edition's own file.

```json
{
  "schemaVersion": 1,
  "checked": "2026-11-26T04:00:00Z",
  "addons": {
    "questie": {
      "classic_era": { "release": "v12.1.1", "file": "Questie-v12.1.1.zip", "url": "https://github.com/Questie/Questie/releases/download/v12.1.1/Questie-v12.1.1.zip", "sha256": "…", "interface": "11509", "published": "2026-11-20" },
      "forever": { "release": "v12.1.1", "file": "Questie-v12.1.1.zip", "url": "https://github.com/Questie/Questie/releases/download/v12.1.1/Questie-v12.1.1.zip", "sha256": "…", "interface": "16001", "published": "2026-11-20" }
    }
  }
}
```

## An author
`authors` is keyed by a stable author ID, with the same rules as an add-on's `id`: lowercase words joined by hyphens (`plusmouse`, `nikita-snetkov`), never changed or reused. Every author an entry names has a record, so the app can group their add-ons and give them a page, and every record is used by at least one entry.

| Field | Required | What |
|---|---|---|
| `name` | yes | How the author is shown: one name, the add-on's main author or the owner of its listing on its first source (the Wago or CurseForge author, else the GitHub owner). Not a list of contributors or a group: the validator refuses a comma, a semicolon, "&", "and others" or "contributors". |
| `homepage` | | The author's own site, `https://` only. Not an add-on's page. |
| `profiles` | | Full links to the author's own pages on GitHub, Wago and CurseForge, as each site writes them, at most one per site. The app shows them on the author's page, named by site. |
| `sponsor` | | Every page where the author asks for support, `https://` only, most preferred first. Where they come from: the [README](../README.md#where-entry-fields-come-from). |

```json
"authors": {
  "example-author": {
    "name": "Example Author",
    "homepage": "https://example.com/",
    "profiles": ["https://github.com/example", "https://www.curseforge.com/members/example"],
    "sponsor": ["https://ko-fi.com/example"]
  }
}
```

## An entry
| Field | Required | What |
|---|---|---|
| `id` | yes | Lowercase words joined by hyphens (`baganator`, `wim`). Stable: never changed or reused. The app's library refers to it. |
| `name` | yes | Display name. |
| `summary` | yes | One sentence, in our own words. |
| `sources` | yes | Where to get it, in order of preference (see below). At least one. |
| `folders` | yes | Every top-level AddOns folder it installs. Several-folder add-ons list all of them (Questie, QuestieDB, QuestieTrace); they're always installed, updated and removed together. No paths and no leading dot. |
| `editions` | | The editions it supports and what its latest checked release lists for each (see [Editions](#editions)). Written by the generator. |
| `tocPath` | | The `.toc` to read the Interface from, relative to the zip's top level (`Questie_Forever.toc`, `OpenMap/OpenMap.toc`). Without it, the app looks for `<Folder>_Forever.toc`, `<Folder>-Forever.toc`, `<Folder>_Camelot.toc`, `<Folder>-Camelot.toc`, then `<Folder>.toc` in the first folder (Camelot is the Forever client's own name). |
| `author` | yes | The author's ID in `authors` (`plusmouse`). The author's name, links and support pages live in their record, once for all their add-ons. |
| `tags` | | From the [fixed list](#tags), main tag first (see below). |
| `added` | for new entries | The day the entry joined the catalog, `YYYY-MM-DD` (`2026-11-20`). The app sorts by it. Optional in the format, but on a PR the validator requires it on every new entry. |
| `homepage` | | The add-on's own page, `https://` only, only when it isn't the default. The app's default is the `repository`, else the first source's page. |
| `requires` | | IDs that must be installed too. Browse offers them together. |
| `optional` | | IDs it works with when they're there. Listed, not added. |
| `conflicts` | | IDs it overlaps with. A warning, never a blocker. |
| `notes` | | Anything worth knowing ("the author disabled CurseForge API downloads, so Wago comes first"). |
| `withdrawn` | | Releases the app never offers: `{ "release": "861", "reason": "…" }`. `release` is a GitHub tag, CurseForge file ID or Wago release, as the source names it. |
| `overrides` | | Changes from a release on: `{ "releaseFrom": "900", "folders": [...] }` and/or `tocPath`. Rare. |

## Tags
`tags` come from this list, and the validator refuses any other. **The first tag is the add-on's main one:** the app shows its icon on the add-on's cards when the add-on has no `icon` of its own, and suggests add-ons that share it as related, so put first what the add-on is mainly for. Add more only when they help someone find it (Questie: `quests`, `map`).

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

CurseForge and Wago sources can also say where a player downloads the Forever release by hand, for the app's **Open download page** button. On both sites the newest upload is often for another game flavor, so the front page can lead to the wrong file. Both fields are optional; leave them out when the front page gets it right.

| Field | What |
|---|---|
| `downloadPage` | The files page to open instead of the front page, filtered to the Forever game version where the site allows it. CurseForge: `https://www.curseforge.com/wow/addons/<project>/files` or `/files/all`, with an optional query. Wago: `https://addons.wago.io/addons/<slug>/versions`, with an optional query. The validator refuses any other host or path. |
| `downloadHint` | One short sentence telling the player which file to pick, when the page alone doesn't make it clear ("the file whose name ends in -forever"). |

Regexes use .NET syntax, matched against the file name only.

## Changing the format
- **A new optional field** is a minor catalog `version` (`3.1.0`) in the same format: an app that doesn't know the field ignores it.
- **Removing or changing a field**, or making one required, needs a new `schemaVersion` and a major catalog `version`. An app that doesn't know the new format keeps the last copy it has, so the app that reads it ships first.

## Example
```json
{
  "id": "baganator",
  "name": "Baganator",
  "author": "plusmouse",
  "summary": "Bag and bank replacement with search and sorting.",
  "tags": ["bags"],
  "added": "2026-11-20",
  "sources": [
    { "type": "wago", "slug": "baganator" },
    { "type": "curseforge", "projectId": 914482 }
  ],
  "folders": ["Baganator"],
  "tocPath": "Baganator/Baganator.toc",
  "requires": ["syndicator"]
}
```
