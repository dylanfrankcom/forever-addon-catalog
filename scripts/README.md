# Scripts

.NET 11 file-based programs. Run them through the app repo's `dev.ps1` (its main checkout sits beside this one, as `..\app`), so NuGet's packages stay in the developer tools folder and .NET's first-run steps don't touch your profile:

```powershell
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/validate.cs
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/validate.cs -- --self-test
```

Don't run plain `dotnet run`: without `dev.ps1`'s settings, NuGet puts packages in `%UserProfile%\.nuget`, and .NET adds its tools folder to your PATH.

## validate.cs
Checks `catalog.json`, and `versions.json` when there is one (or a file given after `--`; a file with "versions" in its name is checked as a versions file):
1. **The schema** (`schema/catalog.schema.json`), which also holds the fixed list of tags.
2. **The rules the schema can't express:**
   - `duplicate-id`: the same ID is used twice.
   - `unknown-reference`: `requires`, `optional` or `conflicts` names an ID that isn't in the catalog.
   - `self-reference`: an entry lists itself.
   - `folder-claimed-twice`: two entries claim the same folder, ignoring case and including `overrides`.
   - `added-date`: `added` isn't a real day (`2026-02-30`) or is in the future.
   - `author-not-one-name`: an author's `name` lists several people or a group (a comma, a semicolon, "&", "and others" or "contributors") instead of the add-on's main author.
   - `unknown-author`: an entry's `author` isn't in `authors`.
   - `unused-author`: an author in `authors` isn't the author of any entry.
   - `author-profile`: one of an author's `profiles` isn't on GitHub, Wago or CurseForge, or two are on the same site.
   - `edition-interface`: an edition's `interface` isn't its `gameVersion`'s, or its `interfaceSince` isn't a real day or is in the future.
   - `test-client-edition`: a PTR's `of` isn't a live edition.
   - `edition-forever`: `editions.forever` doesn't match the `forever` block.
   - `unknown-edition`: an entry lists an edition that isn't in `editions`.
   - `edition-up-to-date`: an entry's `upToDate` for an edition doesn't match whether it lists that edition's current Interface.
   - `versions-same-file` (versions.json): the same file of the same release is listed under two editions with a different hash or link.

Exit code 0 when valid, 1 when not.

**`--base <file>`** also compares with that earlier catalog: if the entries, the authors, the `forever` block or the `editions` differ, `version` must go up (`version-not-bumped`), and every entry that's new must have `added` (`added-missing`). CI passes the catalog from the PR's base branch.

**`--self-test`** runs every file in `test-data/`:
- `valid-*.json` must pass.
- `invalid-<code>[-detail].json` must fail with that code (`schema`, `json`, or one of the codes above). For example, `invalid-folder-claimed-twice-case.json` expects `folder-claimed-twice`.

It also checks that the Add an add-on form's Tags dropdown offers exactly the schema's tags, in the same order (`issue-form-tags`).

It prints only the cases that fail, then how many passed.

Version-bump cases are pairs in `test-data/bump/`: `<name>.base.json` and `<name>.head.json`, where `invalid-added-missing-*` expects `added-missing` and any other `invalid-*` expects `version-not-bumped`.

Add a case for every new rule.

## find-sources.cs
Looks on GitHub for an add-on's other sources and writes a Markdown report for a person to confirm. It never edits `catalog.json`. The **find-sources** workflow runs it by hand (Actions → find-sources → Run workflow) and puts the report in the job summary.

```powershell
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/find-sources.cs                          # every entry
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/find-sources.cs -- --id gatherlite       # one entry
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/find-sources.cs -- --folder GatherLite   # a suggested add-on
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/find-sources.cs -- --self-test
```

For each add-on it gathers repositories: the entry's GitHub sources and `repository` link, repositories holding a `<Folder>.toc` (GitHub code search, which needs `GITHUB_TOKEN`) and repositories named after the folder. For each one it downloads the release GitHub flags as Latest (a .zip asset, up to 200 MB; never extracted or run), reads its `.toc` the way the catalog does (`tocPath`, then `_Forever`, `-Forever`, plain) and reports:
- whether the zip holds the add-on's folder and the `.toc` lists the Forever Interface;
- the IDs the `.toc` declares (`X-Curse-Project-ID`, `X-Wago-ID`, `X-WoWI-ID`) against the entry's sources;
- a verdict: already listed, **the same add-on** (the entry's own repository link), or **possibly the same add-on** (it ships the folder, but could be a mirror or fork: a matching CurseForge ID alone doesn't prove the author publishes it);
- for an entry without a GitHub source whose candidate lists the Forever Interface, a **candidate for one-click installs** with a GitHub source to start from.

CurseForge and Wago aren't contacted. `--self-test` checks the matching rules offline.

## discover.cs
Finds add-ons for every edition on public GitHub repos and writes `candidates.json`, one row per repo, for the release check to work from. It lists nothing by itself and never edits `catalog.json`. The **discover** workflow runs it weekly (or by hand) and keeps `candidates.json` on the `checker` branch, never on `main`.

```powershell
$env:GITHUB_TOKEN = (gh auth token)
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs                        # every edition
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --edition forever   # one edition
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --no-search         # read the known repos again
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --self-test
```

Official APIs only, read-only:
1. **Code search** for `.toc` files listing each edition's current Interface (from `catalog.json`'s `editions`; test clients come later). Code search returns at most 1,000 results a query, so each search is split by file size, halving the range until every part is under the cap; a single size still over it is reported as capped. Requests are spaced 7 seconds apart (the limit is 10 a minute) and rate-limit answers are waited out.
2. **GraphQL**, 10 repos a query: description and topics, last push, archived or fork, the latest release (GitHub's Latest, never a pre-release) with its files, sizes and SHA-256 digests, the newest tags, whether there's a `.pkgmeta`, and up to 12 of the repo's `.toc` files, the shallowest first, read again at the default branch.

A row has the repo, the editions whose current Interface one of its `.toc` files lists, the main `.toc`'s fields (the shallowest one listing a current Interface: Title, Author, Notes and their translations, Version, Category, IconTexture, `X-Curse-Project-ID`, `X-Wago-ID`, `X-WoWI-ID`, dependencies), every `.toc` read with its Interfaces, the latest release and the tags.

Dropped: forks, archived repos, repos that are gone, repos where no `.toc` lists a current Interface, and repos on the never-add list, `optout.json` (`owner/name` for one repo, `owner` for a whole account). One repo is one row, whichever editions it ships for; a renamed repo found under both names is one row.

Re-running changes only what changed: the rows are sorted and written the same way each time, the previous run's repos are read again even if search misses them, and a repo that can't be read this time keeps its previous row. The report (also the job summary) counts the repos per edition and the drops, and names any capped or failed search.

## check.cs
Checks the latest release of each add-on discovery found (`candidates.json`) and writes `checks.json`, which the generator lists add-ons from. It never edits `catalog.json`. The **check** workflow runs it every 6 hours (or by hand) and keeps `checks.json` on the `checker` branch, beside `candidates.json`. While the catalog repo is private, the **discover** and **check** workflows are turned off, so they use no Actions minutes: run both scripts locally, and their files stay in your checkout.

```powershell
$env:GITHUB_TOKEN = (gh auth token)
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/check.cs                          # every candidate in scope
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/check.cs -- --repo owner/name     # one repo
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/check.cs -- --self-test           # made-up releases, offline
```

**Scope** (`--scope`, default `forever`): only candidates that support that edition are checked, each for every edition its release ships for. `--scope all` checks every candidate.

For each repo, read-only and with official APIs only:
1. **GitHub's Latest release,** read with GraphQL, 25 repos a query (one REST request per repo would pass the Actions token's hourly limit). Pre-releases (GitHub's flag, or `alpha` / `beta` in the tag) are skipped. A release published less than `--hold-hours` ago (default 6) is held back, so a release pulled quickly (after an account compromise, say) never reaches players.
2. **Each edition's file:** the only `.zip`, else the one the packager's `release.json` lists for the edition's `packagerFlavors` (never a no-lib build), else the only one whose name has one of its `fileWords`.
3. **The download:** its size must be what GitHub lists, its SHA-256 is recorded, and where GitHub publishes a digest the two must match. Release files come from GitHub's download host without the token.
4. **The inspection,** the same checks the app makes when it installs: no entry outside the add-on's folder, no programs (`.exe`, `.dll`, scripts and the like), at least one folder, and for each edition the `.toc` it reads (`tocSuffixes` in order, then the plain `.toc`, with load conditions for its `gameTypes`) in every folder (one only another folder lists as optional may lack it), listing its Interface. The rules are in `schema/README.md`, "How an edition reads an add-on".

Each edition gets a result: **passed**; **not-updated** (it lists an older Interface: an earlier patch, or a PTR's live edition's); or **failed**, with the reason. An edition the release isn't for gets none. `needs` lists required add-ons the file doesn't ship.

A release is checked once, and again only when the checks or the editions change (`rules`). Each edition keeps its last good release (`good`) until a newer one passes or isn't updated yet, so a failing release is never listed and the one before it stays. A release or file that can't be read this run (network, API) keeps the last result and is tried again next run.

`checks.json`: `checked` (when), `scope`, and `repos`, one per repo: `release`, `published`, `state` (`checked`, `held`, `pre-release`, `no-release` or `error`, with a `reason`), `checkedAt`, `rules`, `results` and `good` by edition (`file`, `url`, `size`, `sha256`, `interface`, `folders`, `main`, `needs`; `good` adds `release`, `published`, `upToDate` and `checkedAt`).
