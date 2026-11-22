# Scripts

.NET 11 file-based programs. Run them through the app repo's `dev.ps1` (it sits beside this repo in `Documents\Developer`), so NuGet's packages stay in `Documents\Developer\.tools` and .NET's first-run steps don't touch your profile:

```powershell
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs -- --self-test
```

Don't run plain `dotnet run`: without `dev.ps1`'s settings, NuGet puts packages in `%UserProfile%\.nuget`, and .NET adds its tools folder to your PATH.

## validate.cs
Checks `catalog.json` (or a file given after `--`):
1. **The schema** (`schema/catalog.schema.json`), which also holds the fixed list of tags.
2. **The rules the schema can't express:**
   - `duplicate-id`: the same ID is used twice.
   - `unknown-reference`: `requires`, `optional` or `conflicts` names an ID that isn't in the catalog.
   - `self-reference`: an entry lists itself.
   - `folder-claimed-twice`: two entries claim the same folder, ignoring case and including `overrides`.
   - `added-date`: `added` isn't a real day (`2026-02-30`) or is in the future.
   - `author-not-one-name`: `author` lists several people or a group (a comma, a semicolon, "&", "and others" or "contributors") instead of the add-on's main author.

Exit code 0 when valid, 1 when not.

**`--base <file>`** also compares with that earlier catalog: if the entries or the `forever` block differ, `version` must go up (`version-not-bumped`), and every entry that's new must have `added` (`added-missing`). CI passes the catalog from the PR's base branch.

**`--self-test`** runs every file in `test-data/`:
- `valid-*.json` must pass.
- `invalid-<code>[-detail].json` must fail with that code (`schema`, `json`, or one of the codes above). For example, `invalid-folder-claimed-twice-case.json` expects `folder-claimed-twice`.

It also checks that the Add an add-on form's Tags dropdown offers exactly the schema's tags, in the same order (`issue-form-tags`).

Version-bump cases are pairs in `test-data/bump/`: `<name>.base.json` and `<name>.head.json`, where `invalid-added-missing-*` expects `added-missing` and any other `invalid-*` expects `version-not-bumped`.

Add a case for every new rule.

## find-sources.cs
Looks on GitHub for an add-on's other sources and writes a Markdown report for a person to confirm. It never edits `catalog.json`. The **find-sources** workflow runs it by hand (Actions → find-sources → Run workflow) and puts the report in the job summary.

```powershell
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/find-sources.cs                          # every entry
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/find-sources.cs -- --id gatherlite       # one entry
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/find-sources.cs -- --folder GatherLite   # a suggested add-on
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/find-sources.cs -- --self-test
```

For each add-on it gathers repositories: the entry's GitHub sources and `repository` link, repositories holding a `<Folder>.toc` (GitHub code search, which needs `GITHUB_TOKEN`) and repositories named after the folder. For each one it downloads the release GitHub flags as Latest (a .zip asset, up to 200 MB; never extracted or run), reads its `.toc` the way the catalog does (`tocPath`, then `_Forever`, `-Forever`, plain) and reports:
- whether the zip holds the add-on's folder and the `.toc` lists the Forever Interface;
- the IDs the `.toc` declares (`X-Curse-Project-ID`, `X-Wago-ID`, `X-WoWI-ID`) against the entry's sources;
- a verdict: already listed, **the same add-on** (the entry's own repository link), or **possibly the same add-on** (it ships the folder, but could be a mirror or fork: a matching CurseForge ID alone doesn't prove the author publishes it);
- for an entry without a GitHub source whose candidate lists the Forever Interface, a **candidate for one-click installs** with a GitHub source to start from.

CurseForge and Wago aren't contacted. `--self-test` checks the matching rules offline.
