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
   - `confirmed-edition`: a source's `confirmed` names an edition the entry doesn't list.
   - `versions-same-file` (versions.json): the same file of the same release is listed under two editions with a different hash or link.

Exit code 0 when valid, 1 when not.

**`--base <file>`** also compares with that earlier catalog: if the entries, the authors, the `forever` block or the `editions` differ, `version` must go up (`version-not-bumped`), and every entry that's new must have `added` (`added-missing`). CI passes the catalog from the PR's base branch.

**`--self-test`** runs every file in `test-data/`:
- `valid-*.json` must pass.
- `invalid-<code>[-detail].json` must fail with that code (`schema`, `json`, or one of the codes above). For example, `invalid-folder-claimed-twice-case.json` expects `folder-claimed-twice`.

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

Code search goes at most every 7.5 seconds (the limit is 10 a minute) and waits for `x-ratelimit-reset` when an answer says none are left. A refused request waits as GitHub's REST docs say, as discovery's do (see discover.cs, "Pacing"): `retry-after`, else the reset, else a minute doubling; a 403 that isn't a rate limit isn't retried.

## discover.cs
Finds add-ons for every edition on public GitHub repos and writes `candidates.json`, one row per repo, for the release check to work from. It lists nothing by itself and never edits `catalog.json`. The **discover** workflow runs it in parts (below) and keeps `candidates.json` on the `checker` branch, never on `main`.

```powershell
$env:GITHUB_TOKEN = (gh auth token)
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs                        # every edition
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --edition forever   # one edition
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --no-search         # read the known repos again
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --search-minutes 60  # stop starting searches after an hour
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --part search       # search, re-read only changed repos
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --part stats        # download counts and funding links only
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/discover.cs -- --self-test
```

Official APIs only, read-only:
1. **Code search** for `.toc` files listing each edition's current Interface (from `catalog.json`'s `editions`; test clients come later). Code search returns at most 1,000 results a query, so each search walks up the file sizes in ranges under the cap: it starts at 0 to 255 bytes, halves a range that's over the cap and asks again, and doubles the next range after one well under it. A single size still over the cap is reported as capped.

   **Pacing.** Code search allows 10 requests a minute, counted in a window that resets each minute, and GitHub's servers can disagree on the count. So searches go at most every 7.5 seconds, and when an answer says none are left (`x-ratelimit-remaining: 0`) the next waits for `x-ratelimit-reset`. A refused request (a 429, or a 403 that's a rate limit) waits as GitHub's REST docs say: `retry-after` when it's given; else, with none left, until the reset; else a minute, then 2, 4, 8 and 16, and after six tries it counts as failed.

   **Spread over runs.** The Interfaces are searched oldest first: `candidates.json`'s `searched` keeps when each was last searched in full, and one never searched goes first. `--search-minutes` stops starting searches after that long; an Interface not searched in full (out of time, or a request that still failed) keeps its date, so the next run starts with it, and the report names what was missed. Repos found for it before are kept, as every previous run's are.
2. **GraphQL**, 10 repos a query: description and topics, last push, archived or fork, the latest release (GitHub's Latest, never a pre-release) with its files, sizes and SHA-256 digests, the newest tags, whether there's a `.pkgmeta`, when the repo and its owner's account were made and its stars, how many releases it has and the days of its newest 100, the CurseForge and Wago links in the latest release's notes (a mirror's record of where its file came from), every release's download counts (GitHub's public `downloadCount` for each release file; a repo with more than 100 releases gets a query per further 100, and keeps the previous run's counts when one fails, never a short count), and up to 12 of the repo's `.toc` files, the shallowest first, read again at the default branch.

A row has the repo, the editions whose current Interface one of its `.toc` files lists, the main `.toc`'s fields (the shallowest one listing a current Interface: Title, Author, Notes and their translations, Version, Category, IconTexture, `X-Curse-Project-ID`, `X-Wago-ID`, `X-WoWI-ID`, dependencies), every `.toc` read with its Interfaces and the same fields (Notes cut to 300 characters), the latest release and the tags. The generator takes an entry's fields from its main folder's `.toc`, which may not be the one discovery picked, and uses each `.toc`'s dependencies to tell a release's separate add-ons from one add-on's modules.

Dropped: forks, archived repos, repos that are gone, repos where no `.toc` lists a current Interface, and repos on the never-add list, `optout.json` (`{ "repo": "owner/name" }` for one repo, `owner` for a whole account, each with its `reason`). One repo is one row, whichever editions it ships for; a renamed repo found under both names is one row.

Code search misses some repos, so the repos the catalog already lists (its GitHub sources, and each override's `repo`) are read on every run as well, their `.toc` files found in the repo's root and one folder down. Private repos are always dropped, even when the token running discovery can read them. Forks are dropped, except one an override names: a person checked, through an issue, that it's the maintained continuation of an abandoned add-on. Every `## Interface` line of a `.toc` counts (a first line can be packager placeholders).

Each row also records whether the repo has issues turned on, and its `FUNDING.yml` as GitHub reads it (`fundingLinks`). For each GitHub Sponsors link, a separate pass asks GitHub whether the account has a Sponsors listing, and whether it is the repo's owner or a public member of the organisation that owns it. An answer that can't be read this run keeps the previous one.

Re-running changes only what changed: the rows are sorted and written the same way each time, the previous run's repos are read again even if search misses them, and a repo that can't be read this time keeps its previous row. The report (also the job summary) counts the repos per edition with when its Interface was last searched in full, the drops, and names any capped, failed or unfinished search.

**Parts.** The workflow runs discovery in two parts, each on its own schedule, and a run by hand picks one (`search`, `stats`, `all`, or `reread`, which is `all` without the search):
- **search** (`--part search`, every 4 hours): code search for up to an hour, then reads the new repos and asks, 100 repos a query, which known repos changed (last push, Latest release, name). Only those are read again; the rest keep their rows. When the editions' Interfaces changed, every repo is read again, since a row's editions depend on them. A repo read again keeps the stats part's fields.
- **stats** (`--part stats`, daily): every repo's download counts and release history, its GitHub Sponsors links' answers and its short links' destinations. Nothing else is read or changed. `downloadHistory` keeps each day's total, the newest 30 days, so a later version can tell which add-ons are being picked up.

Both write `candidates.json`, so they share a concurrency group and never overlap. Without `--part`, a run does all of it and reads every repo.

## check.cs
Checks the latest release of each add-on discovery found (`candidates.json`) and writes `checks.json`, which the generator lists add-ons from. It never edits `catalog.json`. The **check** workflow runs it every hour (or by hand) and keeps `checks.json` on the `checker` branch, beside `candidates.json`. After checking, the workflow generates and publishes the catalog, then records on `checker` (`built.json`) the `main` commit it built from. It fails instead of passing quietly when there's no discovery data to build from, and when the catalog is stale: a generator input (`scripts/generate.cs`, `scripts/details.cs`, `overrides/`, `optout.json`, `confirmed.json`, `tags.json`, `schema/`) changed on `main` more than 3 hours after the last build (`.github/catalog-current.sh`, which names the oldest waiting change; `--self-test` tests it).

```powershell
$env:GITHUB_TOKEN = (gh auth token)
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/check.cs                          # every candidate in scope
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/check.cs -- --repo owner/name     # one repo, checked again even if unchanged
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/check.cs -- --self-test           # made-up releases, offline
```

**Scope** (`--scope`, default `forever`): only candidates that support that edition are checked, each for every edition its release ships for. `--scope all` checks every candidate.

**Repos that release several add-ons** (an override in `--overrides`, default `overrides/`, with `repo` and `tagPrefix` for each): the repo isn't read by its Latest release. Its newest 100 releases are read once (REST), and each prefix's newest stable release whose tag starts with it (case-sensitive, by publish date) is checked as a row of its own, with `tagPrefix` after `repo`. `--repo` checks every prefix of that repo.

For each repo, read-only and with official APIs only:
1. **GitHub's Latest release,** read with GraphQL, 25 repos a query (one REST request per repo would pass the Actions token's hourly limit). Pre-releases (GitHub's flag, or `alpha` / `beta` in the tag) are skipped. A release published less than `--hold-hours` ago (default 6) is held back, so a release pulled quickly (after an account compromise, say) never reaches players.
2. **Each edition's file:** the only `.zip`, else the one the packager's `release.json` lists for the edition's `packagerFlavors` (never a no-lib build), else the only one whose name has one of its `fileWords`.
3. **The download:** its size must be what GitHub lists, its SHA-256 is recorded, and where GitHub publishes a digest the two must match. Release files come from GitHub's download host without the token.
4. **The inspection,** the same checks the app makes when it installs: no entry outside the add-on's folder, no programs (`.exe`, `.dll`, scripts and the like), at least one folder, and for each edition the `.toc` it reads (`tocSuffixes` in order, then the plain `.toc`, with load conditions for its `gameTypes`) in every folder (one only another folder lists as optional may lack it), listing its Interface. A folder whose `.toc` lists only other editions' Interfaces is left out for the edition instead (`leftOut`), unless it's the main folder or a folder that stays requires it. The rules are in `schema/README.md`, "How an edition reads an add-on". Each passing edition also keeps the main folder's `.toc` fields as the release has them (`toc`: Title, Notes and translations, Author, Category, IconTexture, dependencies), which the generator prefers to discovery's: some repos have no such `.toc`, because the packager writes it.

5. **Warning signs,** recorded for the trust signals and listed in the job's summary, never a reason to fail by themselves: `hidden-code`, a `.lua` file (up to 8 MB) that runs text as code (`loadstring` or `load`) and also holds a long run of base64, of `\ddd` escapes or a long `string.char` list; and `unexpected-files`, archives or native code (`.zip`, `.7z`, `.so`, `.dylib` and the like). Each edition's result keeps them as `warnings`, `{ "sign", "files" }` (at most 5 files).
6. **The build record:** GitHub's artifact attestations for the file's SHA-256 (one REST request, plus the bundle from GitHub's storage, which is Snappy-compressed). When one is a SLSA provenance statement naming a workflow in the add-on's own repo, the result keeps that workflow's path as `built`. The record's signature isn't verified here.

A `.toc` of any size is read (some add-ons keep their data in it); only its `##` lines count.

Each edition gets a result: **passed**; **not-updated** (it lists an older Interface: an earlier patch, or a PTR's live edition's); or **failed**, with the reason. An edition the release isn't for gets none. `needs` lists required add-ons the file doesn't ship.

A release is checked once, and again only when the checks or the editions change (`rules`). Each edition keeps its last good release (`good`) until a newer one passes or isn't updated yet, so a failing release is never listed and the one before it stays. A release or file that can't be read this run (network, API) keeps the last result and is tried again next run.

`checks.json`: `checked` (when), `scope`, and `repos`, one per repo: `release`, `published`, `state` (`checked`, `held`, `pre-release`, `no-release` or `error`, with a `reason`), `checkedAt`, `rules`, `results` and `good` by edition (`file`, `url`, `size`, `sha256`, `interface`, `folders`, `main`, `needs`; `good` adds `release`, `published`, `upToDate` and `checkedAt`).

**Refusals:** a request GitHub turns away as a rate limit (a 429, or a 403 that says so) waits as its REST docs say, as discovery's do (see discover.cs, "Pacing"): `retry-after` when given, else the reset when none are left, else a minute, doubling, six tries. Server errors are tried again after 10, 20, 30 seconds; another 403 (a missing permission) isn't retried.

## details.cs
Writes `addons/<id>.json` for each listed add-on with a GitHub source, from `catalog.json` and `candidates.json`, after `generate.cs`. It reads each repo through GitHub's GraphQL API (ten repos a query) and the contributors count through the REST API, so it needs a token in `GITHUB_TOKEN`:

```powershell
$env:GITHUB_TOKEN = gh auth token
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/details.cs                       # every add-on
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/details.cs -- --only questie     # a few, removing nothing
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/details.cs -- --self-test
```

- **What's read:** the README (the repo's, else the main folder's), the five latest stable releases' notes (or `CHANGELOG.md` when none has notes), images on GitHub in the README and a `screenshots` folder, stars, open issues, contributors, the dates of the 50 latest stable releases, and the main folder's `.toc` (languages, dependencies, saved variables). Format: `schema/README.md`, "Add-on details".
- **Safe text:** everything read is untrusted. HTML becomes Markdown where it has a Markdown form (images, links, headings, bold, italic, code) and is dropped otherwise; scripts, styles, frames and comments go entirely. Links keep http(s), mailto and `#` anchors; images keep https only. Relative links and images are made absolute on GitHub. Code blocks and inline code are left as they are. The README is capped at 50,000 characters, each release's notes at 8,000 and the changelog at 20,000, cut at a paragraph and marked as cut.
- **Re-running:** a file is rewritten only when its content changed, so its ETag stays valid. A repo that can't be read keeps its last file. A file whose add-on is no longer listed is removed (not with `--only`).

## reports.cs
Version reports and add-on suggestions that players send through the relay (see [CONTRIBUTING.md](../CONTRIBUTING.md#version-reports-from-the-app)). The **reports** workflow runs it every 10 minutes: `collect`, then `done` once the store is saved on the `checker` branch (`relay.json`), then `propose`, and opens or updates one pull request from `reports/confirm`.

```powershell
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/reports.cs -- propose --store <relay.json>
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/reports.cs -- --self-test
```

- **collect** sends the relay the catalog's index (the editions, and each add-on with a CurseForge or Wago source), takes the waiting messages, checks every field again and keeps the good ones. The relay's address and key come from `RELAY_URL` and `RELAY_KEY`, never from arguments; it must be https (or http to this machine, for a relay run locally). Every message with an ID is marked collected, refused ones too, so a bad one doesn't come back.
- **Checked again:** a report's add-on must be listed with that source, its edition known, its version 1 to 64 letters, digits, spaces and `. _ + - ( )` starting with a letter or digit, its hash 64 hex digits, its count and dates sane, and `networks` (how many different networks sent it, as the relay counted them) a number from 0 to 50, or null when the relay had no key to count with. A suggestion's project, name (plain text, no `< > @ \` or backticks), folders and `.toc` fields are checked the same way; a bad `.toc` field is left out.
- **The store** keys each message by the relay's ID and first-seen time. The relay counts repeats and sends a message again with its higher count, so a message collected again replaces what was kept.
- **An end-to-end test** (`scripts/reports-e2e.cs`, run by the PR check) runs the pieces in order on made-up data, with no network: the generator lists a made-up hand-listed add-on, `collect --answer <file>` reads a recorded relay answer with one report for it (`--answer` sends nothing to the relay), `propose` puts it in `confirmed.json` and the pull request's text, and the generator copies it onto the source.
- **.toc details:** a report can carry its download's `.toc` details (`toc`: title, author, notes, category, Interface numbers, locales, optional add-ons' folders). Each field is checked like the rest and a bad one is left out. For an add-on listed by hand (an override without `repo`), **propose** adds to `overrides/<id>.json` what they add and the entry lacks: editions whose current Interface they name, locales the catalog knows (`enGB` as `enUS`), optional add-ons by the catalog ID that installs that folder, and the author's Notes as the summary only when there's none. Nothing there is replaced. It uses the version the most players reported (newest `.toc` first), flags disagreeing `.toc` files and one player only, and names in the text what an entry can't hold: another author, an optional add-on the catalog doesn't list. A GitHub add-on's `.toc` comes from its checked release, so its reports' details are ignored.
- **Deciding** (`--rules`: a file the reports workflow writes from the `CATALOG_RULES` secret, since the thresholds aren't published; a missing or invalid one stops the run): **propose** gives each item a verdict: `merge` (every threshold met, enough of the version's reports on its checksum, newer than what's confirmed by a part-by-part version comparison), `review` (the thresholds met but it needs a reader: `.toc` details, a version whose order can't be told), `wait`, or `human` (a confirmed version reported with another checksum, an older version, a burst of reports from few players; these are anomalies). Unknown counts are never enough. The public text shows progress (0, 25, 50, 75 or 100%) and says only "needs more players" or "needs more time", never the counts. **review** asks Claude (Sonnet) about the `review` items only, from their checked facts, and keeps per item only `merge`, `wait` or `ask-human` and a plain reason of up to 200 characters. **decide** combines them: `review` items take Claude's answer, or go to the maintainer without one; nothing is merged unless `--auto on`, not while `--paused`, and not past `dailyCap` items a day (`--merged-today`). **propose --only** writes just what the decision merges, **--skip** the rest. **guard** refuses an automatic pull request that changes any path but `confirmed.json` and existing `overrides/<id>.json` files, changes an override's `repo`, `repoId` or `githubWithdrawn` other than by adding the decided link (with the decided repo ID) to an add-on listed by hand or withdrawing one a link made (with its record), changes an item the decision didn't merge, or replaces anything an override says.
- **links** (with `GITHUB_TOKEN`) looks at the GitHub repos players' `.toc` files name (`toc.github`) for add-ons listed by hand, and at the links made before, and writes `links.json` (`--out`). For each repo it reads GitHub's API: whether it's there and archived, its current name, and its Latest release's zip (up to 200 MB, never extracted or run), whose `.toc` files in the add-on's folders give the CurseForge project and Wago ID they name. A repo whose `.toc` names the add-on (or whose release is a confirmed file) is linked (`merge`, with GitHub's repo ID) only with proof a stranger can't give: **the catalog's own records** name it (the entry's `repository` or `homepage` link, or its author's profiles, which reports never write) and it isn't a fork; or **players vouch for it**: reports of a confirmed file that name it meet the new-version thresholds (`--rules`), are the rules' share of those files' reports with `.toc` details, and none names another repo, and the repo was created before the file was first reported, isn't a fork, and the repo and its owner are older than the rules' `linkAge`. A copy of a confirmed file alone proves nothing (anyone can re-upload it). Anything short of that goes to the maintainer (`human`), saying what's missing; anything else is dropped. Links made here are looked at first, then repos players vouch for enough (at most 30 a run), and a release's `.toc` files are read only up to 64 KB. A link made this way (an override with `repo` and `editions`) is withdrawn automatically (`merge`) when the repo is gone or archived (`archived`), renamed or moved (`new-owner`: a new claim, which needs the proof again), its ID changed (`new-owner`: the name was taken by another repo), or neither names the add-on nor has its file (`not-named`), or players' copies of a confirmed file name another repo with the full new-version proof (`not-named`: a few reports can't). A failed read decides nothing. **propose --links** takes them in like the rest (key `<id>/github`): a link adds `repo` to the override, first, and its `repoId` (clearing an old `githubWithdrawn`), and changes nothing else; withdrawing removes it and adds `githubWithdrawn` (the repo, the reason and the day), which the entry publishes so add-on managers can warn players who installed it from GitHub.
- **requests** prints the GitHub repos players suggested that the catalog doesn't list and that weren't asked about in the last 7 days (at most 10), and records them in `--requested`; the reports workflow runs the discover workflow on them (`part: repos`).
- **wanted** writes `wanted.json` (`--out`): add-ons players suggested that aren't listed, hand-listed entries missing a summary or languages or with `.toc` details waiting, and hand-listed sources with no confirmed version for an edition or a newer one reported. Each item has its progress (0, 25, 50, 75 or 100, rounded down) under the rule for its kind (`newAddon`, `tocDetails`, `newVersion`), never the counts. The check workflow runs it with each build (format in `schema/README.md`).
- **done** tells the relay what was collected. It runs only after the store is saved: the relay deletes what's marked.
- **propose**, for each add-on, source and edition the entry lists: the version reported most recently and its most-reported hash, unless `confirmed.json` has them already or confirmed something after the last report. It writes them into `confirmed.json` with the day and the report count, the pull request's text (`--body`) with each version's reports and about how many players sent them ("10 reports from about 3 players": the networks the relay counted, added up; left out when any message wasn't counted), and a flag for anything to check first ("one player only (estimated)" for several reports from one network), and `--facts`: only checked fields, for the summary Claude writes when the workflow has an `ANTHROPIC_API_KEY`. It drops from the store what's confirmed, what's no longer listed, suggestions now listed, and anything not seen for 90 days. Reports for an edition the entry doesn't list are never proposed; it names them in its output (the run's summary), so they don't wait unseen.

## tags.cs
Chooses tags for entries whose author set none and keeps them in `tags.json`, which the generator uses for those entries only (`from.tags: "inferred"`; see `schema/README.md`, "Tags"). It asks about an entry with no tags from its `.toc`, its topics or an override when `tags.json` has no row for it or the row's `fingerprint` (name, summary and folders) no longer matches. Claude (Haiku 5.5) is asked in batches of 50, at most 10 a run, from each entry's name, summary, folders and the first 500 characters of its README (`addons/<id>.json`, without images or link targets). That text comes from authors, so only tags from the fixed list are kept from the answer, each once, at most 3; an entry the answer leaves out is asked again next run. Rows for entries no longer listed are removed. The **tags** workflow runs it daily and opens or updates one pull request from `tags/infer` with the changes; it needs the `ANTHROPIC_API_KEY` secret and does nothing without it.

```powershell
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/tags.cs -- --dry-run    # which entries it would ask about
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/tags.cs                # needs ANTHROPIC_API_KEY
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/tags.cs -- --self-test
```

## generate.cs
Writes `catalog.json`, `versions.json` and `icons/` from `checks.json`, `candidates.json`, `overrides/`, `optout.json`, `confirmed.json` (the confirmed CurseForge and Wago versions, copied onto their sources) and `tags.json` (tags chosen for entries whose author set none). How each field is chosen is in `schema/README.md`, "Where the catalog comes from" and "An entry". Run it after `check.cs`, then validate:

```powershell
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/generate.cs                     # catalog.json and icons/
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/generate.cs -- --no-icons        # keep icons/ as it is
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/generate.cs -- --self-test
powershell -ExecutionPolicy Bypass -File ..\app\dev.ps1 dotnet run scripts/validate.cs
```

**Scope:** the first generated catalog lists Forever add-ons only. The scope is one setting (`Settings.Scope`); every edition's results are already in `checks.json`.

**Icons:** the main `.toc`'s IconTexture, only when it points into the add-on's own folders (never game art). It's read from the checked release file (TGA, BLP or PNG), converted to PNG and kept as `icons/<id>.png`; `icons/icons.json` remembers which release each came from, so an icon is read again only when the release changes. An icon that can't be read is left out.

**The main folder** (from the release check) names the entry: a folder others extend (Questie for QuestieDB), else the first that loads at start, never a `!` helper folder while there's another. The `.toc` fields discovery read (Title, Notes, icon, dependencies) are used only when that `.toc` belongs to the main folder; otherwise the entry falls back to the folder name and the repository's description.

**Authors** made from a GitHub account get the support links from their listed repos' `FUNDING.yml` (rules in the top-level README, `sponsor`). Every refused link, and every support link that appeared, changed or went away since the last catalog, is in the report. Each entry's `links.issues` is the repo's issues page when it has issues turned on.

**`versions.json`** lists each listed add-on's last good release for each edition (format in `schema/README.md`). It's rewritten only when a release changed, so its ETag stays valid between runs. A run that would list fewer than half the add-ons listed now writes nothing and exits with 1, keeping the last good files. The **check** workflow runs the generator and the validator after each check and commits the files to `main` only when they changed and passed.

**A hand-listed add-on's official source:** a GitHub repo whose release shares a folder or CurseForge project with an override that has no `repo` (an add-on listed by hand) is never listed: it's dropped as a re-upload. When something ties it to that entry, the report lists it apart ("possibly the official source for a hand-listed add-on"), naming each signal and whether it's tied both ways:
- **the repo's claim**, which anyone can write: its `.toc` names the entry's CurseForge project (`X-Curse-Project-ID`) or its Wago ID (`X-Wago-ID`, against the entry's Wago source or link);
- **the catalog's side**, set by a person here: the repo's owner is a profile on the entry's author record in `overrides/_authors.json`, or the entry's own `links` point to the repo.

To switch an entry to its repo, keeping its ID:
1. In `overrides/<id>.json`, add `"repo": "<owner>/<name>"` (and `tagPrefix` if the repo releases several add-ons).
2. Remove its hand-only fields: `sources`, `editions`, `folders` and `tocPath`. An override's own value would replace the checked one. From then on, the generator writes the sources from the repo: GitHub, and CurseForge when its `.toc` names the project. Its Wago page becomes a link from the `.toc`'s `X-Wago-ID`. The folders and editions come from the checked release.
3. Keep everything else (name, summary, author, tags, links): it still corrects the generated entry.
4. Run `generate.cs` and `validate.cs`: the entry keeps its ID, its source is now the repo, and the repo no longer appears in the report's re-uploads.

**Re-running changes only what changed:** IDs and `added` dates come from the previous `catalog.json`, entries and fields are written in a fixed order, and the version (the day's date and build number, `2026.11.28.3`) changes only when something changed. The report lists what was dropped and why, entries with no summary, and icons that couldn't be read.
