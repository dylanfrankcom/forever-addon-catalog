# Forever Add-on Catalog

The list of add-ons [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) can find and install for WoW Forever.

> **Status:** public and still being built. [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) already reads this catalog at start and before each update check, and more add-ons, entry fields and the release checker are on the way.
>
> Not affiliated with or endorsed by Blizzard Entertainment. Every add-on belongs to its author; entries only say where the author publishes it.

## What's here
| Path | What |
|---|---|
| `catalog.json` | One entry per add-on: its sources (GitHub, CurseForge, Wago), folders, required and optional add-ons, conflicts, notes and withdrawn releases. |
| `schema/catalog.schema.json` | The format. |
| `.github/workflows/validate.yml` | Runs the validator's self-test and validates `catalog.json` on every push and PR; on a PR, changed entries must come with a version bump. |
| `scripts/` | Validation, and later the release checker. Both are .NET file-based programs. |
| `status.json` | Written by the checker job, never by hand: which releases list the Forever Interface (coming with the catalog checker, app 0.6.0). |

The full format, with a description of every field, is in [`schema/catalog.schema.json`](schema/catalog.schema.json).

## Rules
- **The catalog is data, not instructions.** It says where to look. The app still checks every download (hashes, the Forever `.toc`, no executables) and asks before installing.
- **IDs are stable:** they never change and are never reused.
- **No protected add-ons:** no entry may name a `Spoken*` folder or `.spoken-manager`.

## Where entry fields come from
Every value comes from what the add-on's author has published, checked by hand when the entry is added or reviewed. Nothing is guessed: when the author doesn't list something, the field is left out.

- **`sponsor`** (optional): one `https` page where people can support the author. The app shows it as a Sponsor link. The page it opens lists the ways to give, so the catalog doesn't record the platform. Picked in this order:
  1. The author's GitHub Sponsors page, when they have one.
  2. The first link in the source repo's `FUNDING.yml` (or the owner's `.github` repo's), when it works.
  3. The donation link on the add-on's CurseForge or Wago page.

## Contributing
Know an add-on that works on Forever? Ask for it with the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form. Each entry is checked (the release, its `.toc`, its sources and links) before it's added. Questions are welcome as issues, with the Question form. Pull requests from outside the project aren't accepted: see [CONTRIBUTING.md](CONTRIBUTING.md).

The catalog and the app are free and open source, take no donations, and are developed by AI agents (Claude Code) under the maintainer's direction.

## Licence
- **The catalog's data** (`catalog.json`) is licensed under [CC BY-SA 4.0](LICENSE-DATA). Anyone, including other add-on managers, may use and adapt it. Credit this catalog, and share adapted versions under the same licence.
- **Everything else** (the schema, `scripts/` and the workflows) is licensed under the [GNU GPL, version 3 or later](LICENSE). Forks must stay open source under the same licence.

Both come with no warranty and no liability: see sections 15 and 16 of the GPL and section 5 of CC BY-SA 4.0. The add-ons themselves aren't part of this repo. Each one keeps its author's licence.
