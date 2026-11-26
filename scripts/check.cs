#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Checks the latest release of each add-on that discovery found (candidates.json) and writes checks.json, which the
// generator lists add-ons from. It never edits catalog.json.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md), with a GitHub token in GITHUB_TOKEN:
//   ... dotnet run scripts/check.cs                                  # every candidate in scope
//   ... dotnet run scripts/check.cs -- --repo owner/name             # one repo (repeatable)
//   ... dotnet run scripts/check.cs -- --self-test                   # offline tests with made-up releases
// Options: --catalog <file> (default ./catalog.json), --candidates <file> (default ./candidates.json), --out <file>
// (default ./checks.json, also read as the previous run), --scope <edition id | all> (default forever: only candidates
// that support that edition are checked, each for every edition its release ships for), --hold-hours <n> (default 6),
// --summary <file> (also append the report there, as the workflow does with $GITHUB_STEP_SUMMARY).
//
// For each repo: read GitHub's Latest release (GraphQL, 25 repos a query), skip pre-releases, hold a new release back for --hold-hours, pick
// each edition's file (the packager's release.json, then the edition's words in the file name, or the only .zip),
// download it, record its size and SHA-256 and check them against GitHub's digest, then inspect the zip the way the app
// does when it installs: no entry outside its folder, no programs, and for each edition the .toc it reads (the edition
// facts in catalog.json, rules in schema/README.md) listing its Interface. A release is checked once; it's checked
// again only when the rules or the editions change. A repo whose release can't be read or downloaded keeps its last
// result, and each edition keeps its last good release ("good") until a newer one passes.
// Exit code 0 when checks.json was written, 1 when an input can't be read, 2 for bad arguments or no token.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    return await SelfTest.Run();
}

string catalogArg = "catalog.json", candidatesArg = "candidates.json", outArg = "checks.json", scope = "forever";
string? summaryArg = null;
var holdHours = 6.0;
var onlyRepos = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--catalog" when value is not null: catalogArg = value; i++; break;
        case "--candidates" when value is not null: candidatesArg = value; i++; break;
        case "--out" when value is not null: outArg = value; i++; break;
        case "--summary" when value is not null: summaryArg = value; i++; break;
        case "--scope" when value is not null: scope = value.Trim(); i++; break;
        case "--repo" when value is not null: onlyRepos.Add(value.Trim()); i++; break;
        case "--hold-hours" when double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var h) && h >= 0: holdHours = h; i++; break;
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/check.cs [--scope <edition|all>] [--repo <owner/name>]... [--hold-hours <n>] [--catalog <file>] [--candidates <file>] [--out <file>] [--summary <file>] | --self-test");
            return 2;
    }
}

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("GITHUB_TOKEN isn't set: the release check reads GitHub's API with a token.");
    return 2;
}

List<Edition> editions;
List<JsonObject> candidates;
Dictionary<string, JsonObject> previous;
try
{
    editions = Edition.ReadAll(JsonNode.Parse(File.ReadAllText(catalogArg))!);
    candidates = JsonNode.Parse(File.ReadAllText(candidatesArg))!["candidates"]!.AsArray().Select(r => r!.AsObject()).ToList();
    previous = File.Exists(outArg) ? Checks.Read(File.ReadAllText(outArg)) : new(StringComparer.OrdinalIgnoreCase);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NullReferenceException or FormatException or KeyNotFoundException)
{
    Console.Error.WriteLine($"Can't read the inputs: {e.Message}");
    return 1;
}
if (scope != "all" && editions.All(e => e.Id != scope))
{
    Console.Error.WriteLine($"No edition '{scope}' in the catalog (it has {string.Join(", ", editions.Select(e => e.Id))}), and it isn't 'all'.");
    return 2;
}

var inScope = candidates.Where(c => Checker.InScope(c, scope)
    && (onlyRepos.Count == 0 || onlyRepos.Contains(c["repo"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))).ToList();
var now = DateTimeOffset.UtcNow;
var rules = Checker.Rules(editions);
var hold = TimeSpan.FromHours(holdHours);
using var github = new GitHub(token);
var results = new SortedDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
if (onlyRepos.Count > 0)
{
    foreach (var (repo, entry) in previous)
    {
        results[repo] = entry; // a partial run keeps everything it didn't check
    }
}
Console.Error.WriteLine($"Checking {inScope.Count} repos (scope {scope}) ...");
for (var start = 0; start < inScope.Count; start += Releases.BatchSize)
{
    var chunk = inScope.Skip(start).Take(Releases.BatchSize).ToList();
    var names = chunk.Select(c => c["repo"]!.GetValue<string>()).ToList();
    var releases = await github.LatestReleasesAsync(names);
    foreach (var (candidate, repo) in chunk.Zip(names))
    {
        var (release, problem) = releases[repo];
        results[repo] = await Checker.CheckAsync(new(candidate, release, problem, previous.GetValueOrDefault(repo), editions, rules, now, hold), github.DownloadAsync);
    }
    if ((start / Releases.BatchSize) % 10 == 0)
    {
        Console.Error.WriteLine($"  {Math.Min(start + Releases.BatchSize, inScope.Count)}/{inScope.Count}");
    }
}
File.WriteAllText(outArg, Checks.Write(now, scope, results.Values));

var report = Checks.Report(results.Values, scope, editions);
Console.Write(report);
if (summaryArg is not null)
{
    File.AppendAllText(summaryArg, report);
}
return 0;

/// <summary>
/// How one edition reads an add-on: catalog.json's edition facts (schema/README.md, "How an edition reads an add-on").
/// <paramref name="Of"/> is the live edition a PTR is filed under, whose Interface it accepts as "not updated yet".
/// </summary>
sealed record Edition(string Id, string? Of, string Interface, IReadOnlyList<string> Suffixes, IReadOnlyList<string> GameTypes,
    IReadOnlyList<string> PackagerFlavors, IReadOnlyList<string> FileWords)
{
    public static List<Edition> ReadAll(JsonNode catalog)
    {
        var list = new List<Edition>();
        foreach (var (id, node) in catalog["editions"]!.AsObject())
        {
            static List<string> Strings(JsonNode? n) => n?.AsArray().Select(s => s!.GetValue<string>()).ToList()
                ?? throw new FormatException("an edition is missing its .toc facts (tocSuffixes, gameTypes, packagerFlavors, fileWords)");
            list.Add(new(id, node!["of"]?.GetValue<string>(), node["interface"]!.GetValue<string>(), Strings(node["tocSuffixes"]),
                Strings(node["gameTypes"]), Strings(node["packagerFlavors"]), Strings(node["fileWords"])));
        }
        return list;
    }

    /// <summary>Whether a line with these load conditions applies: every Allow names one of its game types, no Exclude does.</summary>
    public bool Allows(IEnumerable<(bool Allow, IReadOnlyList<string> Types)> conditions) =>
        conditions.All(c => c.Allow == c.Types.Any(t => GameTypes.Contains(t, StringComparer.OrdinalIgnoreCase)));
}

/// <summary>Reading a .toc for an edition, as the app does.</summary>
static partial class Toc
{
    /// <summary>The <c>## Key: value</c> lines that apply to the edition. For a key given more than once, the last one that applies wins.</summary>
    public static Dictionary<string, string> Parse(string text, Edition edition)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var m = Field().Match(raw.TrimEnd('\r').TrimStart('﻿'));
            if (!m.Success)
            {
                continue;
            }
            var value = m.Groups[2].Value.Trim();
            var conditions = Conditions().Matches(value);
            if (conditions.Count > 0)
            {
                if (!edition.Allows(conditions.Select(c => (c.Groups[1].Value.Equals("Allow", StringComparison.OrdinalIgnoreCase),
                    (IReadOnlyList<string>)c.Groups[2].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)))))
                {
                    continue;
                }
                value = Conditions().Replace(value, "").Trim();
            }
            fields[m.Groups[1].Value.Trim()] = value;
        }
        return fields;
    }

    /// <summary>A list value (Interface, Dependencies): separated by commas, spaces or tabs.</summary>
    public static List<string> List(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) ? [.. v.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];

    /// <summary>The .toc an edition reads in folder <paramref name="folder"/>: each suffix in order, then the plain one (names ignore case).</summary>
    public static string? Find(string folder, IEnumerable<string> tocNames, Edition edition)
    {
        var names = tocNames.ToList();
        return edition.Suffixes.Select(s => $"{folder}{s}.toc").Append($"{folder}.toc")
            .Select(wanted => names.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(n => n is not null);
    }

    [GeneratedRegex(@"^##\s*([^:]+):\s*(.*)$")]
    private static partial Regex Field();

    [GeneratedRegex(@"\[(Allow|Exclude)LoadGameType\s+([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Conditions();
}

/// <summary>A release file read without extracting it: its folders, their .toc files and anything that blocks it.</summary>
sealed partial class Package
{
    /// <summary>The most an add-on zip may expand to, and its most entries: the app's limits.</summary>
    public const long MaxUncompressedBytes = 4L << 30;
    public const int MaxEntries = 200_000;
    const int MaxTocBytes = 1 << 20;

    static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".sh", ".py", ".msi", ".msp", ".scr", ".com", ".jar", ".lnk", ".hta", ".reg", ".cpl", ".ocx", ".sys", ".pif",
    };

    public SortedSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each folder's own .toc files (directly inside it), by name, with their text.</summary>
    public Dictionary<string, Dictionary<string, string>> Tocs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Problems that block the file for every edition.</summary>
    public List<string> Blockers { get; } = [];

    public static Package Read(Stream zipStream)
    {
        var package = new Package();
        var unsafeEntries = new List<string>();
        var programs = new List<string>();
        long total = 0;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Contains('\0') || DotDot().IsMatch(name))
            {
                unsafeEntries.Add(entry.FullName);
                continue;
            }
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || (parts.Length == 1 && !name.EndsWith('/')))
            {
                continue; // a loose top-level file: the app never copies it
            }
            package.Folders.Add(parts[0]);
            if (name.EndsWith('/'))
            {
                continue;
            }
            if (BlockedExtensions.Contains(Path.GetExtension(parts[^1])))
            {
                programs.Add(name);
            }
            if (parts.Length == 2 && parts[1].EndsWith(".toc", StringComparison.OrdinalIgnoreCase) && entry.Length <= MaxTocBytes)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var tocs = package.Tocs.TryGetValue(parts[0], out var t) ? t : package.Tocs[parts[0]] = new(StringComparer.OrdinalIgnoreCase);
                tocs[parts[1]] = reader.ReadToEnd();
            }
        }
        if (unsafeEntries.Count > 0)
        {
            package.Blockers.Add($"entries that would land outside the add-on's folder: {string.Join(", ", unsafeEntries.Take(5))}");
        }
        if (zip.Entries.Count > MaxEntries || total > MaxUncompressedBytes)
        {
            package.Blockers.Add($"too large to be an add-on ({zip.Entries.Count:N0} entries, {total:N0} bytes extracted)");
        }
        if (programs.Count > 0)
        {
            package.Blockers.Add($"files an add-on should never need: {string.Join(", ", programs.Take(5))}");
        }
        if (package.Folders.Count == 0)
        {
            package.Blockers.Add("no add-on folder");
        }
        return package;
    }

    [GeneratedRegex(@"(^|/)\.\.(/|$)")]
    private static partial Regex DotDot();
}

/// <summary>What one release file is for one edition.</summary>
/// <param name="Result">passed, not-updated (it lists an older Interface: the live one for a PTR, or an earlier patch), failed, or null when the file isn't for this edition at all.</param>
sealed record EditionResult(string? Result, List<string> Reasons, string? Interface, List<string> Folders, string? Main, List<string> Needs);

static class Inspect
{
    /// <summary>
    /// Whether <paramref name="value"/> is an older Interface the edition accepts as "not updated yet": a PTR's live
    /// edition's, or an earlier patch of the same version or the minor version before it (12.0.7 for 12.1.5). Classic Era
    /// (1.15) and Forever (1.60) share a major version, so a whole major version isn't enough.
    /// </summary>
    public static bool Older(string value, Edition edition, Edition? of) =>
        value != edition.Interface && ((of is not null && value == of.Interface)
            || (int.TryParse(value, out var v) && int.TryParse(edition.Interface, out var current) && v < current
                && v / 10000 == current / 10000 && current / 100 % 100 - v / 100 % 100 <= 1));

    public static EditionResult For(Package package, Edition edition, Edition? of)
    {
        var folders = package.Folders.ToList();
        var reasons = new List<string>();
        var older = new List<string>();
        var tocFor = folders.ToDictionary(f => f, f => package.Tocs.TryGetValue(f, out var t) && Toc.Find(f, t.Keys, edition) is { } n
            ? (Name: n, Fields: Toc.Parse(t[n], edition)) : ((string Name, Dictionary<string, string> Fields)?)null, StringComparer.OrdinalIgnoreCase);

        // A folder another folder lists as optional may have no .toc for this edition: it's copied but never loaded.
        var optional = tocFor.Values.Where(t => t is not null).SelectMany(t => Toc.List(t!.Value.Fields, "OptionalDeps")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<(string Folder, bool LoadOnDemand)>();
        var lists = false; // some folder lists the current Interface, or one it accepts
        var needs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (tocFor[folder] is not { } toc)
            {
                if (!package.Tocs.ContainsKey(folder))
                {
                    reasons.Add($"{folder}: no .toc file");
                }
                else if (!optional.Contains(folder))
                {
                    reasons.Add($"{folder}: no .toc for this edition, only {string.Join(", ", package.Tocs[folder].Keys.Order(StringComparer.OrdinalIgnoreCase))}");
                }
                continue;
            }
            loaded.Add((folder, toc.Fields.TryGetValue("LoadOnDemand", out var lod) && lod == "1"));
            var interfaces = Toc.List(toc.Fields, "Interface");
            if (interfaces.Contains(edition.Interface))
            {
                lists = true;
            }
            else if (interfaces.FirstOrDefault(i => Older(i, edition, of)) is { } old)
            {
                lists = true;
                older.Add(old);
            }
            else
            {
                reasons.Add($"{folder}: {toc.Name} lists Interface '{string.Join(", ", interfaces)}', not {edition.Interface}");
            }
            foreach (var dep in Toc.List(toc.Fields, "Dependencies").Concat(Toc.List(toc.Fields, "RequiredDeps"))
                .Where(d => !d.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase) && !package.Folders.Contains(d)))
            {
                needs.Add(dep);
            }
        }
        var main = loaded.FirstOrDefault(f => loaded.Any(o => o.Folder != f.Folder && o.Folder.StartsWith(f.Folder, StringComparison.OrdinalIgnoreCase))).Folder
            ?? loaded.FirstOrDefault(f => !f.LoadOnDemand).Folder ?? loaded.FirstOrDefault().Folder;

        string? result = !lists ? null : reasons.Count > 0 ? "failed" : older.Count > 0 ? "not-updated" : "passed";
        var iface = result is null ? null : older.Count > 0 ? older.Max(StringComparer.Ordinal) : edition.Interface;
        return new(result, reasons, iface, folders, main, [.. needs]);
    }
}

/// <summary>One repo's check: the inputs and the clock.</summary>
sealed record CheckInput(JsonObject Candidate, JsonNode? Release, string? ReleaseProblem, JsonObject? Previous, IReadOnlyList<Edition> Editions,
    string Rules, DateTimeOffset Now, TimeSpan Hold);

/// <summary>A downloaded file, or null with the reason it couldn't be downloaded.</summary>
sealed record Download(byte[]? Bytes, string? Problem);

static partial class Checker
{
    /// <summary>Raised when the checks themselves change, so every release is checked again.</summary>
    const string Version = "1";

    /// <summary>The largest release file downloaded.</summary>
    public const long MaxFileBytes = 512L << 20;

    public static bool InScope(JsonObject candidate, string scope) =>
        scope == "all" || (candidate["editions"]?.AsArray().Any(e => e!.GetValue<string>() == scope) ?? false);

    /// <summary>A fingerprint of the checks and the editions they read: when it changes, every release is checked again.</summary>
    public static string Rules(IEnumerable<Edition> editions)
    {
        var text = Version + "\n" + string.Join("\n", editions.Select(e => string.Join("|", e.Id, e.Of, e.Interface,
            string.Join(",", e.Suffixes), string.Join(",", e.GameTypes), string.Join(",", e.PackagerFlavors), string.Join(",", e.FileWords))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public static bool IsPreRelease(string tag) => PreReleaseTag().IsMatch(tag);

    /// <summary>
    /// The file each edition reads from these .zip files: the only one; else the one release.json lists for the edition's
    /// flavor (not a no-lib build); else the only one whose name has one of the edition's words. Null when none is clear.
    /// </summary>
    public static string? Pick(IReadOnlyList<string> zips, JsonNode? manifest, Edition edition)
    {
        if (zips.Count == 1)
        {
            return zips[0];
        }
        if (manifest?["releases"] is JsonArray releases)
        {
            var listed = releases.Where(r => r?["nolib"]?.GetValueKind() != JsonValueKind.True
                    && r?["metadata"] is JsonArray m && m.Any(f => f?["flavor"] is JsonValue v && v.TryGetValue<string>(out var fl)
                        && edition.PackagerFlavors.Contains(fl, StringComparer.OrdinalIgnoreCase)))
                .Select(r => r!["filename"] is JsonValue v && v.TryGetValue<string>(out var f) ? f : null)
                .Where(f => f is not null && zips.Contains(f, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (listed.Count == 1)
            {
                return zips.First(z => z.Equals(listed[0], StringComparison.OrdinalIgnoreCase));
            }
        }
        var named = zips.Where(z => edition.FileWords.Any(w => z.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    public static async Task<JsonObject> CheckAsync(CheckInput input, Func<string, Task<Download>> download)
    {
        var repo = input.Candidate["repo"]!.GetValue<string>();
        var supports = input.Candidate["editions"]?.AsArray().Select(e => e!.GetValue<string>()).ToHashSet() ?? [];
        var previousGood = input.Previous?["good"]?.AsObject();

        // Keeps the last result and every good release, with why this run didn't check.
        JsonObject Keep(string state, string reason, string? tag = null, string? published = null)
        {
            var kept = input.Previous?.DeepClone().AsObject() ?? new JsonObject { ["repo"] = repo, ["results"] = new JsonObject(), ["good"] = new JsonObject() };
            kept["repo"] = repo;
            kept["state"] = state;
            kept["reason"] = reason;
            if (tag is not null)
            {
                kept["release"] = tag;
                kept["published"] = published;
            }
            return kept;
        }

        if (input.ReleaseProblem is not null)
        {
            return Keep("error", input.ReleaseProblem);
        }
        if (input.Release is null)
        {
            return new JsonObject
            {
                ["repo"] = repo, ["release"] = null, ["published"] = null, ["state"] = "no-release", ["reason"] = "no Latest release on GitHub",
                ["checkedAt"] = Stamp(input.Now), ["rules"] = input.Rules, ["results"] = new JsonObject(), ["good"] = new JsonObject(),
            };
        }

        var tag = input.Release["tag_name"]!.GetValue<string>();
        var publishedAt = input.Release["published_at"]?.GetValue<string>() is { } at
            ? DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        var published = publishedAt is { } p ? Stamp(p) : null;
        if (input.Release["prerelease"]?.GetValue<bool>() == true || input.Release["draft"]?.GetValue<bool>() == true || IsPreRelease(tag))
        {
            return Keep("pre-release", $"{tag} is a pre-release (alpha or beta); those are never listed", tag, published);
        }
        if (publishedAt is null || publishedAt > input.Now - input.Hold)
        {
            var until = (publishedAt ?? input.Now) + input.Hold;
            return Keep("held", $"{tag} is new: it's checked after {Stamp(until)}, so a release pulled quickly never reaches players", tag, published);
        }
        if (input.Previous is { } prev && prev["state"]?.GetValue<string>() == "checked" && prev["release"]?.GetValue<string>() == tag
            && prev["rules"]?.GetValue<string>() == input.Rules)
        {
            return prev; // checked already, with the same rules
        }

        var assets = input.Release["assets"]?.AsArray().Select(a => a!.AsObject()).ToList() ?? [];
        var zips = assets.Select(a => a["name"]!.GetValue<string>()).Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        JsonNode? manifest = null;
        if (zips.Count > 1 && assets.FirstOrDefault(a => a["name"]!.GetValue<string>().Equals("release.json", StringComparison.OrdinalIgnoreCase)) is { } m)
        {
            var got = await download(m["browser_download_url"]!.GetValue<string>());
            try
            {
                manifest = got.Bytes is null ? null : JsonNode.Parse(got.Bytes);
            }
            catch (JsonException)
            {
                // release.json only helps choose: when it can't be read, the file names decide
            }
        }

        var results = new JsonObject();
        var files = new Dictionary<string, (JsonObject Asset, string Sha, Package? Package, string? Problem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var edition in input.Editions)
        {
            var of = edition.Of is null ? null : input.Editions.FirstOrDefault(e => e.Id == edition.Of);
            var name = Pick(zips, manifest, edition);
            if (name is null)
            {
                if (supports.Contains(edition.Id))
                {
                    results[edition.Id] = Failed(zips.Count == 0 ? "the release has no .zip file"
                        : $"several .zip files ({string.Join(", ", zips)}) and none is marked for this edition by release.json or its name");
                }
                continue;
            }
            if (!files.TryGetValue(name, out var file))
            {
                var asset = assets.First(a => a["name"]!.GetValue<string>() == name);
                var (sha, package, problem) = await FetchAsync(asset, download);
                if (problem is { Network: true })
                {
                    return Keep("error", $"{name}: {problem.Value.Text}", tag, published); // try again next run
                }
                files[name] = file = (asset, sha ?? "", package, problem?.Text);
            }
            if (file.Problem is not null || file.Package!.Blockers.Count > 0)
            {
                // A file that fails for every edition is recorded for each edition it was meant for.
                if (supports.Contains(edition.Id) || (file.Package is { } pk && Inspect.For(pk, edition, of).Result is not null))
                {
                    results[edition.Id] = Described(Failed(file.Problem ?? string.Join("; ", file.Package!.Blockers)), file.Asset, file.Sha);
                }
                continue;
            }
            var check = Inspect.For(file.Package, edition, of);
            if (check.Result is null)
            {
                if (supports.Contains(edition.Id))
                {
                    var why = check.Reasons.Count > 0 ? string.Join("; ", check.Reasons) : "no .toc lists this edition's Interface";
                    results[edition.Id] = Described(Failed(why), file.Asset, file.Sha);
                }
                continue;
            }
            var row = Described(new JsonObject { ["result"] = check.Result }, file.Asset, file.Sha);
            if (check.Reasons.Count > 0)
            {
                row["reason"] = string.Join("; ", check.Reasons);
            }
            row["interface"] = check.Interface;
            row["folders"] = Strings(check.Folders);
            row["main"] = check.Main;
            row["needs"] = Strings(check.Needs);
            results[edition.Id] = row;
        }

        // Each edition keeps its last good release until a newer one passes.
        var good = previousGood?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var (id, node) in results)
        {
            var result = node!["result"]!.GetValue<string>();
            if (result is "passed" or "not-updated")
            {
                var kept = node.DeepClone().AsObject();
                kept.Remove("result");
                kept.Remove("reason");
                good[id] = new JsonObject
                {
                    ["release"] = tag, ["published"] = published, ["upToDate"] = result == "passed", ["checkedAt"] = Stamp(input.Now),
                }.Merge(kept);
            }
        }
        return new JsonObject
        {
            ["repo"] = repo, ["release"] = tag, ["published"] = published, ["state"] = "checked",
            ["checkedAt"] = Stamp(input.Now), ["rules"] = input.Rules,
            ["results"] = Sorted(results), ["good"] = Sorted(good),
        };

        static JsonObject Failed(string reason) => new() { ["result"] = "failed", ["reason"] = reason };
    }

    /// <summary>Downloads a file, records its SHA-256 and checks it against its size and GitHub's digest, then reads the zip.</summary>
    static async Task<(string? Sha, Package? Package, (string Text, bool Network)? Problem)> FetchAsync(JsonObject asset, Func<string, Task<Download>> download)
    {
        var size = asset["size"]?.GetValue<long>() ?? 0;
        if (size > MaxFileBytes)
        {
            return (null, null, ($"the file is {size:N0} bytes, more than an add-on needs", false));
        }
        var got = await download(asset["browser_download_url"]!.GetValue<string>());
        if (got.Bytes is null)
        {
            return (null, null, (got.Problem ?? "couldn't download it", true));
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(got.Bytes));
        if (got.Bytes.LongLength != size)
        {
            return (sha, null, ($"downloaded {got.Bytes.LongLength:N0} bytes, but GitHub lists {size:N0}", false));
        }
        if (asset["digest"]?.GetValue<string>() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            && !digest[7..].Equals(sha, StringComparison.OrdinalIgnoreCase))
        {
            return (sha, null, ($"its SHA-256 {sha} isn't the {digest} GitHub publishes", false));
        }
        try
        {
            return (sha, Package.Read(new MemoryStream(got.Bytes, writable: false)), null);
        }
        catch (InvalidDataException e)
        {
            return (sha, null, ($"not a readable zip ({e.Message})", false));
        }
    }

    static JsonObject Described(JsonObject row, JsonObject asset, string sha)
    {
        row["file"] = asset["name"]!.GetValue<string>();
        row["url"] = asset["browser_download_url"]!.GetValue<string>();
        row["size"] = asset["size"]?.GetValue<long>();
        row["sha256"] = sha.Length > 0 ? sha : null;
        return row;
    }

    static JsonObject Merge(this JsonObject into, JsonObject from)
    {
        foreach (var (key, value) in from.ToList())
        {
            from.Remove(key);
            into[key] = value;
        }
        return into;
    }

    static JsonObject Sorted(JsonObject o) => new(o.ToList().OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));

    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    public static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    // "-alpha", "beta2", ".Beta": alpha or beta not inside a longer word.
    [GeneratedRegex(@"(?<![a-z])(alpha|beta)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PreReleaseTag();
}

static class Checks
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Dictionary<string, JsonObject> Read(string json)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in JsonNode.Parse(json)!["repos"]!.AsArray())
        {
            rows[row!["repo"]!.GetValue<string>()] = row.AsObject();
        }
        return rows;
    }

    public static string Write(DateTimeOffset now, string scope, IEnumerable<JsonObject> rows) => new JsonObject
    {
        ["$comment"] = "Written by scripts/check.cs. Never edit it by hand.",
        ["checked"] = Checker.Stamp(now),
        ["scope"] = scope,
        ["repos"] = new JsonArray(rows.OrderBy(r => r["repo"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .Select(r => (JsonNode)r.DeepClone()).ToArray()),
    }.ToJsonString(Options) + "\n";

    public static string Report(IReadOnlyCollection<JsonObject> rows, string scope, IReadOnlyList<Edition> editions)
    {
        var md = new StringBuilder("## Release check\n\n");
        md.AppendLine($"{rows.Count} repos (scope {scope}).").AppendLine();
        foreach (var state in rows.GroupBy(r => r["state"]?.GetValue<string>() ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"- {state.Key}: {state.Count()}");
        }
        md.AppendLine().AppendLine("| Edition | Passed | Not updated | Failed | Listable (good) |").AppendLine("|---|---|---|---|---|");
        foreach (var edition in editions)
        {
            int Count(string result) => rows.Count(r => r["results"]?[edition.Id]?["result"]?.GetValue<string>() == result);
            md.AppendLine($"| {edition.Id} | {Count("passed")} | {Count("not-updated")} | {Count("failed")} | {rows.Count(r => r["good"]?[edition.Id] is not null)} |");
        }
        return md.AppendLine().ToString();
    }
}

/// <summary>Reading Latest releases with GraphQL, in the REST API's shape (tag_name, published_at, assets ...).</summary>
static class Releases
{
    public const int BatchSize = 25;

    const string Fields = "latestRelease { tagName publishedAt isPrerelease isDraft releaseAssets(first: 50) { nodes { name size downloadUrl digest } } }";

    static string Literal(string s) => JsonSerializer.Serialize(s);

    public static string Query(IReadOnlyList<string> repos)
    {
        var q = new StringBuilder("query {\n");
        for (var i = 0; i < repos.Count; i++)
        {
            var slash = repos[i].IndexOf('/');
            q.Append($"r{i}: repository(owner: {Literal(repos[i][..slash])}, name: {Literal(repos[i][(slash + 1)..])}) {{ {Fields} }}\n");
        }
        return q.Append('}').ToString();
    }

    /// <summary>Each repo's release (null when it has none), or why it couldn't be read: gone, or an error for that repo.</summary>
    public static Dictionary<string, (JsonNode? Release, string? Problem)> Results(IReadOnlyList<string> repos, JsonNode? json)
    {
        var results = new Dictionary<string, (JsonNode?, string?)>(StringComparer.OrdinalIgnoreCase);
        var errors = json?["errors"]?.AsArray() ?? [];
        for (var i = 0; i < repos.Count; i++)
        {
            var node = json?["data"]?[$"r{i}"];
            if (node is null)
            {
                var error = errors.FirstOrDefault(e => e?["path"]?[0]?.GetValue<string>() == $"r{i}");
                results[repos[i]] = (null, error?["type"]?.GetValue<string>() == "NOT_FOUND" ? "the repo is gone or private"
                    : $"its Latest release couldn't be read ({error?["message"]?.GetValue<string>() ?? "no data"})");
                continue;
            }
            if (node["latestRelease"] is not JsonObject r)
            {
                results[repos[i]] = (null, null);
                continue;
            }
            results[repos[i]] = (new JsonObject
            {
                ["tag_name"] = r["tagName"]?.GetValue<string>(),
                ["published_at"] = r["publishedAt"]?.GetValue<string>(),
                ["prerelease"] = r["isPrerelease"]?.GetValue<bool>() ?? false,
                ["draft"] = r["isDraft"]?.GetValue<bool>() ?? false,
                ["assets"] = new JsonArray(r["releaseAssets"]?["nodes"]?.AsArray().Select(a => (JsonNode)new JsonObject
                {
                    ["name"] = a!["name"]?.GetValue<string>(),
                    ["size"] = a["size"]?.GetValue<long>(),
                    ["browser_download_url"] = a["downloadUrl"]?.GetValue<string>(),
                    ["digest"] = a["digest"]?.GetValue<string>(),
                }).ToArray() ?? []),
            }, null);
        }
        return results;
    }
}

sealed class GitHub : IDisposable
{
    readonly HttpClient api = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
    // Release files come from GitHub's download host, which needs no token (and must never get one).
    readonly HttpClient files = new() { Timeout = TimeSpan.FromMinutes(5) };

    public GitHub(string token)
    {
        foreach (var http in new[] { api, files })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-check");
        }
        api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        api.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Each repo's Latest release in one GraphQL query (a run of a few thousand repos stays well inside the Actions
    /// token's hourly limit, which one REST request per repo wouldn't).
    /// </summary>
    public async Task<Dictionary<string, (JsonNode? Release, string? Problem)>> LatestReleasesAsync(IReadOnlyList<string> repos)
    {
        var body = new JsonObject { ["query"] = Releases.Query(repos) }.ToJsonString();
        string? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var response = await api.PostAsync("graphql", new StringContent(body, Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode)
                {
                    return Releases.Results(repos, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
                }
                var status = (int)response.StatusCode;
                last = $"GitHub answered {status} for its Latest release";
                if (status is not (403 or 429 or 500 or 502 or 503 or 504))
                {
                    break;
                }
                var wait = RetryWait(response, attempt);
                Console.Error.WriteLine($"  graphql {repos[0]} ...: {status}, waiting {wait.TotalSeconds:0} s");
                await Task.Delay(wait);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = $"its Latest release couldn't be read ({e.GetType().Name})";
                await Task.Delay(TimeSpan.FromSeconds(10 * (attempt + 1)));
            }
        }
        return repos.ToDictionary(r => r, r => ((JsonNode?)null, (string?)last), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Download> DownloadAsync(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            return new(null, $"not a GitHub release link: {url}");
        }
        string? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await files.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentLength > Checker.MaxFileBytes)
                    {
                        return new(null, "the file is larger than an add-on needs");
                    }
                    return new(await response.Content.ReadAsByteArrayAsync(), null);
                }
                last = $"the download answered {(int)response.StatusCode}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = $"the download failed ({e.GetType().Name})";
            }
            await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
        }
        return new(null, last);
    }

    static TimeSpan RetryWait(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return Clamp(delta);
        }
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var at))
        {
            return Clamp(DateTimeOffset.FromUnixTimeSeconds(at) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
        }
        return TimeSpan.FromSeconds(30 * (attempt + 1));

        static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : t > TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15) : t;
    }

    public void Dispose()
    {
        api.Dispose();
        files.Dispose();
    }
}

static class SelfTest
{
    static int failures;

    static void Check(string name, bool ok)
    {
        if (!ok)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {name}");
        }
    }

    static readonly Edition Forever = new("forever", null, "16001", ["_Forever", "-Forever", "_Camelot", "-Camelot"], ["camelot"], ["camelot", "forever"], ["forever"]);
    static readonly Edition Retail = new("retail", null, "120105", ["_Mainline", "-Mainline"], ["mainline", "standard"], ["mainline", "retail"], ["mainline", "retail"]);
    static readonly Edition Era = new("classic_era", null, "11509", ["_Vanilla", "_Classic", "-Vanilla", "-Classic"], ["vanilla", "classic"], ["classic", "vanilla"], ["vanilla", "era"]);
    static readonly Edition EraPtr = new("classic_era_ptr", "classic_era", "20506", ["_TBC", "_BCC", "_Classic", "-TBC", "-BCC"], ["tbc"], ["bcc", "tbc"], ["tbc", "bcc", "anniversary"]);
    static readonly List<Edition> Editions = [Retail, Era, Forever, EraPtr];
    static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    static readonly string Rules = Checker.Rules(Editions);
    const string Old = "2026-11-20T10:00:00Z";

    static byte[] Zip(params (string Path, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }
        return stream.ToArray();
    }

    static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A made-up GitHub release with these files, and the downloads that serve them.</summary>
    static (JsonNode Release, Dictionary<string, byte[]> Files) Release(string tag, string published, params (string Name, byte[] Bytes, string? Digest)[] assets)
    {
        var files = new Dictionary<string, byte[]>();
        var list = new JsonArray();
        foreach (var (name, bytes, digest) in assets)
        {
            var url = $"https://github.com/a/thing/releases/download/{tag}/{name}";
            files[url] = bytes;
            list.Add(new JsonObject { ["name"] = name, ["size"] = bytes.LongLength, ["browser_download_url"] = url, ["digest"] = digest ?? "sha256:" + Sha(bytes) });
        }
        return (new JsonObject { ["tag_name"] = tag, ["published_at"] = published, ["prerelease"] = false, ["draft"] = false, ["assets"] = list }, files);
    }

    static JsonObject Candidate(params string[] editions) =>
        new() { ["repo"] = "a/thing", ["editions"] = new JsonArray(editions.Select(e => (JsonNode)e).ToArray()) };

    static Task<JsonObject> Run(JsonObject candidate, (JsonNode Release, Dictionary<string, byte[]> Files) release, JsonObject? previous = null,
        string? problem = null, bool offline = false) =>
        Checker.CheckAsync(new(candidate, problem is null ? release.Release : null, problem, previous, Editions, Rules, Now, TimeSpan.FromHours(6)),
            url => Task.FromResult(offline ? new Download(null, "offline")
                : release.Files.TryGetValue(url, out var b) ? new Download(b, null) : new Download(null, "404")));

    static string? Result(JsonObject entry, string edition) => entry["results"]?[edition]?["result"]?.GetValue<string>();
    static string Reason(JsonObject entry, string edition) => entry["results"]?[edition]?["reason"]?.GetValue<string>() ?? "";
    static string? Str(JsonNode? node) => node?.GetValue<string>();

    public static async Task<int> Run()
    {
        // The .toc rules (schema/README.md, "How an edition reads an add-on").
        Check("toc: suffix order", Toc.Find("Thing", ["Thing.toc", "Thing_Camelot.toc", "thing-forever.TOC"], Forever) == "thing-forever.TOC");
        Check("toc: plain last", Toc.Find("Thing", ["Thing.toc", "Thing_Mainline.toc"], Forever) == "Thing.toc");
        Check("toc: none", Toc.Find("Thing", ["Thing_Mainline.toc", "Other.toc"], Forever) is null);
        Check("toc: _Classic is Classic only", Toc.Find("Thing", ["Thing_Classic.toc", "Thing.toc"], Era) == "Thing_Classic.toc"
            && Toc.Find("Thing", ["Thing_Classic.toc", "Thing.toc"], Retail) == "Thing.toc");
        var fields = Toc.Parse("﻿## Interface: 11509,\t16001  120105\r\n## Title: One\n## Title: Forever [AllowLoadGameType camelot]\n## Title: Other [AllowLoadGameType mainline, vanilla]\n## X: no [ExcludeLoadGameType camelot]\n", Forever);
        Check("toc: Interface list", Toc.List(fields, "Interface").SequenceEqual(["11509", "16001", "120105"]));
        Check("toc: the last line that applies wins", fields["Title"] == "Forever");
        Check("toc: exclude", !fields.ContainsKey("X"));
        Check("toc: allow for another edition", Toc.Parse("## Title: Era [AllowLoadGameType vanilla]\n", Era)["Title"] == "Era");

        // Choosing the file.
        var manifest = JsonNode.Parse("""{"releases":[{"filename":"T-nolib.zip","nolib":true,"metadata":[{"flavor":"camelot"}]},{"filename":"T.zip","nolib":false,"metadata":[{"flavor":"camelot"},{"flavor":"vanilla"}]},{"filename":"T-mainline.zip","nolib":false,"metadata":[{"flavor":"mainline"}]}]}""");
        string[] zips = ["T.zip", "T-nolib.zip", "T-mainline.zip"];
        Check("pick: the only zip", Checker.Pick(["One.zip"], null, Retail) == "One.zip");
        Check("pick: release.json, not the no-lib build", Checker.Pick(zips, manifest, Forever) == "T.zip");
        Check("pick: release.json for another edition", Checker.Pick(zips, manifest, Retail) == "T-mainline.zip");
        Check("pick: file words", Checker.Pick(["T-era.zip", "T-retail.zip"], null, Era) == "T-era.zip");
        Check("pick: unclear", Checker.Pick(["T-a.zip", "T-b.zip"], null, Forever) is null);
        Check("pre-release tags", Checker.IsPreRelease("v1.2-beta2") && Checker.IsPreRelease("1.0.Alpha") && !Checker.IsPreRelease("v1.2.3") && !Checker.IsPreRelease("alphabet-1"));
        Check("scope", Checker.InScope(Candidate("forever"), "forever") && !Checker.InScope(Candidate("retail"), "forever") && Checker.InScope(Candidate("retail"), "all"));

        // A release that passes, for each edition it ships for.
        var good = Zip(("Thing/Thing.toc", "## Interface: 11509, 16001\n## Dependencies: Ace3, Blizzard_Foo, ThingLib\n"),
            ("ThingLib/ThingLib.toc", "## Interface: 16001, 11509\n## LoadOnDemand: 1\n"), ("readme.txt", "loose"));
        var passed = await Run(Candidate("forever", "classic_era"), Release("v2", Old, ("Thing-v2.zip", good, null)));
        Check("pass: state", Str(passed["state"]) == "checked" && Str(passed["release"]) == "v2");
        Check("pass: forever and classic_era", Result(passed, "forever") == "passed" && Result(passed, "classic_era") == "passed");
        Check("pass: retail not recorded", passed["results"]?["retail"] is null);
        Check("pass: a PTR with only the live Interface is not updated yet", Result(passed, "classic_era_ptr") == "not-updated"
            && Str(passed["results"]!["classic_era_ptr"]!["interface"]) == "11509");
        var f = passed["results"]!["forever"]!;
        Check("pass: file, size, SHA-256", Str(f["file"]) == "Thing-v2.zip" && f["size"]!.GetValue<long>() == good.LongLength && Str(f["sha256"]) == Sha(good));
        Check("pass: main folder and needs", Str(f["main"]) == "Thing" && f["needs"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["Ace3"]));
        Check("pass: good, with the check time", Str(passed["good"]?["forever"]?["release"]) == "v2" && passed["good"]!["forever"]!["upToDate"]!.GetValue<bool>()
            && Str(passed["good"]!["forever"]!["checkedAt"]) == "2026-11-26T12:00:00Z" && !passed["good"]!["classic_era_ptr"]!["upToDate"]!.GetValue<bool>());

        // Blockers: each fails with its reason, and the last good release stays.
        async Task Blocked(string name, byte[] zip, string reasonPart, string? digest = null)
        {
            var entry = await Run(Candidate("forever"), Release("v3", Old, ("Thing-v3.zip", zip, digest)), passed);
            Check($"{name}: failed", Result(entry, "forever") == "failed");
            Check($"{name}: reason ({Reason(entry, "forever")})", Reason(entry, "forever").Contains(reasonPart, StringComparison.Ordinal));
            Check($"{name}: last good kept", Str(entry["good"]?["forever"]?["release"]) == "v2");
        }
        await Blocked("digest", good, "isn't the", digest: "sha256:" + new string('0', 64));
        await Blocked("program", Zip(("Thing/Thing.toc", "## Interface: 16001\n"), ("Thing/tool.exe", "MZ")), "never need");
        await Blocked("unsafe path", Zip(("Thing/Thing.toc", "## Interface: 16001\n"), ("../evil.lua", "x")), "outside");
        await Blocked("no folder", Zip(("Thing.toc", "## Interface: 16001\n")), "no add-on folder");
        await Blocked("no .toc for the edition", Zip(("Thing/Thing_Mainline.toc", "## Interface: 120105\n")), "no .toc for this edition");
        await Blocked("Interface not listed", Zip(("Thing/Thing.toc", "## Interface: 11509\n")), "not 16001");
        await Blocked("another edition's Interface", Zip(("Thing/Thing.toc", "## Interface: 11500\n")), "not 16001");
        await Blocked("folder without a .toc", Zip(("Thing/Thing.toc", "## Interface: 16001\n"), ("Extra/x.lua", "x")), "Extra: no .toc file");
        await Blocked("not a zip", Encoding.UTF8.GetBytes("not a zip"), "not a readable zip");
        var noZip = await Run(Candidate("forever"), Release("v3", Old), passed);
        Check("no zip", Result(noZip, "forever") == "failed" && Reason(noZip, "forever").Contains("no .zip", StringComparison.Ordinal) && Str(noZip["good"]?["forever"]?["release"]) == "v2");

        // A folder only another folder lists as optional may lack this edition's .toc.
        var optional = await Run(Candidate("forever"), Release("v2", Old, ("T.zip", Zip(("Thing/Thing.toc", "## Interface: 16001\n## OptionalDeps: ThingTrace\n"), ("ThingTrace/ThingTrace_Mainline.toc", "## Interface: 120105\n")), null)));
        Check("optional folder", Result(optional, "forever") == "passed");

        // An earlier patch's Interface is "not updated yet".
        var older = await Run(Candidate("forever"), Release("v2", Old, ("T.zip", Zip(("Thing/Thing.toc", "## Interface: 16000\n")), null)));
        Check("not updated", Result(older, "forever") == "not-updated" && !older["good"]!["forever"]!["upToDate"]!.GetValue<bool>());

        // Several files: each edition gets its own.
        var era = Zip(("Thing/Thing_Vanilla.toc", "## Interface: 11509\n"));
        var forever = Zip(("Thing/Thing_Camelot.toc", "## Interface: 16001\n"));
        var several = await Run(Candidate("forever", "classic_era"), Release("v2", Old, ("Thing-era.zip", era, null), ("Thing-forever.zip", forever, null)));
        Check("several files", Str(several["results"]!["forever"]!["file"]) == "Thing-forever.zip" && Result(several, "forever") == "passed"
            && Str(several["results"]!["classic_era"]!["file"]) == "Thing-era.zip" && Result(several, "classic_era") == "passed");
        var unclear = await Run(Candidate("forever"), Release("v2", Old, ("A.zip", forever, null), ("B.zip", era, null)));
        Check("several files, none marked", Result(unclear, "forever") == "failed" && Reason(unclear, "forever").Contains("none is marked", StringComparison.Ordinal));

        // A size that isn't what GitHub lists.
        var sized = Release("v3", Old, ("T.zip", good, null));
        sized.Release["assets"]![0]!["size"] = 5L;
        var wrongSize = await Run(Candidate("forever"), sized, passed);
        Check("size", Result(wrongSize, "forever") == "failed" && Reason(wrongSize, "forever").Contains("GitHub lists 5", StringComparison.Ordinal));

        // Not checked this run: the last result stays.
        var preEntry = await Run(Candidate("forever"), Release("v3-beta", Old, ("T.zip", good, null)), passed);
        Check("pre-release", Str(preEntry["state"]) == "pre-release" && Str(preEntry["good"]?["forever"]?["release"]) == "v2" && Result(preEntry, "forever") == "passed");
        var flagged = Release("v3", Old, ("T.zip", good, null));
        flagged.Release["prerelease"] = true;
        Check("pre-release flag", Str((await Run(Candidate("forever"), flagged, passed))["state"]) == "pre-release");
        var held = await Run(Candidate("forever"), Release("v3", "2026-11-26T09:00:00Z", ("T.zip", good, null)), passed);
        Check("held", Str(held["state"]) == "held" && Str(held["reason"])!.Contains("2026-11-26T15:00:00Z", StringComparison.Ordinal)
            && Str(held["good"]?["forever"]?["release"]) == "v2");
        var heldOver = await Run(Candidate("forever"), Release("v3", "2026-11-26T05:00:00Z", ("T.zip", good, null)), passed);
        Check("hold over", Str(heldOver["state"]) == "checked" && Str(heldOver["release"]) == "v3");
        var offline = await Run(Candidate("forever"), Release("v3", Old, ("T.zip", good, null)), passed, offline: true);
        Check("download failed", Str(offline["state"]) == "error" && Str(offline["good"]?["forever"]?["release"]) == "v2" && Result(offline, "forever") == "passed");
        var unread = await Run(Candidate("forever"), Release("v3", Old), passed, problem: "GitHub answered 502");
        Check("release unreadable", Str(unread["state"]) == "error" && unread["good"]?["forever"] is not null);
        var none = await Checker.CheckAsync(new(Candidate("forever"), null, null, passed, Editions, Rules, Now, TimeSpan.Zero), _ => throw new InvalidOperationException());
        Check("no release", Str(none["state"]) == "no-release" && none["good"]!.AsObject().Count == 0);

        // Checked once: the same release with the same rules isn't downloaded again; new rules check it again.
        var again = await Checker.CheckAsync(new(Candidate("forever", "classic_era"), Release("v2", Old, ("Thing-v2.zip", good, null)).Release, null, passed, Editions, Rules, Now, TimeSpan.Zero),
            _ => throw new InvalidOperationException("downloaded again"));
        Check("checked once", ReferenceEquals(again, passed));
        var rechecked = await Checker.CheckAsync(new(Candidate("forever"), Release("v2", Old, ("Thing-v2.zip", good, null)).Release, null, passed, Editions, "other", Now, TimeSpan.Zero),
            _ => Task.FromResult(new Download(good, null)));
        Check("new rules: checked again", Str(rechecked["rules"]) == "other" && Result(rechecked, "forever") == "passed");

        // Latest releases read with GraphQL, in the REST shape the check reads.
        var query = Releases.Query(["a/one", "b/two"]);
        Check("graphql: query", query.Contains("r0: repository(owner: \"a\", name: \"one\")", StringComparison.Ordinal) && query.Contains("r1:", StringComparison.Ordinal));
        var answer = JsonNode.Parse("""
            {"data":{"r0":{"latestRelease":{"tagName":"v2","publishedAt":"2026-11-20T10:00:00Z","isPrerelease":false,"isDraft":false,
              "releaseAssets":{"nodes":[{"name":"T.zip","size":1234,"downloadUrl":"https://github.com/a/one/releases/download/v2/T.zip","digest":"sha256:00ff"}]}}},
              "r1":{"latestRelease":null},"r2":null,"r3":null},
             "errors":[{"type":"NOT_FOUND","path":["r2"],"message":"Could not resolve"},{"type":"SERVICE_UNAVAILABLE","path":["r3"],"message":"busy"}]}
            """);
        var read = Releases.Results(["a/one", "b/two", "c/gone", "d/busy"], answer);
        var one = read["a/one"].Release;
        Check("graphql: release", Str(one?["tag_name"]) == "v2" && Str(one?["assets"]?[0]?["browser_download_url"]) == "https://github.com/a/one/releases/download/v2/T.zip"
            && one!["assets"]![0]!["size"]!.GetValue<long>() == 1234 && Str(one["assets"]![0]!["digest"]) == "sha256:00ff" && read["a/one"].Problem is null);
        Check("graphql: no release", read["b/two"] is (null, null));
        Check("graphql: gone", read["c/gone"].Release is null && read["c/gone"].Problem!.Contains("gone", StringComparison.Ordinal));
        Check("graphql: error", read["d/busy"].Problem!.Contains("busy", StringComparison.Ordinal));
        Check("graphql: no answer", Releases.Results(["a/one"], null)["a/one"].Problem is not null);

        // checks.json.
        var written = Checks.Write(Now, "forever", [passed]);
        Check("checks.json", Checks.Read(written).Count == 1 && written.EndsWith("}\n", StringComparison.Ordinal) && !written.Contains('\r'));

        Console.WriteLine(failures == 0 ? "check.cs self-test passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
