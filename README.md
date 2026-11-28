# Forever Add-on Catalog

[![Validate](https://github.com/dylanfrankcom/forever-addon-catalog/actions/workflows/validate.yml/badge.svg)](https://github.com/dylanfrankcom/forever-addon-catalog/actions/workflows/validate.yml)
[![Data: CC BY-SA 4.0](https://img.shields.io/badge/data-CC%20BY--SA%204.0-lightgrey)](LICENSE-DATA)
[![Code: GPL-3.0-or-later](https://img.shields.io/badge/code-GPL--3.0--or--later-blue)](LICENSE)

**An open catalog of World of Warcraft add-ons, found and checked from their authors' own releases.**

About 1,370 add-ons for WoW Forever, each one downloaded and inspected before it's listed. [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) uses it to find, install and update add-ons, and any other add-on manager is welcome to use it too.

- 🔍 **Found automatically.** Add-ons released on GitHub are found by their `.toc` files, so nobody has to submit them.
- ✅ **Checked before listed.** Every release is downloaded and inspected the way the app checks a file before installing it.
- ❤️ **Authors first.** Names, links and support pages come from what each author publishes, and installs count toward their own download numbers.
- 📖 **Open data.** One JSON file, free to use under CC BY-SA 4.0.

> Not affiliated with or endorsed by Blizzard Entertainment. Every add-on belongs to its author; the catalog only says where the author publishes it.

## For players
You don't need anything from this repository: [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) reads the catalog for you. What it gives you:

- **Files that were checked.** Each listed release was downloaded and inspected: nothing outside the add-on's folders, no programs, and a `.toc` that lists your edition's Interface. Its size and SHA-256 are recorded, and the app checks them again when it installs.
- **Signs you can trust an add-on.** Each entry notes whether the add-on's own repository built its release (GitHub's build record), how long the repository and its author's account have been around, and any warning signs. A warning sign doesn't keep an add-on out; the app takes more care with it instead.
- **Ways to support the author.** Support links come only from the author's own repositories and are checked before they're listed. The catalog takes no money itself.

**Missing an add-on?** Ask for it with the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form.

**Version reports.** For add-ons from CurseForge or Wago, the app can, if you agree, report the version you downloaded so the catalog can confirm it. Nothing about you or your PC is sent: see [what's sent and how reports are confirmed](CONTRIBUTING.md#version-reports-from-the-app).

## For add-on authors
Thanks for making add-ons for Forever.

### How listing works
Your add-on is listed when its latest GitHub release lists the Forever Interface and passes the check. Its name, summary and links come from what you published, and the app installs and updates it straight from your GitHub release. Add-ons that are only on CurseForge or Wago can be listed too: players then download the zip there and hand it to the app.

Only your own repository is ever listed. Copies and mirrors of your add-on uploaded by someone else are not.

### Your choice: listed, link only, or not at all
- **Opt out:** add `## X-Forever-Catalog: no` to your `.toc`, or open a [Remove my add-on, or make it link only](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=author-choice.yml) issue from the account that owns the repository. Removed add-ons are never listed again.
- **Link only:** the same form. Your add-on stays listed, but the app never downloads it: players get a link to the page you choose.
- **Your own app:** if you make an app that installs your add-on, tell us with the **Task** form. The app recommends it, and leaves your add-on to it while it's in use.
- **Opt in:** nothing is needed. `## X-Forever-Catalog: yes` in your `.toc` says you're happy to be listed, and the app shows it as "Its author marked it for this catalog". A `wow-forever` topic on your repository helps the catalog find it, but isn't read as that.

### Tips
- **Publish a GitHub release.** That's what gives players one-click installs and updates. The [BigWigs packager](https://github.com/BigWigsMods/packager) can create one in the same run as your CurseForge and Wago uploads.
- **List the Forever Interface in your `.toc`.** If you ship separate files per game version, a name that says which one is Forever's (ending in `-forever`, say) helps the right one get picked.
- **Build your release in a workflow and [attest it](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)** to earn the "Built by its own repo" badge.
- **Several add-ons in one repository?** Give each its own tag prefix (`addon/v1.2.0`, `pack/v2.0.0`) and let us know with the **Task** form.
- **Something to change** in your entry or your author links (homepage, profiles, support pages)? Open an issue with the **Task** form.

## For other add-on managers
The catalog is plain JSON, and you're welcome to build on it.

| File | What |
|---|---|
| [`catalog.json`](catalog.json) | Every add-on: its sources (GitHub, CurseForge, Wago), folders, editions, dependencies, trust signals and author. |
| [`versions.json`](versions.json) | Each add-on's latest checked release for each edition, with its file, size and SHA-256. |
| [`addons/`](addons) | A details file per add-on: its README, recent release notes, screenshots and stats. |
| [`icons/`](icons) | Each add-on's own icon, as PNG. |

Every field is described in [`schema/README.md`](schema/README.md), and the JSON Schemas are in [`schema/`](schema). IDs are stable: they never change and are never reused. Under CC BY-SA 4.0, credit this catalog and share adapted versions under the same licence.

## How it's made
Nobody writes entries by hand. Four scripts build the catalog, each with its own tests ([`scripts/README.md`](scripts/README.md)):

1. **Discovery** searches public GitHub repositories for `.toc` files that list a current WoW Interface.
2. **The release check** downloads each add-on's latest stable release and inspects it. A new release waits 6 hours before it's checked.
3. **The generator** lists every add-on whose release passed for WoW Forever, taking its details from what the author published. A value the author didn't publish is left out, never guessed. Hand corrections live in [`overrides/`](overrides), and the never-add list in [`optout.json`](optout.json).
4. **Details** writes each add-on's details file.

Every change is checked by the validator on its pull request. Versions players report for CurseForge and Wago add-ons are confirmed only through a reviewed pull request ([`confirmed.json`](confirmed.json), see [CONTRIBUTING.md](CONTRIBUTING.md#version-reports-from-the-app)).

## Contributing
- **Ask for an add-on** with the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form. It's for add-ons that were missed, and for those only on CurseForge or Wago.
- **Questions** are welcome as issues, with the Question form.
- **Add-ons that can't be listed**, for example because their only source turned off downloads by other apps, are recorded as [`unavailable`](https://github.com/dylanfrankcom/forever-addon-catalog/issues?q=label%3Aunavailable) issues, each saying what would make it possible.
- **Pull requests from outside the project aren't accepted:** see [CONTRIBUTING.md](CONTRIBUTING.md).

The catalog and the app are free and open source, developed by AI agents (Claude Code) under the maintainer's direction.

## For the maintainer
- **The check workflow's deploy key:** `main` takes changes only by pull request, except from the deploy key that the
  check workflow (`check.yml`) pushes the rebuilt catalog with. Its private half is the `CATALOG_DEPLOY_KEY` secret. To
  replace it, make a new key pair (`ssh-keygen -t ed25519 -N "" -f catalog-deploy`), add the public half as a deploy key
  with write access, put the private half in `CATALOG_DEPLOY_KEY`, point the ruleset's exception at the new key, delete
  the old deploy key, and delete both local files.

## Licence
- **The catalog's data** (`catalog.json`) is licensed under [CC BY-SA 4.0](LICENSE-DATA). Anyone, including other add-on managers, may use and adapt it. Credit this catalog, and share adapted versions under the same licence.
- **Everything else** (the schema, `scripts/` and the workflows) is licensed under the [GNU GPL, version 3 or later](LICENSE).

Both come with no warranty and no liability: see sections 15 and 16 of the GPL and section 5 of CC BY-SA 4.0. The add-ons themselves aren't part of this repository, and each keeps its author's licence.
