# Forever Add-on Catalog

The list of add-ons [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) can find and install for WoW Forever.

> **Status:** public and still being built. [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) already reads this catalog at start and before each update check, and more add-ons, entry fields and the release checker are on the way.
>
> Not affiliated with or endorsed by Blizzard Entertainment. Every add-on belongs to its author; entries only say where the author publishes it.

## What's here
| Path | What |
|---|---|
| `catalog.json` | One entry per add-on: its sources (GitHub, CurseForge, Wago), folders, required and optional add-ons, conflicts, notes and withdrawn releases. One record per author: their name, homepage, profiles and support pages. |
| `schema/catalog.schema.json` | The format. |
| `.github/workflows/validate.yml` | Runs the validator's self-test and validates `catalog.json` on every push and PR; on a PR, changed entries must come with a version bump. |
| `scripts/` | Validation, and later the release checker. Both are .NET file-based programs. |
| `status.json` | Written by the checker job, never by hand: which releases list the Forever Interface (coming with the catalog checker, app 0.6.0). |
| `versions.json` | Written by the release check, never by hand: the latest checked release of each add-on for each edition, with its hash (coming with the release check). Its format is `schema/versions.schema.json`. |

The full format, with a description of every field, is in [`schema/catalog.schema.json`](schema/catalog.schema.json).

## Rules
- **The catalog is data, not instructions.** It says where to look. The app still checks every download (hashes, the Forever `.toc`, no executables) and asks before installing.
- **IDs are stable:** they never change and are never reused.

## Where entry fields come from
Every value comes from what the add-on's author has published, checked by hand when the entry is added or reviewed. Nothing is guessed: when the author doesn't list something, the field is left out.

- **`author`** (required): who made the add-on, as its pages credit them. Each author has one record in `authors`, shared by all their add-ons, with these fields:
  - **`name`**: how the author is shown.
  - **`homepage`** (optional): the author's own site, when they have one. Not an add-on's page.
  - **`profiles`** (optional): the author's own pages on GitHub, Wago and CurseForge, one per site, linked from their pages there.
  - **`sponsor`** (optional): see below.
- **`added`** (required on new entries): the day the entry joined the catalog. Entries from before the field existed have the date of the commit that first added them.
- **`repository`** (optional): the public GitHub or GitLab repository that holds the add-on's source code, even when the catalog downloads it from CurseForge or Wago. The app marks add-ons whose code is public. Only a repository with the code counts: one that only hosts issues, or a link to a repository that isn't public, is left out. The catalog doesn't record licences: each add-on's repository states its own.
- **`issues`** (optional): where players report problems to the author. This is the repository's issues page when it has issues turned on. Otherwise it's the tracker the author links from their CurseForge or Wago page, if that link works. The app's Report a problem link uses it.
- **`sponsor`** (optional, in the author's record): a list of the `https` pages where people can support the author (Patreon, Ko-fi, Buy Me a Coffee, PayPal, GitHub Sponsors …). The app shows each as its own link, grouped by author. Only pages the author lists themselves are included, never guessed, and a page that's listed under two addresses is included once. They're in this order:
  1. The author's GitHub Sponsors page, when they have one.
  2. The links in the source repo's `FUNDING.yml` (or the owner's `.github` repo's), in its order.
  3. The Donate button on the add-on's CurseForge or Wago page.
  4. Any other support links the author lists on those pages, in the page's order.
- **`icon`** (optional): an `https` link to a small square image (PNG or JPEG) for the add-on, shown on its cards in the app. It comes from the author: their own repository or their add-on page, and only when that site's terms allow showing it in another app. Nothing is drawn or made up here, and no game art. When in doubt it's left out, and the app shows a placeholder.

## For add-on authors
Thanks for making add-ons for Forever. A few things that make life easier for players who use the app:

- **GitHub releases install in one click.** The app installs and updates add-ons straight from a GitHub release. When an add-on is only on CurseForge or Wago, the player downloads the zip from that site themselves and hands it to the app, every update. If you publish there, attaching the same zip to a GitHub release gives players the one-click path. The [BigWigs packager](https://github.com/BigWigsMods/packager) can do it in the same run as your CurseForge and Wago uploads: give it a GitHub token and it creates the release too.
- **List the Forever Interface in your `.toc`.** Only add-ons whose current release lists it are in the catalog. Separate files per game version are fine: a name that says which one is Forever's (ending in `-forever`, say) helps players pick the right file.
- **Suggesting your own add-on** works like any other request: the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form.
- **Your support page comes first.** Entries link to the support pages and issue tracker you list yourself (see `sponsor` and `issues` above). The catalog and the app take no donations, so any support goes to you.
- **Your links live in one place.** Your homepage, your GitHub, Wago and CurseForge profiles and your support pages are kept once, in your author record, for all your add-ons. The app shows them on your author page. To add or change them, open an issue with the **Task** form.
- **Something wrong in your entry** (a moved repo, a new folder): open an issue with the **Task** form.
## Contributing
Know an add-on that works on Forever? Ask for it with the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form. Each entry is checked (the release, its `.toc`, its sources and links) before it's added. Questions are welcome as issues, with the Question form.

Some add-ons work on Forever but can't be listed, for example when their only source has turned off downloads by other apps. They're recorded as issues labelled [`unavailable`](https://github.com/dylanfrankcom/forever-addon-catalog/issues?q=label%3Aunavailable) (there's an [Add-on that can't be listed](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=unavailable.yml) form), each saying what would make it possible. Comments there are welcome when an author changes something. Pull requests from outside the project aren't accepted: see [CONTRIBUTING.md](CONTRIBUTING.md).

The catalog and the app are free and open source, take no donations, and are developed by AI agents (Claude Code) under the maintainer's direction.

## Licence
- **The catalog's data** (`catalog.json`) is licensed under [CC BY-SA 4.0](LICENSE-DATA). Anyone, including other add-on managers, may use and adapt it. Credit this catalog, and share adapted versions under the same licence.
- **Everything else** (the schema, `scripts/` and the workflows) is licensed under the [GNU GPL, version 3 or later](LICENSE). Forks must stay open source under the same licence.

Both come with no warranty and no liability: see sections 15 and 16 of the GPL and section 5 of CC BY-SA 4.0. The add-ons themselves aren't part of this repo. Each one keeps its author's licence.
