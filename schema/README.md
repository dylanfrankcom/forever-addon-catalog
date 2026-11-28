# Catalog format (schema 4)

`catalog.schema.json` is the format of `catalog.json`, as JSON Schema 2020-12. `catalog.json` is written by `scripts/generate.cs` (see [Where the catalog comes from](#where-the-catalog-comes-from)), never by hand. Rules the schema can't express are checked by `scripts/validate.cs`:
- IDs are unique.
- `requires`, `optional` and `conflicts` name existing IDs.
- Every entry's `author` is in `authors`, and every author is the author of at least one entry.
- An author's `name` is one name, and their `profiles` are on GitHub, Wago or CurseForge, at most one per site.
- No two entries claim the same folder.
- Each edition's `interface` is its `gameVersion`'s, a PTR's `of` is a live edition, every edition an entry lists is in `editions`, its `upToDate` is right, and its `released` is a real day, not in the future. No edition lists a `.toc` suffix twice, ignoring case.
- `added` is a real day and not in the future.
- On a PR, compared with the base branch: changed entries or authors come with a `version` bump, and new entries have `added`.

## Top level
| Field | What |
|---|---|
| `schemaVersion` | Always `4` for this format. The app refuses a catalog whose `schemaVersion` it doesn't know and keeps using the copy it has. Format 2 (catalog 2.0.0) made `sponsor` a list; format 1's was one link. Format 3 (catalog 3.0.0) gave authors a record of their own. Format 4 (catalog 4.0.0) is the generated catalog: the `forever` block is gone (`editions.forever` replaces it), `editions` is required, and an entry's `homepage`, `repository` and `issues` moved into `links` (see [Changing the format](#changing-the-format)). |
| `version` | The catalog's own version: the UTC date it was built and that day's build number (`2026.11.28.3`). The generator changes it whenever entries change; the app uses it to say what's new and how fresh the catalog is. Catalogs before it used a semantic version (`4.20.0`), which counts as earlier. |
| `editions` | Every edition of the game the catalog covers (see [Editions](#editions)). Added in catalog 3.1.0, required from format 4. |
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
| `tocSuffixes` | The `.toc` suffixes the edition loads, most preferred first (see [How an edition reads an add-on](#how-an-edition-reads-an-add-on)). Added in catalog 3.4.0. |
| `gameTypes` | The game type names its `[AllowLoadGameType]` / `[ExcludeLoadGameType]` load conditions use. Added in catalog 3.4.0. |
| `packagerFlavors` | The flavors the common packager's `release.json` gives this edition's files. Added in catalog 3.4.0. |
| `fileWords` | Words in a release file's name that mark it as this edition's. Added in catalog 3.4.0. |

### How an edition reads an add-on
The app and this repo's release checker both follow these fields, so they never disagree about which file an edition reads. When Blizzard adds an expansion or a PTR moves to another version, only the edition's entry changes.

- **Which `.toc`:** for an add-on folder `<F>`, the edition reads `<F><suffix>.toc` for the first of its `tocSuffixes` that exists, in order, and otherwise the plain `<F>.toc`. File names ignore case, so the validator refuses two suffixes that differ only in case. Every suffix starts with `_` or `-`.
- **Load conditions:** a `## Key: value` line may end with `[AllowLoadGameType a, b]` or `[ExcludeLoadGameType a, b]`. The line applies to the edition only when every `Allow` condition names one of its `gameTypes` and no `Exclude` condition does; lines that don't apply are ignored. For a key given more than once, the last line that applies wins.
- **The Interface line:** `## Interface:` may list several values, separated by commas, spaces or tabs (`## Interface: 11509, 50504, 16001`). The add-on supports the edition when one of them is the edition's `interface`.
- **Which file in a release:** when the release has the common packager's `release.json`, the edition's file is the one whose flavor is in its `packagerFlavors`. Otherwise, a release with one file per edition is matched by `fileWords` in the file name, and a release with a single file uses that file for every edition.
- **PTRs** list the values of the version they run: PTR (Classic Era) on 2.5.6 reads `_TBC` files, like TBC Anniversary.
- **Folders made for other editions:** a release made for every edition can carry folders only some of them load (Deadly Boss Mods' Midnight modules list only Retail's Interface). For an edition, a folder whose `.toc` lists only Interfaces that other editions accept is left out: the edition's `folders` lists the ones it installs, and the app installs only those. Never the add-on's main folder, never a folder one that stays requires (the release then fails for that edition), and never a folder whose Interface no edition accepts (that's out of date, and fails as before). Left-out folders get every safety check all the same.

| Edition | `tocSuffixes` | `gameTypes` | `packagerFlavors` | `fileWords` |
|---|---|---|---|---|
| `retail`, `retail_ptr`, `retail_ptr2` | `_Mainline`, `-Mainline` | `mainline`, `standard` | `mainline`, `retail` | `mainline`, `retail` |
| `classic`, `classic_ptr` | `_Mists`, `_Classic`, `-Mists` | `mists` | `mists` | `mists`, `mop` |
| `anniversary`, `classic_era_ptr` | `_TBC`, `_BCC`, `_Classic`, `-TBC`, `-BCC` | `tbc` | `bcc`, `tbc` | `tbc`, `bcc`, `anniversary` |
| `classic_era` | `_Vanilla`, `_Classic`, `-Vanilla`, `-Classic` | `vanilla`, `classic` | `classic`, `vanilla` | `vanilla`, `era` |
| `forever` | `_Forever`, `-Forever`, `_Camelot`, `-Camelot` | `camelot` | `camelot`, `forever` | `forever` |

"Camelot" is the Forever client's own name: add-ons built with the common packager ship `_Camelot.toc` files with Forever's Interface. `_Classic` is read by every Classic edition; `-Classic` only by Classic Era.

### PTRs
The PTRs Battle.net offers are editions too, with one more field: `of`, the live edition Battle.net files the PTR under. That stays so even when the PTR runs another edition's version (today PTR (Classic Era) runs 2.5.6); the app checks add-ons against the version a PTR runs. The validator checks that `of` is a live edition. Betas aren't listed: Battle.net shows them only to invited accounts. The app manages PTRs only when the player turns them on.

| ID | `of` | `name` | `shortName` | `product` | `folder` |
|---|---|---|---|---|---|
| `classic_ptr` | `classic` | PTR (MoP Classic) | PTR MoP Classic | `wow_classic_ptr` | `_classic_ptr_` |
| `classic_era_ptr` | `classic_era` | PTR (Classic Era) | PTR Classic Era | `wow_classic_era_ptr` | `_classic_era_ptr_` |
| `retail_ptr` | `retail` | PTR (Midnight 12.1.7) | PTR 12.1.7 | `wowt` | `_ptr_` |
| `retail_ptr2` | `retail` | PTR (Midnight 12.1.5) | PTR 12.1.5 | `wowxptr` | `_xptr_` |

Battle.net puts the expansion and version in a Retail PTR's name, so the name changes with each patch: it's updated here with its `gameVersion`.

## The wanted list: wanted.json
`wanted.schema.json` is the format of `wanted.json`, which `scripts/reports.cs` (`wanted`) writes with each catalog build: what the catalog is missing, for an add-on manager to show its players, so they know where a report helps. It's rewritten from the reports players sent (see [CONTRIBUTING.md](../CONTRIBUTING.md#version-reports-from-the-app)); an item leaves once its change has merged.

| Field | What |
|---|---|
| `schemaVersion` | Always `1` for this format. |
| `items` | The items, add-ons first, then by ID. |

Every item has `kind`, `needs` and `progress`: how far it has come toward a decision under the rule for its kind, rounded down to `0`, `25`, `50`, `75` or `100`, where `100` means it's being checked. It combines how many players reported it and over how long; the counts and the rules aren't published.

| `kind` | Fields | `needs` |
|---|---|---|
| `addon` | `source` (`curseforge` or `wago`), `project` (the CurseForge project ID or the Wago slug), `name`, `editions` | `listing`: players suggested it and it isn't listed. |
| `details` | `id`, `name` | Any of `summary` and `languages` (the entry has none), and `toc` (players' `.toc` details are waiting). For entries listed by hand. |
| `version` | `id`, `name`, `source`, `edition` | `version` (no confirmed version for that edition yet) or `newer-version` (players reported a newer one). For entries listed by hand. |

## Checked releases: versions.json
`versions.schema.json` is the format of `versions.json`, which `scripts/generate.cs` writes from the release check, beside `catalog.json`: the latest checked release of each add-on for each edition, so the app finds updates without asking GitHub. `addons` is keyed by catalog ID, then by edition ID.

| Field | What |
|---|---|
| `release` | The release as the source names it (a GitHub tag). |
| `file` | The release file's name. |
| `url` | Where it downloads from, `https://` only. |
| `size` | The file's size in bytes, as GitHub lists it. |
| `sha256` | The file's SHA-256 in lowercase hex. The app refuses a download that doesn't match. |
| `interface` | The Interface the file's `.toc` lists for this edition. |
| `upToDate` | Whether that's the edition's current Interface; false for a release that isn't updated yet. |
| `folders` | The folders this edition installs, when some of the release's are left out for it ([folders made for other editions](#how-an-edition-reads-an-add-on)). Left out, it's all of them. |
| `published` | The day the release was published. |

Only releases that passed the release check, or that it found not updated yet, are listed, after the check's 6-hour hold for new releases. `checked` is when the run that last changed the file finished: a run that finds nothing new doesn't rewrite it, so its ETag stays valid. A run that would drop more than half the listed add-ons writes neither file and keeps the last good ones. One file for several editions is listed under each of them, with the same hash and link (the validator checks). A release with one file per edition lists each edition's own file.

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

## Add-on details: addons/&lt;id&gt;.json
One file per listed add-on released on GitHub, written by `scripts/details.cs`, never by hand (format: `schema/details.schema.json`). The app fetches it, with its ETag, only when the player opens the add-on, and shows `catalog.json`'s data while it loads or when it can't be fetched. An add-on with no GitHub source has none.

| Field | What |
|---|---|
| `schemaVersion` | Always `1`. |
| `id`, `repo` | The catalog ID (the file's name) and the repo it was read from. |
| `about` | The README, as Markdown. `aboutTruncated` is `true` when it was cut at the size cap. |
| `releases` | The five latest stable releases, newest first: `tag`, `name` (when it isn't the tag), `published` (UTC day), `notes` (Markdown; `truncated` when cut). |
| `changelog` | The repo's CHANGELOG, as Markdown, only when no release has notes (`changelogTruncated` when cut). |
| `screenshots` | Up to 12 images on GitHub (`raw.githubusercontent.com`, `user-images.githubusercontent.com`, `github.com/user-attachments/assets`), from the README and a `screenshots` folder. Linked, never copied. |
| `stats` | `stars`; `openIssues` (left out when issues are off); `contributors` (left out when GitHub won't say); `lastRelease` and `releaseDates`, the days of the 50 latest stable releases. |
| `languages` | Locales the main `.toc` has a `Title-xxXX` or `Notes-xxXX` for. |
| `technical` | `folders` (as in `catalog.json`), `editions` (every edition its `.toc` files list an Interface for, checked or not), and from the main `.toc`: `requires`, `optional`, `savedVariables`, `savedVariablesPerCharacter`. |

**Markdown is text only:** no raw HTML outside code, links are absolute http(s), mailto or `#` anchors, and images are https. Images can be on any host; an app may choose to load only those on GitHub. The validator checks all of it, that each file is named after its ID, and that the ID is listed in `catalog.json`.

## An author
`authors` is keyed by a stable author ID, with the same rules as an add-on's `id`: lowercase words joined by hyphens (`plusmouse`, `nikita-snetkov`), never changed or reused. Every author an entry names has a record, so the app can group their add-ons and give them a page, and every record is used by at least one entry.

| Field | Required | What |
|---|---|---|
| `name` | yes | How the author is shown: one name, the add-on's main author or the owner of its listing on its first source (the Wago or CurseForge author, else the GitHub owner). Not a list of contributors or a group: the validator refuses a comma, a semicolon, "&", "and others" or "contributors". |
| `homepage` | | The author's own site, `https://` only. Not an add-on's page. |
| `profiles` | | Full links to the author's own pages on GitHub, Wago and CurseForge, as each site writes them, at most one per site. The app shows them on the author's page, named by site. |
| `sponsor` | | Every page where the author asks for support, `https://` only, most preferred first. How they're chosen: [Support links](#support-links). |

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

### Support links
A wrong link would send a player's money to the wrong person, so `sponsor` lists only pages the author lists themselves, never guessed, and each is checked:
- **From the author's own repositories only:** each listed repository's `FUNDING.yml` (or the owner's `.github` repository's), as GitHub reads it. Never from a fork or another account's repository.
- **GitHub Sponsors:** only an account that has a Sponsors listing and owns the repository, or is a public member of the organisation that does. It comes first.
- **Everything else:** in the file's order, `https` only (an `http://` link, or one written without a scheme like `paypal.me/name`, is read as the same page over `https`). A shortened link (bit.ly and the like) is listed as the page it leads to, found from the shortener's redirect, never as the short link. A page listed under two addresses is included once.
- **Reported:** every refused link, and every link that appears, changes or goes away, is listed in the generator's report for the maintainer to look at.

Authors written by hand (`overrides/_authors.json`) keep their own list, which can also include the Donate button on their CurseForge or Wago page.

## An entry
| Field | Required | What |
|---|---|---|
| `id` | yes | Lowercase words joined by hyphens (`baganator`, `wim`). Stable: never changed or reused. The app's library refers to it. A new entry's ID is its main folder in lowercase, with anything but letters and digits turned into hyphens (`DBM-Core` → `dbm-core`, `WeakAuras` → `weakauras`); the owner is added when two add-ons share a folder name. |
| `name` | yes | Display name: the main `.toc`'s Title, cleaned of colour codes, textures and version numbers, with a leading tag in angle brackets turned into plain words (`<DBM Media> Countpack HotS` gives "DBM Media Countpack HotS"), else the main folder. |
| `languages` | | The WoW locales (`enUS`, `deDE`, `zhCN` …) the add-on's text supports, so apps can hide add-ons a player can't read: the translation files its release ships outside its libraries (`Locales/deDE.lua`, or Deadly Boss Mods' `localization.de.lua`; `enGB` counts as `enUS`), and its `.toc`'s translated Notes. When it has translations and its name and summary are in Latin script, `enUS` too, as the base text they're made from. With neither, the script of its name and summary when it isn't Latin: Chinese gives `zhCN` and `zhTW`, Korean `koKR`, Cyrillic `ruRU`. Left out when unknown, which never means English. An override can give its own. |
| `otherNames` | | Other names the add-on is known by, for search only, never shown as its name: the repository's name as words (`Max-Camera-Distance` and `MaxCameraDistance` give "Max Camera Distance"), without a leading or trailing "wow" or "addon", and left out when `name` already holds it once case and symbols are ignored, and for a repository that releases several add-ons. An override can give its own list. |
| `summary` | yes | One line, from the author: the main `.toc`'s Notes, else the repository's description, else its README's first sentence. Cleaned like `name` and cut at a word to about 120 characters. An add-on with none of these isn't listed unless an override gives one. |
| `summaries` | | The summary in other languages, from the `.toc`'s `Notes-deDE` lines and the like, keyed by the game's locale. |
| `sources` | yes | Where to get it, in order of preference (see below). At least one. |
| `folders` | yes | Every top-level AddOns folder it installs. Several-folder add-ons list all of them (Questie, QuestieDB, QuestieTrace); they're always installed, updated and removed together. Letters, digits, `_`, `.`, `-`, `!` and inner spaces: a leading `!` is how an add-on loads before the others (`!BugGrabber`). No paths, no leading dot, and no trailing space or dot. |
| `editions` | | The editions it supports and what its latest checked release lists for each (see [Editions](#editions)): its `interface`, whether that's `upToDate`, `released`, the day (UTC) the release was published, left out when it isn't known, and `folders`, the ones the edition installs when some of the entry's are [made for other editions](#how-an-edition-reads-an-add-on) (left out, it's all of `folders`). Written by the generator; for an add-on that isn't released on GitHub, from the editions its override lists, which the maintainer checked by hand (`upToDate` is still worked out from the edition's current Interface). |
| `tocPath` | | The `.toc` to read the Interface from, relative to the zip's top level (`Questie_Forever.toc`, `OpenMap/OpenMap.toc`). Without it, the app looks for `<Folder>_Forever.toc`, `<Folder>-Forever.toc`, `<Folder>_Camelot.toc`, `<Folder>-Camelot.toc`, then `<Folder>.toc` in the first folder (Camelot is the Forever client's own name). |
| `author` | yes | The author's ID in `authors` (`plusmouse`). The author's name, links and support pages live in their record, once for all their add-ons. A curated record (`overrides/_authors.json`) when there is one, else one made from the repo's GitHub owner. |
| `tags` | | From the [fixed list](#tags), main tag first (see below): the `.toc`'s Category, then the repo's topics, at most 3. When the author set neither (and no override does), from [`tags.json`](../tags.json): chosen from the add-on's name, summary, folders and README, marked `from.tags: "inferred"`. |
| `icon` | | A small square PNG: the main `.toc`'s IconTexture when it's one of the add-on's own files (never game art), converted to PNG and kept in this repo's `icons/`. Left out otherwise; the app shows its main tag's icon. |
| `added` | for new entries | The day the entry joined the catalog, `YYYY-MM-DD` (`2026-11-20`). The app sorts by it. Optional in the format, but on a PR the validator requires it on every new entry. |
| `links` | | The add-on's own pages: `homepage` (only when it isn't the default: the app's default is the `repository`, else the first source's page), `repository` (the public GitHub or GitLab repo with its code) and `issues` (where players report problems). All `https://`. The generator fills `repository` and `issues` from the GitHub repo. Also `curseforge`, `wago` and `wowinterface`: the add-on's pages on those sites, from its main `.toc`'s `X-Curse-Project-ID`, `X-Wago-ID` and `X-WoWI-ID`. Those IDs are taken as the author states them (`from` says `toc`), never confirmed with the sites, which can't be asked automatically. Instead: none is taken from a fork, and an ID two listed add-ons both claim goes to neither, unless an override confirms the right one (`from` then says `override`). The CurseForge and Wago sources an override lists (every add-on listed by hand) give the entry those links (`from` says `source`), in place of its `.toc`'s, and count as claims a `.toc` can't share. An override sets a link, or removes one with `null` (a player's "Wrong link" report). Nothing in the catalog asks CurseForge, Wago or WoWInterface anything: a link that no longer leads anywhere is reported by players. |
| `downloads` | | How often it's downloaded, for lists, sorting and its page: `total` (every release's files), `latest` (GitHub's Latest release's files) and `read`, the day (UTC) the counts were read. Only GitHub's own public download counts for release files, read weekly by discovery; the app tracks nothing (its installs download from GitHub, so they're already counted). GitHub doesn't count its source-code archives. Left out for add-ons not released on GitHub. |
| `trust` | | What players can go on to trust it, from public data only: see [Trust signals](#trust-signals). |
| `requires` | | IDs that must be installed too. Browse offers them together. |
| `requiresFolders` | | Required add-ons that no entry ships, by folder name, from the `.toc`'s `## Dependencies` or `## RequiredDeps` (`DBM-Core` for the DBM packs), so apps can say what's missing. Blizzard's own `Blizzard_` folders are left out. A folder an entry ships goes in `requires` instead. |
| `optional` | | IDs it works with when they're there. Listed, not added. |
| `conflicts` | | IDs it overlaps with. A warning, never a blocker. |
| `notes` | | Anything worth knowing ("the author disabled CurseForge API downloads, so Wago comes first"). |
| `linkOnly` | | Link only, at the author's request: `{ "url": "https://..." }`, their chosen download page. The entry stays listed, but the app sends players to that page and never downloads the file itself, and it's left out of `versions.json`. From the add-on's override. |
| `ownApp` | | The add-on's own app, when its author recommends one for installing and updating it (Spoken Manager for Spoken): `name`, `url` (its page, `https`) and an optional `marker`, a folder or file name the app leaves in AddOns while it's in use (`.spoken-manager`), a plain name with no path separators or wildcards. An optional `folders` lists every folder the app installs, packs the catalog doesn't list included: folder names, or a name ending in `*` for every folder that starts with it (`Spoken*`); left out, it's the entry's own `folders`. A pattern may not cover another entry's folder. The app recommends it, and leaves the add-on and those folders to it while the marker is there, so two tools never change the same folders. From the add-on's override. |
| `githubWithdrawn` | | The GitHub repo the catalog had linked this add-on to was withdrawn because the link stopped holding: `{ "repo": "Owner/Name", "reason": "archived", "date": "2026-11-28" }`. `reason` is `archived` (the repo is archived or gone), `new-owner` (it moved to another owner or name) or `not-named` (its `.toc` no longer names the add-on and its release isn't the confirmed file). An app warns players who installed the add-on from that repo. Written by the reports workflow into the add-on's override, and cleared when it's linked again. |
| `withdrawn` | | Releases the app never offers: `{ "release": "861", "reason": "…" }`. `release` is a GitHub tag, CurseForge file ID or Wago release, as the source names it. |
| `overrides` | | Changes from a release on: `{ "releaseFrom": "900", "folders": [...] }` and/or `tocPath`. Rare. |
| `from` | | Where the generator took each value from (`name`: `toc-title`, `folder` or `override`; `summary`: `toc-notes`, `repo-description`, `readme` or `override`; and so on), so a wrong one is easy to trace. `"entry": "override"` marks an add-on that isn't released on GitHub, whose whole entry is an override. |

## Trust signals
`trust` is written by the generator for add-ons released on GitHub, from public data only. Nothing here keeps an add-on out of the catalog: every add-on whose release passes the check is listed, and the signals decide how the app shows and handles it.

| Field | What |
|---|---|
| `built` | The workflow in the add-on's own repo that built the file, from GitHub's build record for it (an [artifact attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations), as `actions/attest-build-provenance` writes it), for every listed edition's file. A hand-uploaded zip has none. The catalog reads the workflow the record names; it doesn't verify the record's signature (`gh attestation verify` does). |
| `repoSince`, `ownerSince` | The days (UTC) the repo and its owner's GitHub account were made. |
| `releases`, `stars` | Its number of GitHub releases, and its stars. Download counts are in `downloads`. |
| `ownerAddons` | How many other listed add-ons come from the same GitHub owner. |
| `marker` | `yes` or `no`, from the author's `## X-Forever-Catalog:` line in the `.toc`. |
| `badges` | `built`: `built` is set. `new`: its first release came out less than 30 days ago (known only when it has at most 100 releases). |
| `warnings` | Warning signs, also listed in the generator's report for the maintainer: `hidden-code` (a `.lua` file that runs text as code with `loadstring` or `load` and also holds a long run of base64, `\ddd` escapes or a long `string.char` list), `unexpected-files` (archives or native code, which an add-on never needs; programs already fail the check), `new-owner` (the owner's account is less than 90 days old), `single-release` (it has one release). |
| `careful` | True when it has any warning sign. The app holds its new releases longer before offering them and leaves it out of Update all until it has none. |

Cross-references count too: the `.toc`'s CurseForge, Wago and WoWInterface pages are in `links`. A wrong signal is corrected the usual way: an issue, then an override or a fix to the rule.

## Tags
`tags` come from this list, and the validator refuses any other. **The first tag is the add-on's main one:** the app shows its icon on the add-on's cards when the add-on has no `icon` of its own, and suggests add-ons that share it as related, so put first what the add-on is mainly for. Add more only when they help someone find it (Questie: `quests`, `map`).

**Inferred tags.** Most add-ons set no Category or topics, so [`tags.json`](../tags.json) ([`tags.schema.json`](tags.schema.json)) keeps tags chosen for them by reading what each add-on is, not by matching words ("Accountant" isn't `class`). The generator uses a row only for an entry with no tags from its author or an override, so the author's own always win, and marks them `from.tags: "inferred"`. Each row keeps a `fingerprint` of the name, summary and folders they were chosen from (the first 16 hex digits of their SHA-256, one per line), so they're chosen again when those change; an empty list means none fit. The **tags** workflow (`scripts/tags.cs`) chooses them for new and changed entries and proposes them in a pull request the maintainer reviews.

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
| `github` | `repo` (`owner/name`), `assetPattern` (regex the asset's file name must match), optional `excludePattern` and `tagPrefix`. The release GitHub flags as **Latest** is used, never a pre-release. With `tagPrefix`, for a repo that releases several add-ons, only releases whose tag starts with it (case-sensitive, `"spoken/"`) are this add-on's: the newest stable one by publish date among the repo's newest 100 releases, never a draft or pre-release, and its version is the tag after the last `/` (`spoken/v3.13.1` is `v3.13.1`). |
| `curseforge` | `projectId`. The newest stable (release-type) file that lists `editions.forever.gameVersion`. |
| `wago` | `slug`, as in `https://addons.wago.io/addons/<slug>`. Newest stable release. |

CurseForge and Wago sources can also say where a player downloads the Forever release by hand, for the app's **Open download page** button. On both sites the newest upload is often for another game flavor, so the front page can lead to the wrong file. Both fields are optional; leave them out when the front page gets it right.

| Field | What |
|---|---|
| `downloadPage` | The files page to open instead of the front page, filtered to the Forever game version where the site allows it. CurseForge: `https://www.curseforge.com/wow/addons/<project>/files` or `/files/all`, with an optional query. Wago: `https://addons.wago.io/addons/<slug>/versions`, with an optional query. The validator refuses any other host or path. |
| `downloadHint` | One short sentence telling the player which file to pick, when the page alone doesn't make it clear ("the file whose name ends in -forever"). |

CurseForge and Wago sources can also carry `confirmed`: versions players reported downloading and the maintainer confirmed, keyed by edition ID, each with `version` (as the `.toc` gives it), `sha256` (of the zip, lowercase hex) and `date` (the day it was confirmed). It's only for editions the entry lists. The generator copies it from [`confirmed.json`](../confirmed.json) ([`confirmed.schema.json`](confirmed.schema.json)), which changes only through the reports workflow's reviewed pull request; an override can't set it. An app can treat a download whose hash matches as that version, and a newer confirmed version as an update.

Regexes use .NET syntax, matched against the file name only.

## Where the catalog comes from
`scripts/generate.cs` writes `catalog.json` from three things:
1. **The release check** (`checks.json`, from `scripts/check.cs`): every add-on whose latest GitHub release passed for the catalog's scope, the Forever edition for now. Only that release's folders, `.toc` and editions are used, so nothing is listed that wasn't downloaded and checked.
2. **Discovery** (`candidates.json`, from `scripts/discover.cs`): the repo's description, topics and `.toc` fields (Title, Notes, Author, Category, IconTexture, dependencies), taken from the `.toc` of the main folder the release check found: as the release itself has it when the check recorded it, else the repo's, whichever `.toc` discovery picked first.
3. **Overrides** (`overrides/<id>.json`, format `override.schema.json`): hand corrections for one entry. With `"repo"`, they correct that GitHub repo's generated entry and apply only while its release passes the check; without it, the file is the whole entry of an add-on that isn't released on GitHub. A repo that releases several add-ons gets one override for each, with `"repo"` and its `"tagPrefix"`: the check then reads each prefix's release instead of the repo's Latest, and the repo is listed only through those entries (they carry no CurseForge project or download counts, which are the whole repo's). An override with `"partOf": "<id>"` makes its add-on part of that entry instead of an entry of its own: its release is still checked, and its folders are added to the entry's `folders`. That entry must be link only, since the app installs neither (a part of any other entry is left out and reported); Spoken's voice packs are parts of Spoken this way. Without `"repo"`, an override also lists `editions`: each edition the maintainer checked the release for by hand, with the `interface` it lists (and `folders` or `released` when needed). Any edition the catalog covers can be listed; one it doesn't is left out and reported. With `"repo"`, editions always come from the checked release, except on an add-on listed by hand that the reports workflow linked to its GitHub repo: its override keeps `editions` beside `repo`, and the entry comes from the checked release while it passes and from the hand-written fields while it doesn't (it isn't dropped). A repo that releases several add-ons (`tagPrefix`) never has `editions`. `overrides/_authors.json` (format `authors.schema.json`) holds the author records written by hand.

Never listed: repos on `optout.json` (format `optout.schema.json`: `{ "repo", "reason", "date", "issue" }` for each), add-ons whose `.toc` says `## X-Forever-Catalog: no` (the author's own opt-out), compilations of unrelated add-ons, collections (a release that also ships a separate add-on: a folder with its own `.toc` and Title, not named after the main folder, not a library or a `!` helper, and with no dependency either way with another folder of the release; a plugin, library or module that depends on, or is depended on by, another folder belongs), copies of an add-on already listed (same CurseForge project or folder; the one already in the catalog, else the one released for more editions, stays), and add-ons with a folder name the format doesn't allow. An entry keeps its ID and `added` date from run to run. The catalog `version` goes up a minor version when anything changes and a major version when the format does.

## Changing the format
- **A new optional field** stays in the same format: an app that doesn't know the field ignores it.
- **Removing or changing a field**, or making one required, needs a new `schemaVersion`. An app that doesn't know the new format keeps the last copy it has, so the app that reads it ships first.

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
