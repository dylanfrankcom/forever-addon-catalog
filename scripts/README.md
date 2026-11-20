# Scripts

.NET 11 file-based programs. Run them through the app repo's `dev.ps1` (it sits beside this repo in `Documents\Developer`), so NuGet's packages stay in `Documents\Developer\.tools` and .NET's first-run steps don't touch your profile:

```powershell
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs
powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs -- --self-test
```

Don't run plain `dotnet run`: without `dev.ps1`'s settings, NuGet puts packages in `%UserProfile%\.nuget`, and .NET adds its tools folder to your PATH.

## validate.cs
Checks `catalog.json` (or a file given after `--`):
1. **The schema** (`schema/catalog.schema.json`).
2. **The rules the schema can't express:**
   - `duplicate-id`: the same ID is used twice.
   - `unknown-reference`: `requires`, `optional` or `conflicts` names an ID that isn't in the catalog.
   - `self-reference`: an entry lists itself.
   - `folder-claimed-twice`: two entries claim the same folder, ignoring case and including `overrides`.
   - `protected-folder`: a `Spoken*` folder or `.spoken-manager`. The schema already rejects these; this is a second check.

Exit code 0 when valid, 1 when not.

**`--base <file>`** also checks the version: if the entries or the `forever` block differ from that earlier catalog, `version` must go up (`version-not-bumped`). CI passes the catalog from the PR's base branch.

**`--self-test`** runs every file in `test-data/`:
- `valid-*.json` must pass.
- `invalid-<code>[-detail].json` must fail with that code (`schema`, `json`, or one of the codes above). For example, `invalid-folder-claimed-twice-case.json` expects `folder-claimed-twice`.

Version-bump cases are pairs in `test-data/bump/`: `<name>.base.json` and `<name>.head.json`, where `invalid-*` expects `version-not-bumped`.

Add a case for every new rule.
