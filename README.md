# Forever Add-on Catalog

An open catalog of World of Warcraft add-ons, built from what their authors publish. [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager) reads it to find, install and update add-ons, and any other add-on manager is free to use it too.

> **Status:** being built. About 1,370 add-ons for WoW Forever are listed, each found and checked automatically from its GitHub release. Each entry also records the other WoW editions its release supports. Forever Add-on Manager reads the catalog at start and before each update check.
>
> Not affiliated with or endorsed by Blizzard Entertainment. Every add-on belongs to its author; entries only say where the author publishes it.

## How the catalog is made
Nobody writes entries by hand. Each step is a script in `scripts/` (see [`scripts/README.md`](scripts/README.md)):

1. **Discovery** (`discover.cs`) searches public GitHub repositories for `.toc` files that list a current WoW Interface, and reads each repository's description, topics, `.toc` files and latest release. Private repositories, forks and archived repositories are skipped.
2. **The release check** (`check.cs`) downloads each add-on's latest stable release and inspects it the way the app does before installing: no files outside the add-on's folders, no programs, and a `.toc` for each edition that lists its Interface. It records each file's size and SHA-256, warning signs (hidden code, unexpected files) and, where GitHub has one, the build record of the workflow that made it. A new release waits 6 hours before it's checked.
3. **The generator** (`generate.cs`) lists every add-on whose release passed for WoW Forever. It takes the name, summary, author, tags, icon and links from what the author published, and applies the hand corrections in `overrides/`. It writes `catalog.json` and `versions.json`.
4. **Details** (`details.cs`) writes a page of details for each add-on: its README, recent release notes, screenshots and stats.

Every change to `catalog.json` gets a new version and must pass the validator (`validate.cs`), which the PR check runs on every pull request.

## What's here
| Path | What |
|---|---|
| `catalog.json` | One entry per add-on: its sources, folders, editions, required and optional add-ons, conflicts, trust signals and notes, and one record per author. Generated, never edited by hand. |
| `versions.json` | Each add-on's latest checked release for each edition, with its file, size and SHA-256, so the app finds updates without asking GitHub. Generated with `catalog.json`. |
| `addons/` | A details file per add-on released on GitHub (`<id>.json`), which the app fetches only when the add-on is opened. Written by `details.cs`. |
| `icons/` | Add-on icons, converted to PNG from each add-on's own `.toc` IconTexture. |
| `overrides/` | Hand corrections, one file per entry (`<id>.json`), and `_authors.json`, the author records written by hand. |
| `optout.json` | The never-add list: repositories and accounts that are never listed, each with why (see [Your choice](#your-choice-listed-link-only-or-not-at-all)). |
| `schema/` | The formats of every file above, with a description of every field in [`schema/README.md`](schema/README.md). |
| `scripts/` | Discovery, the release check, the generator, details, link checks and validation: .NET file-based programs, each with its own self-test. |
| `.github/workflows/` | The PR check (`validate.yml`: every script's self-test, and the catalog validated), and the scheduled discovery and release-check jobs, which are turned off while the repository is private. |

## Rules
- **The catalog is data, not instructions.** It says where to look. The app still checks every download (its hash, the Forever `.toc`, no programs) and asks before installing.
- **Nothing is listed that wasn't checked:** only a release the release check downloaded and inspected. The exception is an add-on that's only on CurseForge or Wago, whose entry is written by hand.
- **Nothing is guessed:** a value the author didn't publish is left out. Each entry's `from` says where its values came from.
- **IDs are stable:** they never change and are never reused.
- **Never a copy:** an add-on is listed from its author's own repository, never from someone else's re-upload or mirror of it.

## Entry fields
The full format is in [`schema/README.md`](schema/README.md). The fields players see most:

- **`sources`**: where the add-on is downloaded, in order of preference: its GitHub release (one-click installs and updates), then CurseForge and Wago (the player downloads the zip there and hands it to the app). Some repositories release several add-ons. Each one's source then names the tag its releases start with (`tagPrefix`, such as `spoken/`).
- **`author`**: who made the add-on, as its pages credit them. Each author has one record, shared by all their add-ons: their **`name`**, an optional **`homepage`**, their **`profiles`** on GitHub, Wago and CurseForge, and their **`sponsor`** pages.
- **`sponsor`** (in the author's record): the `https` pages where people can support the author (Patreon, Ko-fi, PayPal, GitHub Sponsors …), only as the author lists them. A wrong link would send a player's money to the wrong person, so they're checked:
  - **From the author's own repositories only:** each one's `FUNDING.yml` (or the owner's `.github` repository's), as GitHub reads it. Never from a fork or another account's repository.
  - **GitHub Sponsors:** only an account that has a Sponsors listing and owns the repository, or is a public member of the organisation that does. It comes first.
  - **Everything else:** `https` only, and a shortened link (bit.ly and the like) is listed as the page it leads to, never as the short link.
  - **Reported:** every refused link, and every link that appears, changes or goes away, for the maintainer to look at.
  
  Authors written by hand (`overrides/_authors.json`) keep their own list, which can also include the Donate button on their CurseForge or Wago page.
- **`links`**: the add-on's public source repository (`repository`), even when it's downloaded from CurseForge or Wago, and where players report problems (`issues`). The catalog doesn't record licences: each repository states its own.
- **`downloads`**: GitHub's own public download counts for the release files: all releases' (`total`) and the latest one's (`latest`). The app tracks nothing. Its installs download from GitHub, so they're already part of these counts.
- **`trust`**: what players can go on, from public data only:
  - whether a workflow in the add-on's own repository built the release file (GitHub's build record);
  - when the repository and its owner's account were made, and its releases and stars;
  - the author's `X-Forever-Catalog` marker;
  - badges ("Built by its own repo", "New") and warning signs.

  A warning sign never keeps an add-on out. The app is more careful with it instead: a longer wait for new releases, and left out of Update all. The rules are in [`schema/README.md`](schema/README.md#trust-signals).
- **`icon`**: the icon the author ships in the add-on itself (the `.toc`'s IconTexture), converted to PNG. Never game art and never drawn here.
- **`ownApp`**: the add-on's own app, when its author makes one that installs and updates it (Spoken Manager, for Spoken). The app recommends it, and leaves the add-on to it while it's in use.
- **`linkOnly`**: the add-on stays listed, but the app never downloads it. Players get a link to the page the author chose.
- **`added`**: the day the entry joined the catalog.

## For add-on authors
Thanks for making add-ons for Forever.

### How listing works
The catalog lists every add-on whose latest GitHub release lists the Forever Interface and passes the release check. It's found by searching public repositories for `.toc` files, so nobody has to submit it. Its name, summary and links come from what you published. The app installs and updates it from your GitHub release, so every install counts in your release's download numbers.

### Your choice: listed, link only, or not at all
- **Opt out:** open a [Remove my add-on, or make it link only](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=author-choice.yml) issue from the GitHub account that owns the repository, or add `## X-Forever-Catalog: no` to your `.toc` (the catalog stops listing the add-on on its next run). Removed add-ons go on the never-add list (`optout.json`, with the reason and the day), so discovery never lists them again.
- **Link only:** the same form. Your add-on stays listed, but the app never downloads it: players get a link to the page you choose and download it there.
- **Your own app:** if you make an app that installs your add-on, say so with the **Task** form. The app then recommends it, and leaves your add-on to it while it's in use.
- **Opt in:** nothing is needed. But `## X-Forever-Catalog: yes` in your `.toc`, or the `wow-forever` topic on your repository, says you're happy to be listed, and the app may show it.

### Tips
- **GitHub releases install in one click.** When an add-on is only on CurseForge or Wago, the player downloads the zip from that site themselves and hands it to the app, every update. If you publish there, attaching the same zip to a GitHub release gives players the one-click path. The [BigWigs packager](https://github.com/BigWigsMods/packager) can do it in the same run as your CurseForge and Wago uploads: give it a GitHub token and it creates the release too.
- **A build record earns a badge.** If a workflow in your repository builds the release and [attests it](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations), the app shows "Built by its own repo".
- **List the Forever Interface in your `.toc`.** Only add-ons whose current release lists it are in the catalog. Separate files per game version are fine: a name that says which one is Forever's (ending in `-forever`, say) helps the right one get picked.
- **One repository, several add-ons:** give each its own tag prefix (`addon/v1.2.0`, `pack/v2.0.0`) and open a **Task** issue, so each is listed from its own releases.
- **Your support page comes first.** Entries link to the support pages and issue tracker you list yourself. The catalog takes no donations, so any support goes to you.
- **Your links live in one place.** Your homepage, your profiles and your support pages are kept once, in your author record, for all your add-ons. To add or change them, or to fix something in your entry, open an issue with the **Task** form.

## Contributing
Know an add-on that works on Forever? Ask for it with the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form. Add-ons released on GitHub are found and checked automatically; the form is for add-ons that are missed, and for those only on CurseForge or Wago. Questions are welcome as issues, with the Question form.

Some add-ons work on Forever but can't be listed, for example when their only source has turned off downloads by other apps. They're recorded as issues labelled [`unavailable`](https://github.com/dylanfrankcom/forever-addon-catalog/issues?q=label%3Aunavailable) (there's an [Add-on that can't be listed](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=unavailable.yml) form), each saying what would make it possible. Comments there are welcome when an author changes something. Pull requests from outside the project aren't accepted: see [CONTRIBUTING.md](CONTRIBUTING.md).

The catalog and the app are free and open source, and are developed by AI agents (Claude Code) under the maintainer's direction.

## Licence
- **The catalog's data** (`catalog.json`) is licensed under [CC BY-SA 4.0](LICENSE-DATA). Anyone, including other add-on managers, may use and adapt it. Credit this catalog, and share adapted versions under the same licence.
- **Everything else** (the schema, `scripts/` and the workflows) is licensed under the [GNU GPL, version 3 or later](LICENSE). Forks must stay open source under the same licence.

Both come with no warranty and no liability: see sections 15 and 16 of the GPL and section 5 of CC BY-SA 4.0. The add-ons themselves aren't part of this repo. Each one keeps its author's licence.
