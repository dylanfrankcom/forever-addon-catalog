# Forever Add-on Catalog

The list of add-ons [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) can find and install for WoW Forever.

> **Status:** being set up (phase 1 of [the plan](https://github.com/dylanfrankcom/forever-addon-manager/blob/main/docs/PLAN.md)). Private for now.
>
> Not affiliated with or endorsed by Blizzard Entertainment. Every add-on belongs to its author; entries only say where the author publishes it.

## What's here (once phase 1 lands)
| Path | What |
|---|---|
| `catalog.json` | One entry per add-on: its sources (GitHub, CurseForge, Wago), folders, required and optional add-ons, conflicts, notes and withdrawn releases. |
| `schema/catalog.schema.json` | The format, checked on every PR. |
| `scripts/` | Validation, and later the release checker. Both are .NET file-based programs. |
| `status.json` | Written by the checker job, never by hand: which releases list the Forever Interface (phase 6). |

The full format is in section 2 of the plan.

## Rules
- **The catalog is data, not instructions.** It says where to look. The app still checks every download (hashes, the Forever `.toc`, no executables) and asks before installing.
- **IDs are stable:** they never change and are never reused.
- **No protected add-ons:** no entry may name a `Spoken*` folder or `.spoken-manager`.
