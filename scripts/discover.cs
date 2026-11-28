#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Finds add-ons for every edition on public GitHub repos and writes candidates.json, which the release check works from.
// It lists nothing by itself and never edits catalog.json.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md), with a GitHub token in GITHUB_TOKEN:
//   ... dotnet run scripts/discover.cs                               # every edition in ./catalog.json
//   ... dotnet run scripts/discover.cs -- --edition forever          # one edition (repeatable), for a quicker run
//   ... dotnet run scripts/discover.cs -- --no-search                # only re-read the repos already in candidates.json
//   ... dotnet run scripts/discover.cs -- --search-minutes 60        # stop starting searches after an hour
//   ... dotnet run scripts/discover.cs -- --part search              # search, then re-read only the repos that changed
//   ... dotnet run scripts/discover.cs -- --part stats               # only download counts and funding links
//   ... dotnet run scripts/discover.cs -- --self-test                # offline tests
// Options: --catalog <file> (default ./catalog.json), --optout <file> (default ./optout.json), --overrides <folder>
// (default ./overrides), --out <file> (default
// ./candidates.json, also read as the previous run), --summary <file> (also append the report there, as the workflow does
// with $GITHUB_STEP_SUMMARY).
//
// Official APIs only, read-only:
// 1. Code search for .toc files listing each edition's current Interface, split by file size so no query passes the
//    1,000-result cap. Code search allows 10 requests a minute, so searches are paced under it and refusals are waited
//    out as GitHub says (Pacing). The Interface searched longest ago goes first; one whose search didn't finish (out of
//    time, or a request that still failed) keeps its date, so the next run starts with it. Its repos found before are kept.
// 2. GraphQL, 10 repos a query: last push, archived or fork, description and topics, the latest release and its files,
//    the newest tags, .pkgmeta, and the .toc files themselves (read again at HEAD, so a stale search index doesn't count).
// 3. README.md's first sentence, only for repos whose main .toc has no Notes and that have no description: the
//    generator's last fallback for a summary. (Reading every README makes the queries heavy enough to time out.)
// Parts (--part): "search" searches, then reads new repos and re-reads only those whose last push or Latest release
// changed (one small query for 100 repos tells), keeping every other row and the stats as they were; "stats" reads
// nothing but each repo's download counts (with a 30-day history of daily totals) and its funding links' answers,
// keeping everything else. With no part, a run does all of it and re-reads every repo.
// Repos the catalog already lists (its GitHub sources, and the "repo" of each file in overrides/) are read every run too:
// code search misses some repos. Their .toc files are found from the repo's tree (the root and one folder down). Forks
// are dropped, except one an override names: a person checked it's the maintained continuation of an add-on.
// Repos from the previous run are read again even when search misses them, and a repo whose read fails keeps its
// previous row, so a re-run changes only what changed. Repos on the never-add list (optout.json) are skipped.
// Exit code 0 when candidates.json was written, 1 when an input can't be read, 2 for bad arguments or no token.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    return await SelfTest.Run();
}

string catalogArg = "catalog.json", optoutArg = "optout.json", outArg = "candidates.json", overridesArg = "overrides";
string? summaryArg = null;
var search = true;
TimeSpan? searchTime = null;
string? part = null;
var onlyEditions = new List<string>();
var onlyRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--catalog" when value is not null: catalogArg = value; i++; break;
        case "--overrides" when value is not null: overridesArg = value; i++; break;
        case "--optout" when value is not null: optoutArg = value; i++; break;
        case "--out" when value is not null: outArg = value; i++; break;
        case "--summary" when value is not null: summaryArg = value; i++; break;
        case "--edition" when value is not null: onlyEditions.Add(value.Trim()); i++; break;
        case "--no-search": search = false; break;
        case "--part" when value is "search" or "stats": part = value; i++; break;
        case "--search-minutes" when int.TryParse(value, out var minutes) && minutes > 0: searchTime = TimeSpan.FromMinutes(minutes); i++; break;
        // Reads only these repos again (no search) and keeps every other row as it is.
        case "--repo" when value is not null: onlyRepos.Add(value.Trim()); search = false; i++; break;
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/discover.cs [--edition <id>]... [--no-search] [--search-minutes <n>] [--part search|stats] [--repo <owner/name>]... [--catalog <file>] [--optout <file>] [--out <file>] [--summary <file>] | --self-test");
            return 2;
    }
}

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("GITHUB_TOKEN isn't set: code search and GraphQL need a token.");
    return 2;
}

SortedDictionary<string, string> editions;
OptOut optout;
Dictionary<string, JsonObject> previous;
Dictionary<string, string> searched;
JsonObject? previousEditions;
List<string> seeds, forks;
try
{
    var catalogNode = JsonNode.Parse(File.ReadAllText(catalogArg))!;
    editions = Inputs.Editions(catalogNode);
    var overrideNodes = Directory.Exists(overridesArg)
        ? Directory.GetFiles(overridesArg, "*.json").Order(StringComparer.Ordinal).Select(f => JsonNode.Parse(File.ReadAllText(f))).ToList() : [];
    seeds = Inputs.Seeds(catalogNode, overrideNodes);
    forks = Inputs.Seeds(JsonNode.Parse("{}")!, overrideNodes);
    optout = File.Exists(optoutArg) ? OptOut.Parse(File.ReadAllText(optoutArg)) : new([]);
    var previousText = File.Exists(outArg) ? File.ReadAllText(outArg) : null;
    previous = previousText is null ? new(StringComparer.OrdinalIgnoreCase) : Candidates.Read(previousText);
    searched = previousText is null ? [] : Candidates.Searched(previousText);
    previousEditions = previousText is null ? null : JsonNode.Parse(previousText)!["editions"] as JsonObject;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NullReferenceException or FormatException)
{
    Console.Error.WriteLine($"Can't read the inputs: {e.Message}");
    return 1;
}
foreach (var id in onlyEditions.Where(id => !editions.ContainsKey(id)))
{
    Console.Error.WriteLine($"No edition '{id}' in the catalog (it has {string.Join(", ", editions.Keys)}).");
    return 2;
}

var log = new RunLog();
using var github = new GitHub(token);
if (part == "stats")
{
    var statRows = previous.Values.Select(r => r.DeepClone().AsObject()).ToList();
    await github.ReadDownloadsAsync(statRows, previous);
    await github.CheckSponsorsAsync(statRows, previous);
    await Funding.ResolveShortLinksAsync(statRows, previous);
    File.WriteAllText(outArg, Candidates.Write(editions, statRows, searched));
    log.Searched = searched;
    var statReport = log.Report(statRows, editions, false, "stats");
    Console.Write(statReport);
    if (summaryArg is not null)
    {
        File.AppendAllText(summaryArg, statReport);
    }
    return 0;
}
var found = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
if (search)
{
    var interfaces = Search.Order(editions.Where(e => onlyEditions.Count == 0 || onlyEditions.Contains(e.Key)).Select(e => e.Value), searched);
    var deadline = DateTime.UtcNow + (searchTime ?? TimeSpan.MaxValue - TimeSpan.FromDays(1));
    foreach (var iface in interfaces)
    {
        Console.Error.WriteLine($"Searching for .toc files listing {iface} ...");
        if (await Search.CollectAsync(github.SearchCodeAsync, $"Interface {iface} extension:toc", found, log, () => DateTime.UtcNow > deadline))
        {
            searched[iface] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
    }
}
log.Searched = searched;
foreach (var (repo, row) in previous)
{
    var paths = found.TryGetValue(repo, out var p) ? p : found[repo] = new(StringComparer.Ordinal);
    foreach (var toc in row["tocs"]?.AsArray() ?? [])
    {
        paths.Add(toc!["path"]!.GetValue<string>());
    }
}
if (onlyRepos.Count > 0)
{
    foreach (var repo in found.Keys.Where(r => !onlyRepos.Contains(r)).ToList())
    {
        found.Remove(repo);
    }
    seeds = [.. onlyRepos];
}
var unseen = seeds.Where(s => !found.ContainsKey(s)).ToList();
if (unseen.Count > 0)
{
    Console.Error.WriteLine($"Listing the .toc files of {unseen.Count} known repos search missed ...");
    foreach (var (repo, paths) in await github.ListTocsAsync(unseen))
    {
        found[repo] = new(paths, StringComparer.Ordinal);
    }
}
foreach (var repo in found.Keys.Where(optout.Covers).ToList())
{
    found.Remove(repo);
    log.Drop(repo, "on the never-add list");
}

// The search part re-reads only repos that changed, unless the editions' Interfaces did (every row's editions then can).
var unchanged = new List<JsonObject>();
if (part == "search" && Refresh.SameEditions(previousEditions, editions))
{
    var known = found.Keys.Where(previous.ContainsKey).ToList();
    Console.Error.WriteLine($"Asking which of {known.Count} known repos changed ...");
    var now = await github.ReadChangesAsync(known);
    foreach (var repo in known.Where(r => Refresh.Unchanged(previous[r], now.GetValueOrDefault(r))))
    {
        found.Remove(repo);
        unchanged.Add(previous[repo].DeepClone().AsObject());
    }
    log.Unchanged = unchanged.Count;
}

Console.Error.WriteLine($"Reading {found.Count} repos ...");
var reads = await github.ReadReposAsync(found, log);
var rows = Candidates.Build(found.Keys, reads, previous, editions, optout, log, forks);
await github.ReadReadmesAsync(rows, previous);
if (part == "search")
{
    // A repo moved to another owner or name is read under its old name: its stats are under that name.
    var oldNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, node) in reads)
    {
        if (node?["nameWithOwner"]?.GetValue<string>() is { } name && !name.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            oldNames.TryAdd(name, key);
        }
    }
    foreach (var row in rows)
    {
        var name = row["repo"]!.GetValue<string>();
        Stats.Carry(row, previous.GetValueOrDefault(name) ?? (oldNames.TryGetValue(name, out var old) ? previous.GetValueOrDefault(old) : null));
    }
    var read = rows.Select(r => r["repo"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    rows.AddRange(unchanged.Where(r => !read.Contains(r["repo"]!.GetValue<string>())));
}
else
{
    await github.ReadDownloadsAsync(rows, previous);
    await github.CheckSponsorsAsync(rows, previous);
    await Funding.ResolveShortLinksAsync(rows, previous);
}
if (onlyRepos.Count > 0)
{
    var read = rows.Select(r => r["repo"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    rows.AddRange(previous.Where(p => !onlyRepos.Contains(p.Key) && !read.Contains(p.Key)).Select(p => p.Value.DeepClone().AsObject()));
}
File.WriteAllText(outArg, Candidates.Write(editions, rows, searched));

var report = log.Report(rows, editions, search, part);
Console.Write(report);
if (summaryArg is not null)
{
    File.AppendAllText(summaryArg, report);
}
return 0;

/// <summary>One page of code search: the total GitHub reports and the files on this page.</summary>
record SearchPage(int Total, List<(string Repo, string Path)> Items);

/// <summary>What happened in a run, for the report.</summary>
sealed class RunLog
{
    public List<string> Failed { get; } = [];
    public List<string> Unfinished { get; } = [];
    public int Unchanged { get; set; }
    public IReadOnlyDictionary<string, string> Searched { get; set; } = new Dictionary<string, string>();
    public List<(string Query, int Total)> Capped { get; } = [];
    public SortedDictionary<string, List<string>> Dropped { get; } = new(StringComparer.Ordinal);
    public List<string> Kept { get; } = [];
    public int Requests { get; set; }

    public void Drop(string repo, string reason)
    {
        if (!Dropped.TryGetValue(reason, out var list))
        {
            Dropped[reason] = list = [];
        }
        list.Add(repo);
    }

    public string Report(IReadOnlyCollection<JsonObject> rows, IReadOnlyDictionary<string, string> editions, bool searched, string? part = null)
    {
        var md = new StringBuilder(part is null ? "## Discovery\n\n" : $"## Discovery: {part}\n\n");
        if (part == "stats")
        {
            md.AppendLine($"{rows.Count} candidate repos: download counts and funding links read again; everything else kept.").AppendLine();
        }
        else
        {
            md.AppendLine($"{rows.Count} candidate repos{(searched ? "" : " (no search: the previous run's repos read again)")}, from {Requests} code search requests.").AppendLine();
        }
        if (Unchanged > 0)
        {
            md.AppendLine($"- Unchanged since the last read (kept, not read again): {Unchanged}");
        }
        md.AppendLine("| Edition | Interface | Repos | Last full search |").AppendLine("|---|---|---|---|");
        foreach (var (id, iface) in editions)
        {
            md.AppendLine($"| {id} | {iface} | {rows.Count(r => r["editions"]!.AsArray().Any(e => e!.GetValue<string>() == id))} | {(Searched.TryGetValue(iface, out var at) ? at : "never")} |");
        }
        md.AppendLine();
        foreach (var (reason, repos) in Dropped)
        {
            md.AppendLine($"- Dropped, {reason}: {repos.Count}");
        }
        if (Kept.Count > 0)
        {
            md.AppendLine($"- Kept from the previous run (couldn't be read this time): {Kept.Count}");
        }
        foreach (var (query, total) in Capped)
        {
            md.AppendLine($"- **Capped:** `{query}` has {total} results, more than the 1,000 code search returns; some files were missed.");
        }
        foreach (var query in Failed)
        {
            md.AppendLine($"- **Failed:** `{query}` (its Interface is searched again first next run)");
        }
        foreach (var query in Unfinished)
        {
            md.AppendLine($"- **Out of time:** `{query}` wasn't searched in full (its Interface goes first next run)");
        }
        return md.AppendLine().ToString();
    }
}

static class Inputs
{
    /// <summary>The GitHub repos the catalog already knows: its GitHub sources and the overrides' "repo", once each.</summary>
    public static List<string> Seeds(JsonNode catalog, IEnumerable<JsonNode?> overrides) =>
        (catalog["addons"]?.AsArray() ?? [])
            .SelectMany(a => a?["sources"]?.AsArray() ?? [])
            .Where(s => s?["type"]?.GetValue<string>() == "github")
            .Select(s => s!["repo"]?.GetValue<string>())
            .Concat(overrides.Select(o => o?["repo"]?.GetValue<string>()))
            .OfType<string>().Where(r => r.Contains('/')).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Each live edition's ID and current Interface, from catalog.json's <c>editions</c>. Test clients (with <c>of</c>) come later.</summary>
    public static SortedDictionary<string, string> Editions(JsonNode catalog)
    {
        var editions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, edition) in catalog["editions"]!.AsObject())
        {
            if (edition!["of"] is not null)
            {
                continue;
            }
            editions[id] = edition["interface"]!.GetValue<string>();
        }
        if (editions.Count == 0)
        {
            throw new FormatException("catalog.json has no editions.");
        }
        return editions;
    }
}

/// <summary>The never-add list: <c>owner/name</c> skips one repo, <c>owner</c> every repo of that account.</summary>
sealed record OptOut(IReadOnlyList<string> Entries)
{
    public static OptOut Parse(string json)
    {
        var entries = new List<string>();
        foreach (var item in JsonNode.Parse(json)!["repos"]!.AsArray())
        {
            var repo = item!["repo"]!.GetValue<string>().Trim();
            var parts = repo.Split('/');
            if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            {
                throw new FormatException($"optout.json: '{repo}' isn't an owner or owner/name.");
            }
            entries.Add(repo);
        }
        return new(entries);
    }

    public bool Covers(string repo) => Entries.Any(e => e.Contains('/')
        ? string.Equals(e, repo, StringComparison.OrdinalIgnoreCase)
        : repo.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase));
}

static class Toc
{
    /// <summary>
    /// The <c>## Key: Value</c> lines of a .toc; the first of a key wins, keys ignore case. Interface is the exception: the
    /// values of every Interface line are kept, joined with commas (a first line can be packager placeholders).
    /// </summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("##"))
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }
            var key = line[2..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("Interface", StringComparison.OrdinalIgnoreCase) && fields.TryGetValue(key, out var before))
            {
                fields[key] = before + "," + value;
            }
            else if (key.Length > 0)
            {
                fields.TryAdd(key, value);
            }
        }
        return fields;
    }

    public static List<string> List(string? value) =>
        (value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> Interfaces(IReadOnlyDictionary<string, string> fields) =>
        fields.TryGetValue("Interface", out var v)
            ? v.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.All(char.IsAsciiDigit)).Distinct().ToList()
            : [];

    /// <summary>Required dependencies: <c>Dependencies</c>, <c>RequiredDeps</c> and any <c>Dep…</c> key.</summary>
    public static List<string> Dependencies(IReadOnlyDictionary<string, string> fields) =>
        fields.Where(f => f.Key.Equals("Dependencies", StringComparison.OrdinalIgnoreCase) || f.Key.Equals("RequiredDeps", StringComparison.OrdinalIgnoreCase)
                || (f.Key.StartsWith("Dep", StringComparison.OrdinalIgnoreCase) && !f.Key.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => List(f.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The .toc that speaks for the repo: fewest folders deep, then shortest, then by name.</summary>
    public static int Compare(string a, string b)
    {
        var depth = a.Count(c => c == '/').CompareTo(b.Count(c => c == '/'));
        if (depth != 0)
        {
            return depth;
        }
        var length = a.Length.CompareTo(b.Length);
        return length != 0 ? length : string.CompareOrdinal(a, b);
    }
}

static class Search
{
    /// <summary>Code search indexes files up to 384 KB.</summary>
    public const long MaxSize = 384 * 1024;
    const int Cap = 1000;
    /// <summary>The first size range: most .toc files are a few hundred bytes.</summary>
    const long FirstWidth = 256;

    /// <summary>The Interfaces to search, each once: never searched in full first, then the one searched longest ago.</summary>
    public static List<string> Order(IEnumerable<string> interfaces, IReadOnlyDictionary<string, string> searched) =>
        interfaces.Distinct().Select((iface, i) => (iface, i))
            .OrderBy(x => searched.TryGetValue(x.iface, out var at) ? at : "", StringComparer.Ordinal).ThenBy(x => x.i)
            .Select(x => x.iface).ToList();

    /// <summary>
    /// Collects every file the query finds, walking up the file sizes in ranges that stay under the cap: a range over it
    /// is halved and asked again, and after one well under it the next is twice as wide. A request that still fails is
    /// reported and the walk goes on; when <paramref name="outOfTime"/> says so, it stops. Returns whether the search
    /// finished with nothing missed. What it found is kept either way.
    /// </summary>
    public static async Task<bool> CollectAsync(Func<string, int, Task<SearchPage?>> page, string query, Dictionary<string, SortedSet<string>> found, RunLog log,
        Func<bool>? outOfTime = null)
    {
        var complete = true;
        long lo = 0, width = FirstWidth;
        while (lo <= MaxSize)
        {
            if (outOfTime?.Invoke() == true)
            {
                log.Unfinished.Add($"{query} size:{lo}..{MaxSize}");
                return false;
            }
            var hi = Math.Min(lo + width - 1, MaxSize);
            var q = $"{query} size:{lo}..{hi}";
            var first = await page(q, 1);
            if (first is null)
            {
                log.Failed.Add(q);
                complete = false;
                lo = hi + 1;
                continue;
            }
            if (first.Total > Cap && hi > lo)
            {
                width = Math.Max(1, (hi - lo + 1) / 2);
                continue;
            }
            if (first.Total > Cap)
            {
                log.Capped.Add((q, first.Total));
            }
            var items = new List<(string Repo, string Path)>(first.Items);
            for (var n = 2; items.Count < Math.Min(first.Total, Cap) && n <= Cap / 100; n++)
            {
                if (outOfTime?.Invoke() == true)
                {
                    log.Unfinished.Add($"{q} (page {n})");
                    Add(items);
                    return false;
                }
                var more = await page(q, n);
                if (more is null)
                {
                    log.Failed.Add($"{q} (page {n})");
                    complete = false;
                    break;
                }
                if (more.Items.Count == 0)
                {
                    break;
                }
                items.AddRange(more.Items);
            }
            Add(items);
            lo = hi + 1;
            if (first.Total < Cap / 2)
            {
                width *= 2;
            }
        }
        return complete;

        void Add(List<(string Repo, string Path)> items)
        {
            foreach (var (repo, path) in items)
            {
                if (!found.TryGetValue(repo, out var paths))
                {
                    found[repo] = paths = new(StringComparer.Ordinal);
                }
                paths.Add(path);
            }
        }
    }
}

static class Candidates
{
    /// <summary>At most this many .toc files are read per repo, the shallowest first (compilations hold hundreds).</summary>
    public const int MaxTocs = 12;

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
        foreach (var row in JsonNode.Parse(json)!["candidates"]!.AsArray())
        {
            rows[row!["repo"]!.GetValue<string>()] = row.AsObject();
        }
        return rows;
    }

    /// <summary>When each Interface was last searched in full, from <c>searched</c>.</summary>
    public static Dictionary<string, string> Searched(string json) =>
        (JsonNode.Parse(json)!["searched"]?.AsObject() ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());

    /// <summary>The file. <paramref name="searched"/> keeps only the editions' current Interfaces.</summary>
    public static string Write(IReadOnlyDictionary<string, string> editions, IEnumerable<JsonObject> rows, IReadOnlyDictionary<string, string>? searched = null)
    {
        var root = new JsonObject
        {
            ["$comment"] = "Written by scripts/discover.cs. Never edit it by hand.",
            ["editions"] = new JsonObject(editions.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))),
            ["searched"] = new JsonObject((searched ?? new Dictionary<string, string>())
                .Where(p => editions.Values.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["candidates"] = new JsonArray(rows.OrderBy(r => r["repo"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .Select(r => (JsonNode)r.DeepClone()).ToArray()),
        };
        return root.ToJsonString(Options) + "\n";
    }

    /// <summary>
    /// The rows for this run. <paramref name="reads"/> maps a repo to its GraphQL node, or to null when GitHub says it's
    /// gone; a repo missing from it couldn't be read, and keeps its previous row.
    /// </summary>
    public static List<JsonObject> Build(IEnumerable<string> repos, IReadOnlyDictionary<string, JsonNode?> reads, IReadOnlyDictionary<string, JsonObject> previous,
        IReadOnlyDictionary<string, string> editions, OptOut optout, RunLog log, IEnumerable<string>? known = null)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var knownRepos = new HashSet<string>(known ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
        {
            if (!reads.TryGetValue(repo, out var node))
            {
                if (previous.TryGetValue(repo, out var old))
                {
                    rows[repo] = old;
                    log.Kept.Add(repo);
                }
                continue;
            }
            var (row, reason) = Row(repo, node, editions, knownRepos.Contains(repo));
            if (row is null)
            {
                log.Drop(repo, reason!);
                continue;
            }
            var name = row["repo"]!.GetValue<string>();
            if (optout.Covers(name))
            {
                log.Drop(name, "on the never-add list");
                continue;
            }
            // A renamed repo can be found under its old and new names: one row, with the .toc files of both.
            if (rows.TryGetValue(name, out var twin))
            {
                row = Merge(twin, row);
            }
            rows[name] = row;
        }
        return rows.Values.ToList();
    }

    /// <summary>Notes (and each localized Notes) in "tocs" are cut to this many characters, to keep candidates.json small.</summary>
    const int MaxNotesInTocs = 300;

    /// <summary>A .toc's fields as candidates.json keeps them; with interfaces for "tocs", without for the main "toc".</summary>
    static JsonObject TocFields(string path, Dictionary<string, string> fields, JsonArray? interfaces, int maxNotes = int.MaxValue)
    {
        string? Field(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        string? Cut(string? s) => s is null || s.Length <= maxNotes ? s : s[..maxNotes].TrimEnd() + "…";
        var localized = new JsonObject(fields.Where(f => f.Key.StartsWith("Notes-", StringComparison.OrdinalIgnoreCase) && f.Value.Length > 0)
            .OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => KeyValuePair.Create(f.Key[6..], (JsonNode?)Cut(f.Value))));
        var toc = new JsonObject { ["path"] = path };
        if (interfaces is not null)
        {
            toc["interfaces"] = interfaces;
        }
        toc["title"] = Field("Title");
        toc["author"] = Field("Author");
        toc["notes"] = Cut(Field("Notes"));
        toc["localizedNotes"] = localized.Count > 0 ? localized : null;
        toc["version"] = Field("Version");
        toc["category"] = Field("Category");
        toc["iconTexture"] = Field("IconTexture");
        toc["curseforge"] = Field("X-Curse-Project-ID");
        toc["wago"] = Field("X-Wago-ID");
        toc["wowi"] = Field("X-WoWI-ID");
        toc["catalog"] = Field("X-Forever-Catalog"); // the author's own yes or no to being listed
        toc["dependencies"] = Strings(Toc.Dependencies(fields));
        toc["optionalDependencies"] = Strings(Toc.List(Field("OptionalDeps")));
        return toc;
    }

    static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var tocs = a["tocs"]!.AsArray().Concat(b["tocs"]!.AsArray())
            .GroupBy(t => t!["path"]!.GetValue<string>()).Select(g => g.First()!)
            .OrderBy(t => t["path"]!.GetValue<string>(), Comparer<string>.Create(Toc.Compare)).ToList();
        var merged = b.DeepClone().AsObject();
        merged["tocs"] = new JsonArray(tocs.Select(t => t.DeepClone()).ToArray());
        merged["editions"] = new JsonArray(a["editions"]!.AsArray().Concat(b["editions"]!.AsArray()).Select(e => e!.GetValue<string>())
            .Distinct().Order(StringComparer.Ordinal).Select(e => (JsonNode)e).ToArray());
        return merged;
    }

    /// <summary>
    /// One repo's row from its GraphQL node, or why it's dropped. A fork is dropped unless an override names it
    /// (<paramref name="known"/>): one is added only after a person checks it's the maintained continuation of an add-on.
    /// </summary>
    public static (JsonObject? Row, string? Reason) Row(string repo, JsonNode? node, IReadOnlyDictionary<string, string> editions, bool known = false)
    {
        if (node is null)
        {
            return (null, "gone or private");
        }
        // A private repo is never listed, even when the token running discovery (a person's own) can read it.
        if (node["isPrivate"]?.GetValue<bool>() == true)
        {
            return (null, "private");
        }
        if (node["isFork"]?.GetValue<bool>() == true && !known)
        {
            return (null, "a fork");
        }
        if (node["isArchived"]?.GetValue<bool>() == true)
        {
            return (null, "archived");
        }

        var tocs = new List<(string Path, Dictionary<string, string> Fields, List<string> Interfaces)>();
        foreach (var toc in node["tocFiles"]?.AsArray() ?? [])
        {
            var text = toc!["text"]?.GetValue<string>();
            if (text is null)
            {
                continue; // deleted since the search index saw it, or not text
            }
            var fields = Toc.Parse(text);
            tocs.Add((toc["path"]!.GetValue<string>(), fields, Toc.Interfaces(fields)));
        }
        var current = tocs.Where(t => t.Interfaces.Any(editions.Values.Contains)).OrderBy(t => t.Path, Comparer<string>.Create(Toc.Compare)).ToList();
        if (current.Count == 0)
        {
            return (null, "no .toc lists a current Interface");
        }

        var listed = editions.Where(e => current.Any(t => t.Interfaces.Contains(e.Value))).Select(e => e.Key).ToList();

        var release = node["latestRelease"];
        var tags = node["refs"]?["nodes"]?.AsArray().OfType<JsonObject>().Select(t => new JsonObject
        {
            ["name"] = t!["name"]!.GetValue<string>(),
            ["date"] = t["target"]?["committedDate"]?.GetValue<string>() ?? t["target"]?["target"]?["committedDate"]?.GetValue<string>(),
        }).ToArray() ?? [];

        var row = new JsonObject
        {
            ["repo"] = node["nameWithOwner"]?.GetValue<string>() ?? repo,
            ["editions"] = Strings(listed),
            ["description"] = node["description"]?.GetValue<string>() is { Length: > 0 } d ? d : null,
            ["topics"] = Strings(node["repositoryTopics"]?["nodes"]?.AsArray().OfType<JsonObject>().Select(t => t["topic"]?["name"]?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>()),
            ["defaultBranch"] = node["defaultBranchRef"]?["name"]?.GetValue<string>(),
            ["pushedAt"] = node["pushedAt"]?.GetValue<string>(),
            ["pkgmeta"] = node["pkgmeta"] is not null,
            ["issues"] = node["hasIssuesEnabled"]?.GetValue<bool>() ?? false,
            ["ownerType"] = node["owner"]?["__typename"]?.GetValue<string>(),
            // History, for trust signals: when the repo and its owner's account were made, its stars, and its releases.
            ["created"] = node["createdAt"]?.GetValue<string>(),
            ["ownerCreated"] = node["owner"]?["createdAt"]?.GetValue<string>(),
            ["stars"] = node["stargazerCount"]?.GetValue<int>(),
            ["releases"] = null, // filled in afterwards by ReadDownloadsAsync, with downloads
            ["fork"] = node["isFork"]?.GetValue<bool>() == true, // only one an override names gets this far
            ["funding"] = Funding.FromNode(node),
            ["readme"] = null, // filled in afterwards, only for repos with no other summary
            ["toc"] = TocFields(current[0].Path, current[0].Fields, null),
            // Every .toc read, with the same fields (Notes cut short): the generator takes an entry's name, summary and
            // icon from its main folder's .toc, which may not be the one picked above, and tells a release's separate
            // add-ons from one add-on's modules by their dependencies.
            ["tocs"] = new JsonArray(tocs.OrderBy(t => t.Path, Comparer<string>.Create(Toc.Compare))
                .Select(t => (JsonNode)TocFields(t.Path, t.Fields, Strings(t.Interfaces), MaxNotesInTocs)).ToArray()),
            ["latestRelease"] = release is null ? null : new JsonObject
            {
                ["tag"] = release["tagName"]?.GetValue<string>(),
                ["name"] = release["name"]?.GetValue<string>(),
                ["publishedAt"] = release["publishedAt"]?.GetValue<string>(),
                ["prerelease"] = release["isPrerelease"]?.GetValue<bool>() ?? false,
                ["sourceLinks"] = History.SourceLinks(release["description"]?.GetValue<string>()),
                ["assets"] = new JsonArray(release["releaseAssets"]?["nodes"]?.AsArray().OfType<JsonObject>().Select(a => (JsonNode)new JsonObject
                {
                    ["name"] = a!["name"]!.GetValue<string>(),
                    ["size"] = a["size"]?.GetValue<long>(),
                    ["url"] = a["downloadUrl"]?.GetValue<string>(),
                    ["digest"] = a["digest"]?.GetValue<string>(),
                    ["downloads"] = a["downloadCount"]?.GetValue<long>(),
                }).ToArray() ?? []),
            },
            ["tags"] = new JsonArray(tags),
            ["downloads"] = null,
        };
        return (row, null);
    }

    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}

/// <summary>
/// GitHub's rules for pacing requests and for a refused one ("Rate limits for the REST API" and "Search" in its REST
/// docs). Code search allows 10 requests a minute, counted in a window that resets each minute; GitHub's servers can
/// disagree on the count, so searches keep a margin under it and also wait for the reset when an answer says none are left.
/// </summary>
static class Pacing
{
    /// <summary>The least time between code searches: 8 a minute, under the limit of 10.</summary>
    public static readonly TimeSpan SearchGap = TimeSpan.FromSeconds(7.5);
    /// <summary>Tries per request. Refusals with no time given wait 1, 2, 4, 8 and 16 minutes, about half an hour in all.</summary>
    public const int Attempts = 6;
    static readonly TimeSpan Margin = TimeSpan.FromSeconds(3);

    /// <summary>How long after a code search's answer the next one may go.</summary>
    public static TimeSpan BeforeNextSearch(string? remaining, string? reset, DateTimeOffset now) =>
        remaining == "0" && UntilReset(reset, now) is { } wait && wait > SearchGap ? wait : SearchGap;

    /// <summary>A 429, or a 403 that says it's a rate limit (not a missing permission).</summary>
    public static bool IsRateLimit(int status, string? remaining, bool retryAfter, string body) =>
        status == 429 || status == 403 && (retryAfter || remaining == "0" || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How long to wait after a rate-limit refusal, in GitHub's order: retry-after when it's given; else, when none are
    /// left, until the reset; else at least a minute. Refusals that go on wait exponentially longer (from the third).
    /// </summary>
    public static TimeSpan AfterRefusal(TimeSpan? retryAfter, string? remaining, string? reset, DateTimeOffset now, int attempt)
    {
        if (retryAfter is { } after)
        {
            return after > TimeSpan.FromSeconds(1) ? after : TimeSpan.FromSeconds(1);
        }
        var backoff = TimeSpan.FromMinutes(Math.Pow(2, attempt));
        if (remaining == "0" && UntilReset(reset, now) is { } wait)
        {
            var floor = attempt >= 2 ? TimeSpan.FromMinutes(Math.Pow(2, attempt - 2)) : TimeSpan.Zero;
            return wait > floor ? wait : floor;
        }
        return backoff;
    }

    static TimeSpan? UntilReset(string? reset, DateTimeOffset now)
    {
        if (!long.TryParse(reset, out var at))
        {
            return null;
        }
        var wait = DateTimeOffset.FromUnixTimeSeconds(at) - now + Margin;
        return wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
    }
}

sealed class GitHub : IDisposable
{
    const int BatchSize = 10;

    readonly HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
    DateTimeOffset nextSearch = DateTimeOffset.MinValue;
    int searchRequests;

    public GitHub(string token)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-discover");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public int SearchRequests => searchRequests;

    /// <summary>
    /// Sends a request, waiting out rate limits as GitHub says (Pacing) and retrying server errors; null when it still
    /// fails. A code search also sets when the next one may go.
    /// </summary>
    async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> make, string what, bool search = false)
    {
        for (var attempt = 0; attempt < Pacing.Attempts; attempt++)
        {
            try
            {
                using var response = await http.SendAsync(make());
                var text = await response.Content.ReadAsStringAsync();
                var remaining = Header(response, "x-ratelimit-remaining");
                var reset = Header(response, "x-ratelimit-reset");
                if (search)
                {
                    nextSearch = DateTimeOffset.UtcNow + Pacing.BeforeNextSearch(remaining, reset, DateTimeOffset.UtcNow);
                }
                if (response.IsSuccessStatusCode)
                {
                    return JsonNode.Parse(text);
                }
                var status = (int)response.StatusCode;
                var retryAfter = response.Headers.RetryAfter is { } ra ? ra.Delta ?? ra.Date - DateTimeOffset.UtcNow : null;
                if (Pacing.IsRateLimit(status, remaining, retryAfter is not null, text))
                {
                    var wait = Pacing.AfterRefusal(retryAfter, remaining, reset, DateTimeOffset.UtcNow, attempt);
                    Console.Error.WriteLine($"  {what}: {status}, waiting {wait.TotalSeconds:0} s");
                    await Task.Delay(wait);
                    continue;
                }
                if (status is 500 or 502 or 503 or 504)
                {
                    Console.Error.WriteLine($"  {what}: {status}, trying again");
                    await Task.Delay(TimeSpan.FromSeconds(10 * (attempt + 1)));
                    continue;
                }
                Console.Error.WriteLine($"  {what}: {status}");
                return null;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                Console.Error.WriteLine($"  {what}: {e.GetType().Name}");
                await Task.Delay(TimeSpan.FromSeconds(10 * (attempt + 1)));
            }
        }
        Console.Error.WriteLine($"  {what}: still refused after {Pacing.Attempts} tries");
        return null;
    }

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public async Task<SearchPage?> SearchCodeAsync(string query, int page)
    {
        var wait = nextSearch - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        var url = $"search/code?per_page=100&page={page}&q={Uri.EscapeDataString(query)}";
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), $"search {query} (page {page})", search: true);
        searchRequests++;
        if (json is null)
        {
            return null;
        }
        var items = json["items"]!.AsArray()
            .Where(i => i!["repository"]?["fork"]?.GetValue<bool>() != true)
            .Select(i => (i!["repository"]!["full_name"]!.GetValue<string>(), i["path"]!.GetValue<string>()))
            .ToList();
        Console.Error.WriteLine($"  {query} (page {page}): {json["total_count"]} files");
        return new(json["total_count"]!.GetValue<int>(), items);
    }

    /// <summary>Reads each repo in batched GraphQL queries. A repo absent from the result couldn't be read.</summary>
    public async Task<Dictionary<string, JsonNode?>> ReadReposAsync(Dictionary<string, SortedSet<string>> repos, RunLog log)
    {
        log.Requests = searchRequests;
        var reads = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var names = repos.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        await ReadAsync(names, BatchSize, true);
        return reads;

        async Task ReadAsync(List<string> list, int size, bool progress)
        {
        for (var start = 0; start < list.Count; start += size)
        {
            var chunk = list.Skip(start).Take(size).ToList();
            var paths = chunk.Select(n => repos[n].Order(Comparer<string>.Create(Toc.Compare)).Take(Candidates.MaxTocs).ToList()).ToList();
            var query = GraphQl.Query(chunk, paths);
            var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(new JsonObject { ["query"] = query }.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"graphql {chunk[0]} ...");
            foreach (var (name, node) in GraphQl.Results(chunk, paths, json))
            {
                reads[name] = node;
            }
            if (progress && (start / size) % 20 == 0)
            {
                Console.Error.WriteLine($"  read {Math.Min(start + size, list.Count)}/{list.Count}");
            }
        }
        }
    }

    /// <summary>
    /// Settles each GitHub Sponsors link in the rows' funding: whether the account has a Sponsors listing, and whether it
    /// is the repo's owner or a public member of the organisation that owns it. A link that can't be checked this run
    /// keeps the previous run's answer, else it counts as not checked.
    /// </summary>
    public async Task CheckSponsorsAsync(List<JsonObject> rows, IReadOnlyDictionary<string, JsonObject> previous)
    {
        var links = rows.SelectMany(r => (r["funding"]?.AsArray() ?? []).OfType<JsonObject>()
            .Where(f => f["platform"]?.GetValue<string>() == "GITHUB").Select(f => (Row: r, Link: f))).ToList();
        var logins = links.Select(l => Funding.Login(l.Link["url"]?.GetValue<string>())).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var listing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < logins.Count; start += 50)
        {
            var chunk = logins.Skip(start).Take(50).ToList();
            var q = new StringBuilder("query {\n");
            for (var i = 0; i < chunk.Count; i++)
            {
                q.Append($"u{i}: repositoryOwner(login: {JsonSerializer.Serialize(chunk[i])}) {{ login ... on Sponsorable {{ hasSponsorsListing }} }}\n");
            }
            var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(new JsonObject { ["query"] = q.Append('}').ToString() }.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"sponsors {chunk[0]} ...");
            for (var i = 0; i < chunk.Count; i++)
            {
                if (json?["data"] is { } data)
                {
                    listing[chunk[i]] = data[$"u{i}"]?["hasSponsorsListing"]?.GetValue<bool>() == true;
                }
            }
        }
        foreach (var (row, link) in links)
        {
            var repo = row["repo"]!.GetValue<string>();
            var owner = repo[..repo.IndexOf('/')];
            var login = Funding.Login(link["url"]?.GetValue<string>());
            var before = previous.GetValueOrDefault(repo)?["funding"]?.AsArray().OfType<JsonObject>()
                .FirstOrDefault(f => string.Equals(Funding.Login(f["url"]?.GetValue<string>()), login, StringComparison.OrdinalIgnoreCase));
            link["sponsors"] = login is not null && listing.TryGetValue(login, out var has) ? has : before?["sponsors"]?.GetValue<bool>();
            if (login is not null && login.Equals(owner, StringComparison.OrdinalIgnoreCase))
            {
                link["ownerOrMember"] = true;
            }
            else if (login is not null && row["ownerType"]?.GetValue<string>() == "Organization")
            {
                link["ownerOrMember"] = await IsPublicMemberAsync(owner, login) ?? before?["ownerOrMember"]?.GetValue<bool>();
            }
            else
            {
                link["ownerOrMember"] = false;
            }
        }
    }

    /// <summary>Whether <paramref name="login"/> is a public member of <paramref name="org"/>; null when GitHub didn't say.</summary>
    async Task<bool?> IsPublicMemberAsync(string org, string login)
    {
        try
        {
            using var response = await http.GetAsync($"orgs/{Uri.EscapeDataString(org)}/public_members/{Uri.EscapeDataString(login)}");
            return response.StatusCode switch { HttpStatusCode.NoContent => true, HttpStatusCode.NotFound => false, _ => null };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>The .toc files in each repo's root and one folder down, 10 repos a query. A repo that can't be read is left out.</summary>
    public async Task<Dictionary<string, List<string>>> ListTocsAsync(IReadOnlyList<string> repos)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < repos.Count; start += BatchSize)
        {
            var chunk = repos.Skip(start).Take(BatchSize).ToList();
            var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(new JsonObject { ["query"] = GraphQl.TreeQuery(chunk) }.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"tree {chunk[0]} ...");
            for (var i = 0; i < chunk.Count; i++)
            {
                if (json?["data"]?[$"r{i}"] is { } node && GraphQl.TocPaths(node) is { Count: > 0 } paths)
                {
                    result[chunk[i]] = paths;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// README.md's first sentence for the rows with no other summary (no .toc Notes, no description), 10 repos a query.
    /// A README that can't be read this time keeps the previous run's sentence.
    /// </summary>
    public async Task ReadReadmesAsync(List<JsonObject> rows, IReadOnlyDictionary<string, JsonObject> previous)
    {
        var wanted = rows.Where(r => r["toc"]?["notes"] is null && r["description"] is null).ToList();
        for (var start = 0; start < wanted.Count; start += BatchSize)
        {
            var chunk = wanted.Skip(start).Take(BatchSize).ToList();
            var q = new StringBuilder("query {\n");
            for (var i = 0; i < chunk.Count; i++)
            {
                var repo = chunk[i]["repo"]!.GetValue<string>();
                var slash = repo.IndexOf('/');
                q.Append($"r{i}: repository(owner: {JsonSerializer.Serialize(repo[..slash])}, name: {JsonSerializer.Serialize(repo[(slash + 1)..])}) {{ readme: object(expression: \"HEAD:README.md\") {{ ... on Blob {{ text }} }} }}\n");
            }
            var query = q.Append('}').ToString();
            var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(new JsonObject { ["query"] = query }.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"readme {chunk[0]["repo"]} ...");
            for (var i = 0; i < chunk.Count; i++)
            {
                var repo = chunk[i]["repo"]!.GetValue<string>();
                chunk[i]["readme"] = json?["data"]?[$"r{i}"] is { } node
                    ? Readme.FirstSentence(node["readme"]?["text"]?.GetValue<string>())
                    : previous.GetValueOrDefault(repo)?["readme"]?.GetValue<string>();
            }
        }
    }

    /// <summary>
    /// Every release's download counts and days, a few repos and releases a query: one query that asks for every release's
    /// files of 10 repos at once makes GitHub time out. A repo GitHub doesn't answer 3 times in a row keeps the previous run's counts
    /// and history, or has none: a count is never written short.
    /// </summary>
    /// <summary>Each repo's last push and Latest release tag, 100 repos a query; a repo missing from the result couldn't be read.</summary>
    public async Task<Dictionary<string, JsonNode?>> ReadChangesAsync(IReadOnlyList<string> repos)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < repos.Count; start += Refresh.ReposAQuery)
        {
            var chunk = repos.Skip(start).Take(Refresh.ReposAQuery).ToList();
            var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(new JsonObject { ["query"] = Refresh.Query(chunk) }.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"changes {chunk[0]} ...");
            for (var i = 0; i < chunk.Count; i++)
            {
                if (json?["data"] is JsonObject data && data.ContainsKey($"r{i}"))
                {
                    result[chunk[i]] = data[$"r{i}"]?.DeepClone();
                }
            }
        }
        return result;
    }

    public async Task ReadDownloadsAsync(List<JsonObject> rows, IReadOnlyDictionary<string, JsonObject> previous)
    {
        var tallies = rows.Select(r => new Downloads.Tally(r["repo"]!.GetValue<string>())).ToList();
        var open = tallies.ToList();
        Console.Error.WriteLine($"Counting the downloads of {open.Count} repos ...");
        for (var queries = 1; open.Count > 0; queries++)
        {
            var batch = open.Take(Downloads.ReposAQuery).ToList();
            var query = Downloads.Query(batch);
            var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(new JsonObject { ["query"] = query }.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"downloads {batch[0].Repo} ...");
            var errors = json?["errors"]?.AsArray() ?? [];
            for (var i = 0; i < batch.Count; i++)
            {
                if (json?["data"]?[$"r{i}"]?["releases"] is { } releases && !errors.Any(e => e?["path"]?[0]?.GetValue<string>() == $"r{i}"))
                {
                    batch[i].Add(releases);
                    batch[i].Misses = 0; // misses in a row: a repo with hundreds of releases needs many pages
                }
                else
                {
                    batch[i].Misses++;
                }
            }
            open = open.Where(t => !t.Done && t.Misses < 3).ToList();
            if (queries % 100 == 0)
            {
                Console.Error.WriteLine($"  {queries} queries, {open.Count} repos left");
            }
        }
        var now = DateTime.UtcNow;
        for (var i = 0; i < rows.Count; i++)
        {
            var (row, tally) = (rows[i], tallies[i]);
            var before = tally.Done ? null : previous.GetValueOrDefault(tally.Repo);
            row["releases"] = tally.Done ? tally.Releases() : before?["releases"]?.DeepClone();
            row["downloads"] = tally.Done ? tally.Counts(row, now) : before?["downloads"]?.DeepClone();
            var history = previous.GetValueOrDefault(tally.Repo)?["downloadHistory"] as JsonArray;
            row["downloadHistory"] = tally.Done && row["downloads"]?["total"] is { } total
                ? Stats.History(history, now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), total.GetValue<long>())
                : history?.DeepClone();
        }
    }

    public void Dispose() => http.Dispose();
}

/// <summary>A repo's release history and where its latest release says its files came from, for trust signals and mirrors.</summary>
static partial class History
{
    /// <summary>
    /// The CurseForge, Wago and CurseForge file-host links in release notes (at most 10, each once): a mirror's record of
    /// where its file came from.
    /// </summary>
    public static JsonArray? SourceLinks(string? notes)
    {
        var links = SourceLink().Matches(notes ?? "").Select(m => m.Value.TrimEnd('.', ',', ';', ':')).Distinct(StringComparer.Ordinal).Take(10).ToList();
        return links.Count > 0 ? new JsonArray(links.Select(l => (JsonNode)l).ToArray()) : null;
    }

    [GeneratedRegex(@"https?://(?:www\.|legacy\.)?(?:curseforge\.com|(?:media|edge|mediafilez)\.forgecdn\.net|addons\.wago\.io)/[^\s)\]""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex SourceLink();
}

/// <summary>
/// How often an add-on is downloaded: GitHub's own public counts for its release files (downloadCount), summed over every
/// release (total) and over GitHub's Latest release (latest), with the day they were read, and the release history for
/// trust signals: how many releases and the days of the newest 100 that aren't pre-releases. Nothing is tracked by the app:
/// its installs download from GitHub, so they're already in these counts. GitHub doesn't count its source-code archives,
/// and each release's first 30 files are counted.
/// </summary>
/// <summary>Which known repos the search part can keep as they were.</summary>
static class Refresh
{
    public const int ReposAQuery = 100;

    public static string Query(IReadOnlyList<string> repos)
    {
        var q = new StringBuilder("query {\n");
        for (var i = 0; i < repos.Count; i++)
        {
            var slash = repos[i].IndexOf('/');
            q.Append($"r{i}: repository(owner: {JsonSerializer.Serialize(repos[i][..slash])}, name: {JsonSerializer.Serialize(repos[i][(slash + 1)..])}) {{ nameWithOwner pushedAt latestRelease {{ tagName }} }}\n");
        }
        return q.Append('}').ToString();
    }

    /// <summary>
    /// Whether a repo is as it was when its row was written: the same name, last push and Latest release. One that couldn't
    /// be asked, or is gone, counts as changed, so it's read in full.
    /// </summary>
    public static bool Unchanged(JsonObject before, JsonNode? now) =>
        now is JsonObject n
        && string.Equals(n["nameWithOwner"]?.GetValue<string>(), before["repo"]?.GetValue<string>(), StringComparison.Ordinal)
        && n["pushedAt"]?.GetValue<string>() is { } pushed && pushed == before["pushedAt"]?.GetValue<string>()
        && n["latestRelease"]?["tagName"]?.GetValue<string>() == before["latestRelease"]?["tag"]?.GetValue<string>();

    /// <summary>Whether the previous run's editions had the same Interfaces: a row's editions depend on them.</summary>
    public static bool SameEditions(JsonObject? before, IReadOnlyDictionary<string, string> editions) =>
        before is not null && before.Count == editions.Count
        && editions.All(e => before[e.Key]?.GetValue<string>() == e.Value);
}

/// <summary>What only the stats part writes, and the history of daily download totals.</summary>
static class Stats
{
    public const int HistoryDays = 30;

    /// <summary>A freshly read row keeps the previous row's download counts, history and funding links' answers.</summary>
    public static void Carry(JsonObject row, JsonObject? before)
    {
        if (before is null)
        {
            return;
        }
        foreach (var key in new[] { "releases", "downloads", "downloadHistory" })
        {
            row[key] = before[key]?.DeepClone();
        }
        var old = (before["funding"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        foreach (var link in (row["funding"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var url = link["url"]?.GetValue<string>();
            if (old.FirstOrDefault(o => o["url"]?.GetValue<string>() == url) is not { } match)
            {
                continue;
            }
            foreach (var key in new[] { "sponsors", "ownerOrMember", "resolved" })
            {
                if (match.ContainsKey(key))
                {
                    link[key] = match[key]?.DeepClone();
                }
            }
        }
    }

    /// <summary>The daily totals with today's: one per day, the newest <see cref="HistoryDays"/>, oldest first.</summary>
    public static JsonArray History(JsonArray? before, string day, long total)
    {
        var days = (before ?? []).OfType<JsonObject>()
            .Where(d => d["day"]?.GetValue<string>() is { } at && at != day)
            .Select(d => (Day: d["day"]!.GetValue<string>(), Total: d["total"]!.GetValue<long>()))
            .Append((Day: day, Total: total))
            .OrderBy(d => d.Day, StringComparer.Ordinal).TakeLast(HistoryDays);
        return new JsonArray(days.Select(d => (JsonNode)new JsonObject { ["day"] = d.Day, ["total"] = d.Total }).ToArray());
    }
}

static class Downloads
{
    public const int ReposAQuery = 5, ReleasesAQuery = 50, FilesARelease = 30, Days = 100;

    static long Files(JsonNode? assets) => (assets?["nodes"]?.AsArray() ?? []).Sum(a => a?["downloadCount"]?.GetValue<long>() ?? 0);

    /// <summary>The downloads of one page of releases.</summary>
    public static long Sum(JsonNode releases) => (releases["nodes"]?.AsArray() ?? []).Sum(r => Files(r?["releaseAssets"]));

    /// <summary>The cursor of the next page of releases, or null on the last one.</summary>
    public static string? Next(JsonNode releases) =>
        releases["pageInfo"]?["hasNextPage"]?.GetValue<bool>() == true ? releases["pageInfo"]?["endCursor"]?.GetValue<string>() : null;

    /// <summary>One repo's count, a page of releases at a time.</summary>
    public sealed class Tally(string repo)
    {
        long total;
        int count;
        readonly List<string> days = [];

        public string Repo { get; } = repo;
        public string? Cursor { get; private set; }
        public bool Done { get; private set; }
        public int Misses { get; set; }

        public void Add(JsonNode releases)
        {
            total += Sum(releases);
            count = releases["totalCount"]?.GetValue<int>() ?? count;
            foreach (var r in (releases["nodes"]?.AsArray() ?? []).OfType<JsonObject>())
            {
                if (days.Count < Days && r["isPrerelease"]?.GetValue<bool>() != true && r["publishedAt"]?.GetValue<string>() is { } published)
                {
                    days.Add(DateTimeOffset.Parse(published, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime
                        .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            Cursor = Next(releases);
            Done = Cursor is null;
        }

        public JsonObject Releases() => new() { ["count"] = count, ["days"] = new JsonArray(days.Select(d => (JsonNode)d).ToArray()) };

        /// <summary>The counts, or null for a repo with no releases. <c>latest</c> is the row's latest release's files.</summary>
        public JsonObject? Counts(JsonObject row, DateTime now)
        {
            if (count == 0)
            {
                return null;
            }
            var d = new JsonObject { ["total"] = total };
            if (row["latestRelease"]?["assets"] is JsonArray assets)
            {
                d["latest"] = assets.Sum(a => a?["downloads"]?.GetValue<long>() ?? 0);
            }
            d["read"] = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return d;
        }
    }

    /// <summary>The next page of releases for each repo, from where its tally stopped.</summary>
    public static string Query(IReadOnlyList<Tally> tallies)
    {
        var q = new StringBuilder("query {\n");
        for (var i = 0; i < tallies.Count; i++)
        {
            var repo = tallies[i].Repo;
            var slash = repo.IndexOf('/');
            var after = tallies[i].Cursor is { } c ? $", after: {JsonSerializer.Serialize(c)}" : "";
            q.Append($"r{i}: repository(owner: {JsonSerializer.Serialize(repo[..slash])}, name: {JsonSerializer.Serialize(repo[(slash + 1)..])}) {{ releases(first: {ReleasesAQuery}{after}, orderBy: {{field: CREATED_AT, direction: DESC}}) {{ totalCount pageInfo {{ hasNextPage endCursor }} nodes {{ publishedAt isPrerelease releaseAssets(first: {FilesARelease}) {{ nodes {{ downloadCount }} }} }} }} }}\n");
        }
        return q.Append('}').ToString();
    }
}

/// <summary>A repo's FUNDING.yml as GitHub reads it (fundingLinks): platform and URL, in the file's order.</summary>
static class Funding
{
    public static JsonArray? FromNode(JsonNode node)
    {
        var links = node["fundingLinks"]?.AsArray().OfType<JsonObject>()
            .Select(f => new JsonObject { ["platform"] = f["platform"]?.GetValue<string>(), ["url"] = f["url"]?.GetValue<string>() })
            .Where(f => f["platform"] is not null && f["url"] is not null).ToArray() ?? [];
        return links.Length > 0 ? new JsonArray(links) : null;
    }

    /// <summary>Link shorteners: where they lead can't be seen from the link, so discovery asks them (scripts/generate.cs has the same list).</summary>
    public static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "is.gd", "ow.ly", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc",
    };

    public static bool IsShortLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && Shorteners.Contains(uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host);

    /// <summary>
    /// Where a shortened link leads, from the shortener's redirect alone (a HEAD request, at most 5 hops, never reading a
    /// page). Null when it doesn't redirect to an https page outside the shorteners.
    /// </summary>
    public static async Task<string?> ResolveAsync(string url, Func<string, Task<string?>> location)
    {
        var current = url;
        for (var hop = 0; hop < 5 && IsShortLink(current); hop++)
        {
            if (await location(current) is not { } next || !Uri.TryCreate(new Uri(current), next, out var target))
            {
                return null;
            }
            current = target.AbsoluteUri;
        }
        return !IsShortLink(current) && current.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? current : null;
    }

    /// <summary>Resolves each shortened CUSTOM funding link into "resolved"; one that can't be resolved this run keeps the previous answer.</summary>
    public static async Task ResolveShortLinksAsync(List<JsonObject> rows, IReadOnlyDictionary<string, JsonObject> previous)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-discovery");
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var f in (row["funding"]?.AsArray() ?? []).OfType<JsonObject>().Where(f => f["platform"]?.GetValue<string>() == "CUSTOM"))
            {
                var url = f["url"]?.GetValue<string>() ?? "";
                if (!IsShortLink(url))
                {
                    continue;
                }
                if (!cache.TryGetValue(url, out var resolved))
                {
                    cache[url] = resolved = await ResolveAsync(url, async u =>
                    {
                        try
                        {
                            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, u));
                            return (int)response.StatusCode is >= 300 and < 400 ? response.Headers.Location?.OriginalString : null;
                        }
                        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                        {
                            return null;
                        }
                    });
                }
                var before = previous.GetValueOrDefault(row["repo"]!.GetValue<string>())?["funding"]?.AsArray().OfType<JsonObject>()
                    .FirstOrDefault(p => p["url"]?.GetValue<string>() == url)?["resolved"]?.GetValue<string>();
                f["resolved"] = resolved ?? before;
            }
        }
    }

    /// <summary>The account a GITHUB funding link names: https://github.com/&lt;login&gt; or .../sponsors/&lt;login&gt;.</summary>
    public static string? Login(string? url)
    {
        var m = Regex.Match(url ?? "", @"^https://github\.com/(?:sponsors/)?([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/?$");
        return m.Success ? m.Groups[1].Value : null;
    }
}

static class GraphQl
{
    const string Fields = """
        nameWithOwner description isArchived isFork isPrivate pushedAt hasIssuesEnabled createdAt stargazerCount
        owner { __typename login ... on User { createdAt } ... on Organization { createdAt } }
        fundingLinks { platform url }
        defaultBranchRef { name }
        repositoryTopics(first: 20) { nodes { topic { name } } }
        latestRelease { tagName name publishedAt isPrerelease description releaseAssets(first: 50) { nodes { name size downloadUrl digest downloadCount } } }
        refs(refPrefix: "refs/tags/", first: 5, orderBy: {field: TAG_COMMIT_DATE, direction: DESC}) {
          nodes { name target { ... on Commit { committedDate } ... on Tag { target { ... on Commit { committedDate } } } } }
        }
        pkgmeta: object(expression: "HEAD:.pkgmeta") { id }
        """;

    static string Literal(string s) => JsonSerializer.Serialize(s);

    /// <summary>Each repo's root tree and the trees of its folders, for finding .toc files.</summary>
    public static string TreeQuery(IReadOnlyList<string> repos)
    {
        var q = new StringBuilder("query {\n");
        for (var i = 0; i < repos.Count; i++)
        {
            var slash = repos[i].IndexOf('/');
            q.Append($"r{i}: repository(owner: {Literal(repos[i][..slash])}, name: {Literal(repos[i][(slash + 1)..])}) {{ root: object(expression: \"HEAD:\") {{ ... on Tree {{ entries {{ path type object {{ ... on Tree {{ entries {{ path type }} }} }} }} }} }} }}\n");
        }
        return q.Append('}').ToString();
    }

    /// <summary>The .toc paths in a <see cref="TreeQuery"/> result for one repo.</summary>
    public static List<string> TocPaths(JsonNode repo) =>
        (repo["root"]?["entries"]?.AsArray() ?? [])
            .SelectMany(e => e?["type"]?.GetValue<string>() == "tree"
                ? (e["object"]?["entries"]?.AsArray() ?? []).Select(f => f?["type"]?.GetValue<string>() == "blob" ? f["path"]?.GetValue<string>() : null)
                : [e?["type"]?.GetValue<string>() == "blob" ? e["path"]?.GetValue<string>() : null])
            .OfType<string>().Where(path => path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();

    public static string Query(IReadOnlyList<string> repos, IReadOnlyList<List<string>> paths)
    {
        var q = new StringBuilder("query {\n");
        for (var i = 0; i < repos.Count; i++)
        {
            var slash = repos[i].IndexOf('/');
            q.Append($"r{i}: repository(owner: {Literal(repos[i][..slash])}, name: {Literal(repos[i][(slash + 1)..])}) {{\n{Fields}\n");
            for (var t = 0; t < paths[i].Count; t++)
            {
                q.Append($"t{t}: object(expression: {Literal("HEAD:" + paths[i][t])}) {{ ... on Blob {{ text }} }}\n");
            }
            q.Append("}\n");
        }
        return q.Append('}').ToString();
    }

    /// <summary>
    /// Each repo's node, with its .toc files gathered into <c>tocFiles</c>; null for a repo GitHub says isn't there. A repo
    /// is left out when the query failed for it (no data, or an error other than NOT_FOUND).
    /// </summary>
    public static IEnumerable<(string Repo, JsonNode? Node)> Results(IReadOnlyList<string> repos, IReadOnlyList<List<string>> paths, JsonNode? json)
    {
        var data = json?["data"];
        if (data is null)
        {
            yield break;
        }
        var errors = json!["errors"]?.AsArray() ?? [];
        for (var i = 0; i < repos.Count; i++)
        {
            var node = data[$"r{i}"];
            if (node is null)
            {
                var notFound = errors.Any(e => e!["path"]?[0]?.GetValue<string>() == $"r{i}" && e["type"]?.GetValue<string>() == "NOT_FOUND");
                if (notFound)
                {
                    yield return (repos[i], null);
                }
                continue;
            }
            var tocs = new JsonArray();
            for (var t = 0; t < paths[i].Count; t++)
            {
                tocs.Add(new JsonObject { ["path"] = paths[i][t], ["text"] = node[$"t{t}"]?["text"]?.GetValue<string>() });
            }
            var copy = node.DeepClone().AsObject();
            copy["tocFiles"] = tocs;
            yield return (repos[i], copy);
        }
    }
}

/// <summary>A README's first sentence of plain text: the generator's last fallback for an add-on's summary.</summary>
static partial class Readme
{
    public static string? FirstSentence(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }
        var inCode = false;
        var paragraph = new List<string>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            // Headings, quotes, tables, lists, rules, images and badges, and HTML end a paragraph without being one.
            var skip = inCode || line.Length == 0 || line[0] is '#' or '>' or '|' or '-' or '*' or '<' or '!' or '=' || Image().IsMatch(line);
            if (skip)
            {
                if (paragraph.Count > 0)
                {
                    break;
                }
                continue;
            }
            paragraph.Add(line);
        }
        var text = Link().Replace(string.Join(" ", paragraph), "$1");
        text = Emphasis().Replace(text, "").Trim();
        if (!text.Any(char.IsLetter))
        {
            return null;
        }
        var end = SentenceEnd().Match(text);
        var sentence = end.Success ? text[..(end.Index + 1)] : text;
        return sentence.Length > 300 ? sentence[..300].TrimEnd() : sentence;
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"[*_`]+")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"[.!?](\s|$)")]
    private static partial Regex SentenceEnd();
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

    static readonly SortedDictionary<string, string> Editions = new(StringComparer.Ordinal)
    {
        ["classic_era"] = "11509", ["forever"] = "16001", ["retail"] = "120105",
    };

    static JsonNode Repo(string name, params (string Path, string? Text)[] tocs)
    {
        var node = JsonNode.Parse($$"""
            {
              "nameWithOwner": "{{name}}", "description": "A thing", "isArchived": false, "isFork": false, "pushedAt": "2026-11-20T10:00:00Z", "createdAt": "2024-01-02T03:00:00Z", "stargazerCount": 42,
              "owner": { "__typename": "User", "login": "a", "createdAt": "2020-05-06T00:00:00Z" },
              "defaultBranchRef": { "name": "main" },
              "repositoryTopics": { "nodes": [ { "topic": { "name": "wow" } }, { "topic": { "name": "addon" } } ] },
              "latestRelease": { "tagName": "v2", "name": "Two", "publishedAt": "2026-11-19T10:00:00Z", "isPrerelease": false,
                "description": "Mirrored from https://www.curseforge.com/wow/addons/thing/files/123. Also https://www.curseforge.com/wow/addons/thing/files/123 and https://example.com/x",
                "releaseAssets": { "nodes": [ { "name": "Thing-v2.zip", "size": 1234, "downloadUrl": "https://github.com/a/b/releases/download/v2/Thing-v2.zip", "digest": "sha256:00ff", "downloadCount": 7 } ] } },
              "refs": { "nodes": [ { "name": "v2", "target": { "committedDate": "2026-11-19T09:00:00Z" } }, { "name": "v1", "target": { "target": { "committedDate": "2026-10-01T09:00:00Z" } } } ] },
              "pkgmeta": { "id": "x" }
            }
            """)!;
        node["tocFiles"] = new JsonArray(tocs.Select(t => (JsonNode)new JsonObject { ["path"] = t.Path, ["text"] = t.Text }).ToArray());
        return node;
    }

    public static async Task<int> Run()
    {
        // The .toc.
        var toc = Toc.Parse("﻿## Interface: 11509, 16001 ,120105\r\n## Title: Thing\n## Dependencies: Ace3, LibStub\n## RequiredDeps: LibStub, Other\n## OptionalDeps: A, B\n## Notes-deDE: Ding\n## title: Second\n##NoColon\n");
        Check("toc: interfaces", Toc.Interfaces(toc).SequenceEqual(["11509", "16001", "120105"]));
        Check("toc: first key wins, ignoring case", toc["Title"] == "Thing");
        Check("toc: dependencies from every key, once", Toc.Dependencies(toc).SequenceEqual(["Ace3", "LibStub", "Other"]));
        Check("toc: no interface", Toc.Interfaces(Toc.Parse("## Title: X")).Count == 0);
        Check("toc: every Interface line, placeholders skipped", Toc.Interfaces(Toc.Parse("## Interface: @toc-version-retail@, @toc-version-classic@\n## Interface: 120000, 16001\n## Interface: 16001 11509"))
            .SequenceEqual(["120000", "16001", "11509"]));
        var order = new[] { "a/b/Thing.toc", "Thing_Mainline.toc", "Thing.toc", "Libs/Lib.toc" }.Order(Comparer<string>.Create(Toc.Compare)).ToList();
        Check("toc: shallowest, then shortest", order.SequenceEqual(["Thing.toc", "Thing_Mainline.toc", "Libs/Lib.toc", "a/b/Thing.toc"]));

        // Download counts.
        var tally = new Downloads.Tally("a/b");
        Check("downloads: the first page's query", Downloads.Query([tally]).Contains("""r0: repository(owner: "a", name: "b") { releases(first: 50, orderBy""") && !Downloads.Query([tally]).Contains("after:"));
        tally.Add(JsonNode.Parse("""{ "totalCount": 3, "pageInfo": { "hasNextPage": true, "endCursor": "c1" }, "nodes": [ { "publishedAt": "2026-11-19T23:30:00-02:00", "releaseAssets": { "nodes": [ { "downloadCount": 7 }, { "downloadCount": 3 } ] } }, null ] }""")!);
        Check("downloads: more releases, the next page's query", !tally.Done && tally.Cursor == "c1" && Downloads.Query([tally]).Contains("after: \"c1\""));
        tally.Add(JsonNode.Parse("""{ "totalCount": 3, "pageInfo": { "hasNextPage": false }, "nodes": [ { "publishedAt": "2026-10-01T10:00:00Z", "isPrerelease": true, "releaseAssets": { "nodes": [ { "downloadCount": 10 } ] } } ] }""")!);
        var counted = tally.Counts(JsonNode.Parse("""{ "latestRelease": { "assets": [ { "downloads": 7 }, { "downloads": null } ] } }""")!.AsObject(), new DateTime(2026, 11, 26, 23, 0, 0, DateTimeKind.Utc))!;
        Check("downloads: every page's files, the latest release's, and the day", tally.Done && (long)counted["total"]! == 20 && (long)counted["latest"]! == 7
            && counted["read"]!.GetValue<string>() == "2026-11-26");
        Check("downloads: the release history, pre-releases left out of the days", tally.Releases()["count"]!.GetValue<int>() == 3
            && tally.Releases()["days"]!.AsArray().Select(d => d!.GetValue<string>()).SequenceEqual(["2026-11-20"]));
        var empty = new Downloads.Tally("a/b");
        empty.Add(JsonNode.Parse("""{ "totalCount": 0, "pageInfo": { "hasNextPage": false }, "nodes": [] }""")!);
        Check("downloads: none without releases", empty.Done && empty.Counts(new JsonObject(), DateTime.UtcNow) is null && empty.Releases()["count"]!.GetValue<int>() == 0);

        // The never-add list.
        var optout = OptOut.Parse("""{ "repos": [ { "repo": "Someone/Thing" }, { "repo": "whole-account" } ] }""");
        Check("optout: one repo, ignoring case", optout.Covers("someone/thing") && !optout.Covers("someone/other"));
        Check("optout: a whole account", optout.Covers("Whole-Account/anything") && !optout.Covers("whole-accountx/thing"));
        foreach (var bad in new[] { "a/b/c", "a//b", "", "a b/c" })
        {
            Check($"optout: refuses '{bad}'", Throws(() => OptOut.Parse($$"""{ "repos": [ { "repo": "{{bad}}" } ] }""")));
        }

        // Rows.
        var (row, reason) = Candidates.Row("a/b", Repo("A/B",
            ("Libs/LibStub/LibStub.toc", "## Interface: 120105\n## Title: LibStub"),
            ("Thing_Vanilla.toc", "## Interface: 11509\n## Title: Thing Classic"),
            ("Thing.toc", "## Interface: 120105, 16001\n## Title: |cff00ff00Thing|r\n## Notes: Does things\n## Notes-frFR: Fait\n## Author: Someone\n## Version: 2.0\n## X-Curse-Project-ID: 123\n## X-Wago-ID: abc\n## X-WoWI-ID: 9\n## Category: Bags\n## IconTexture: Interface\\Icons\\X\n## Dependencies: Ace3\n## OptionalDeps: Masque\n## X-Forever-Catalog: yes"),
            ("Old.toc", "## Interface: 11508"),
            ("Deleted.toc", null)), Editions);
        Check("row: kept", row is not null && reason is null);
        Check("row: history", row!["created"]!.GetValue<string>() == "2024-01-02T03:00:00Z" && row["ownerCreated"]!.GetValue<string>() == "2020-05-06T00:00:00Z"
            && row["stars"]!.GetValue<int>() == 42 && row["releases"] is null && row["downloads"] is null
            && row["latestRelease"]!["assets"]![0]!["downloads"]!.GetValue<long>() == 7);
        Check("row: the latest release's source links, each once", row["latestRelease"]!["sourceLinks"]!.AsArray().Select(l => l!.GetValue<string>())
            .SequenceEqual(["https://www.curseforge.com/wow/addons/thing/files/123"]) && History.SourceLinks("none here") is null);
        Check("row: the author's catalog marker", row["toc"]!["catalog"]!.GetValue<string>() == "yes");
        var broken = Repo("A/B", ("Thing.toc", "## Interface: 16001\n## Title: T"));
        broken["latestRelease"]!["releaseAssets"]!["nodes"]!.AsArray().Add(null);
        broken["refs"]!["nodes"]!.AsArray().Add(null);
        var (partial, _) = Candidates.Row("a/b", broken, Editions);
        Check("row: a partial answer skips what's missing", partial is not null
            && partial["latestRelease"]!["assets"]!.AsArray().Count == 1 && partial["tags"]!.AsArray().Count == 2);
        Check("row: editions from every .toc", row!["editions"]!.AsArray().Select(e => e!.GetValue<string>()).SequenceEqual(["classic_era", "forever", "retail"]));
        Check("row: main .toc is the shallowest listing a current Interface", row["toc"]!["path"]!.GetValue<string>() == "Thing.toc");
        Check("row: .toc fields", row["toc"]!["title"]!.GetValue<string>() == "|cff00ff00Thing|r" && row["toc"]!["curseforge"]!.GetValue<string>() == "123"
            && row["toc"]!["wago"]!.GetValue<string>() == "abc" && row["toc"]!["wowi"]!.GetValue<string>() == "9" && row["toc"]!["author"]!.GetValue<string>() == "Someone"
            && row["toc"]!["version"]!.GetValue<string>() == "2.0" && row["toc"]!["notes"]!.GetValue<string>() == "Does things"
            && row["toc"]!["localizedNotes"]!["frFR"]!.GetValue<string>() == "Fait" && row["toc"]!["category"]!.GetValue<string>() == "Bags");
        Check("row: dependencies", row["toc"]!["dependencies"]!.AsArray().Single()!.GetValue<string>() == "Ace3"
            && row["toc"]!["optionalDependencies"]!.AsArray().Single()!.GetValue<string>() == "Masque");
        Check("row: every .toc read, deleted ones left out", row["tocs"]!.AsArray().Select(t => t!["path"]!.GetValue<string>())
            .SequenceEqual(["Old.toc", "Thing.toc", "Thing_Vanilla.toc", "Libs/LibStub/LibStub.toc"]));
        var vanilla = row["tocs"]!.AsArray().Single(t => t!["path"]!.GetValue<string>() == "Thing_Vanilla.toc")!;
        var thing = row["tocs"]!.AsArray().Single(t => t!["path"]!.GetValue<string>() == "Thing.toc")!;
        Check("row: each .toc's fields", vanilla["title"]!.GetValue<string>() == "Thing Classic" && vanilla["interfaces"]![0]!.GetValue<string>() == "11509"
            && thing["notes"]!.GetValue<string>() == "Does things" && thing["dependencies"]![0]!.GetValue<string>() == "Ace3" && thing["iconTexture"] is not null);
        var (longRow, _) = Candidates.Row("a/b", Repo("A/B", ("Long.toc", "## Interface: 16001\n## Notes: " + new string('x', 500) + "\n## Notes-deDE: " + new string('y', 500))), Editions);
        Check("row: Notes in tocs are cut, the main .toc's aren't", longRow!["toc"]!["notes"]!.GetValue<string>().Length == 500
            && longRow["tocs"]![0]!["notes"]!.GetValue<string>().Length == 301 && longRow["tocs"]![0]!["localizedNotes"]!["deDE"]!.GetValue<string>().Length == 301);
        Check("row: release and digest", row["latestRelease"]!["tag"]!.GetValue<string>() == "v2"
            && row["latestRelease"]!["assets"]![0]!["digest"]!.GetValue<string>() == "sha256:00ff" && row["latestRelease"]!["assets"]![0]!["size"]!.GetValue<long>() == 1234);
        Check("row: tags, lightweight and annotated", row["tags"]![0]!["date"]!.GetValue<string>() == "2026-11-19T09:00:00Z" && row["tags"]![1]!["date"]!.GetValue<string>() == "2026-10-01T09:00:00Z");
        Check("readme: first sentence", Readme.FirstSentence("# Thing\n[![badge](x)](y)\n\n<img src=x>\nThing **tracks** your [quests](https://x). It also maps.\nMore.\n") == "Thing tracks your quests.");
        Check("readme: none", Readme.FirstSentence("# Title\n\n![shot](a.png)\n```\ncode.\n```\n") is null && Readme.FirstSentence(null) is null);
        Check("row: repo, topics, .pkgmeta", row["repo"]!.GetValue<string>() == "A/B" && row["topics"]!.AsArray().Select(t => t!.GetValue<string>()).SequenceEqual(["addon", "wow"]) && row["pkgmeta"]!.GetValue<bool>());

        Check("drop: gone", Candidates.Row("a/b", null, Editions).Reason == "gone or private");
        var fork = Repo("a/b", ("X.toc", "## Interface: 16001"));
        fork["isFork"] = true;
        Check("drop: fork", Candidates.Row("a/b", fork, Editions).Reason == "a fork");
        Check("keep: a fork an override names, marked as a fork", Candidates.Row("a/b", fork, Editions, known: true).Row?["fork"]?.GetValue<bool>() == true
            && row["fork"]!.GetValue<bool>() == false);
        var archived = Repo("a/b", ("X.toc", "## Interface: 16001"));
        archived["isArchived"] = true;
        Check("drop: archived", Candidates.Row("a/b", archived, Editions).Reason == "archived");
        var hidden = Repo("a/b", ("X.toc", "## Interface: 16001"));
        hidden["isPrivate"] = true;
        Check("drop: private, even an override's", Candidates.Row("a/b", hidden, Editions).Reason == "private" && Candidates.Row("a/b", hidden, Editions, known: true).Reason == "private");
        Check("drop: no current Interface", Candidates.Row("a/b", Repo("a/b", ("X.toc", "## Interface: 11508, 16000")), Editions).Reason == "no .toc lists a current Interface");
        Check("drop: interface only in another field", Candidates.Row("a/b", Repo("a/b", ("X.toc", "## Version: 16001")), Editions).Row is null);

        // Building a run: unread repos keep their previous row, renames merge, the never-add list wins.
        var log = new RunLog();
        var previous = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
        {
            ["keep/me"] = new JsonObject { ["repo"] = "keep/me", ["editions"] = new JsonArray("forever"), ["tocs"] = new JsonArray() },
            ["gone/now"] = new JsonObject { ["repo"] = "gone/now", ["editions"] = new JsonArray("forever"), ["tocs"] = new JsonArray() },
        };
        var reads = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["gone/now"] = null,
            ["old/name"] = Repo("new/name", ("Old.toc", "## Interface: 16001")),
            ["new/name"] = Repo("new/name", ("New.toc", "## Interface: 120105")),
            ["Someone/Thing"] = Repo("Someone/Thing", ("X.toc", "## Interface: 16001")),
        };
        var rows = Candidates.Build(["keep/me", "gone/now", "old/name", "new/name", "Someone/Thing"], reads, previous, Editions, optout, log);
        Check("build: an unread repo keeps its row", rows.Any(r => r["repo"]!.GetValue<string>() == "keep/me") && log.Kept.SequenceEqual(["keep/me"]));
        Check("build: a gone repo is dropped", rows.All(r => r["repo"]!.GetValue<string>() != "gone/now") && log.Dropped["gone or private"].SequenceEqual(["gone/now"]));
        var renamed = rows.Single(r => r["repo"]!.GetValue<string>() == "new/name");
        Check("build: a renamed repo is one row with both .toc files", rows.Count(r => r["repo"]!.GetValue<string>() == "new/name") == 1
            && renamed["tocs"]!.AsArray().Count == 2 && renamed["editions"]!.AsArray().Select(e => e!.GetValue<string>()).SequenceEqual(["forever", "retail"]));
        Check("build: never-add list", rows.All(r => r["repo"]!.GetValue<string>() != "Someone/Thing") && log.Dropped["on the never-add list"].Contains("Someone/Thing"));

        // The file: stable, sorted, independent of input order.
        var one = Candidates.Write(Editions, rows);
        var two = Candidates.Write(Editions, Enumerable.Reverse(rows));
        Check("write: same rows, same text", one == two && one.EndsWith("}\n") && !one.Contains('\r'));
        var back = Candidates.Read(one);
        Check("write: reads back", back.Count == rows.Count && Candidates.Write(Editions, back.Values) == one);
        var dated = Candidates.Write(Editions, rows, new Dictionary<string, string> { [Editions.Values.First()] = "2026-11-28T02:00:00Z", ["99999"] = "2026-01-01T00:00:00Z" });
        Check("write: search dates for the current Interfaces only, read back", Candidates.Searched(dated).Count == 1
            && Candidates.Searched(dated)[Editions.Values.First()] == "2026-11-28T02:00:00Z" && Candidates.Searched(one).Count == 0);

        // GraphQL: the query and its results.
        var query = GraphQl.Query(["a/b", "c/d\"x"], [["Thing.toc", "Sub/A \"B\".toc"], []]);
        Check("graphql: aliases and escaped paths", query.Contains("r0: repository(owner: \"a\", name: \"b\")") && query.Contains("t1: object(expression: \"HEAD:Sub/A \\u0022B\\u0022.toc\")")
            && query.Contains("name: \"d\\u0022x\""));
        var response = JsonNode.Parse("""
            { "data": { "r0": { "nameWithOwner": "a/b", "t0": { "text": "## Interface: 16001" }, "t1": null }, "r1": null, "r2": null },
              "errors": [ { "type": "NOT_FOUND", "path": ["r1"] }, { "type": "SOMETHING_ELSE", "path": ["r2"] } ] }
            """);
        var results = GraphQl.Results(["a/b", "gone/x", "err/y"], [["Thing.toc", "B.toc"], [], []], response).ToList();
        Check("graphql: .toc texts gathered", results[0].Node!["tocFiles"]!.AsArray().Count == 2 && results[0].Node!["tocFiles"]![1]!["text"] is null);
        Check("graphql: NOT_FOUND is gone, other errors unread", results.Count == 2 && results[1] is ("gone/x", null));
        Check("graphql: no data, nothing read", !GraphQl.Results(["a/b"], [[]], null).Any());
        Check("funding: GitHub's reading of FUNDING.yml", Funding.FromNode(JsonNode.Parse("""{ "fundingLinks": [ { "platform": "GITHUB", "url": "https://github.com/a" }, { "platform": "KO_FI", "url": "https://ko-fi.com/b" } ] }""")!)!.Count == 2
            && Funding.FromNode(JsonNode.Parse("""{ "fundingLinks": [] }""")!) is null);
        Check("funding: login", Funding.Login("https://github.com/a-b") == "a-b" && Funding.Login("https://github.com/sponsors/x") == "x"
            && Funding.Login("https://github.com/a/b") is null && Funding.Login("https://example.com/a") is null);
        var hops = new Dictionary<string, string?> { ["https://bit.ly/a"] = "https://tinyurl.com/b", ["https://tinyurl.com/b"] = "https://ko-fi.com/me", ["https://bit.ly/loop"] = "https://bit.ly/loop", ["https://bit.ly/http"] = "http://example.com/" };
        Task<string?> Hop(string u) => Task.FromResult(hops.GetValueOrDefault(u));
        Check("funding: a short link resolves through its redirects", Funding.ResolveAsync("https://bit.ly/a", Hop).Result == "https://ko-fi.com/me");
        Check("funding: a short link that loops, goes nowhere or leaves https doesn't resolve", Funding.ResolveAsync("https://bit.ly/loop", Hop).Result is null
            && Funding.ResolveAsync("https://bit.ly/none", Hop).Result is null && Funding.ResolveAsync("https://bit.ly/http", Hop).Result is null);
        Check("graphql: tree query", GraphQl.TreeQuery(["a/b"]).Contains("r0: repository(owner: \"a\", name: \"b\") { root: object(expression: \"HEAD:\")"));
        var tree = JsonNode.Parse("""
            { "root": { "entries": [
              { "path": "Thing.toc", "type": "blob" }, { "path": "README.md", "type": "blob" }, { "path": "Odd.toc", "type": "tree", "object": { "entries": [] } },
              { "path": "Sub", "type": "tree", "object": { "entries": [ { "path": "Sub/Sub_Forever.toc", "type": "blob" }, { "path": "Sub/x.lua", "type": "blob" } ] } } ] } }
            """)!;
        Check("graphql: .toc paths from the root and one folder down", GraphQl.TocPaths(tree).SequenceEqual(["Sub/Sub_Forever.toc", "Thing.toc"]));
        var seeds = Inputs.Seeds(JsonNode.Parse("""
            { "addons": [ { "sources": [ { "type": "github", "repo": "A/b" }, { "type": "curseforge", "projectId": 1 } ] }, { "sources": [ { "type": "github", "repo": "c/d" } ] } ] }
            """)!, [JsonNode.Parse("""{ "repo": "a/B" }"""), JsonNode.Parse("""{ "name": "No repo" }"""), JsonNode.Parse("""{ "repo": "e/f" }""")]);
        Check("inputs: seeds from GitHub sources and overrides, once each", seeds.SequenceEqual(["A/b", "c/d", "e/f"]));

        // Search: halves the size range until each part is under the cap, and reads every page.
        var files = Enumerable.Range(0, 3500).Select(i => (Repo: $"o/r{i % 1700}", Path: $"f{i}.toc", Size: (long)(i % 50) * 37 + 100)).ToList();
        files.AddRange(Enumerable.Range(0, 1200).Select(i => (Repo: $"big/r{i}", Path: "Same.toc", Size: 5000L))); // one size, over the cap
        var queries = new List<string>();
        Task<SearchPage?> Fake(string q, int page)
        {
            queries.Add($"{q} p{page}");
            var range = q[(q.LastIndexOf("size:") + 5)..].Split("..").Select(long.Parse).ToArray();
            var hits = files.Where(f => f.Size >= range[0] && f.Size <= range[1]).ToList();
            return Task.FromResult<SearchPage?>(new(hits.Count, hits.Skip((page - 1) * 100).Take(100).Select(f => (f.Repo, f.Path)).ToList()));
        }
        var found = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var searchLog = new RunLog();
        await Search.CollectAsync(Fake, "Interface 16001 extension:toc", found, searchLog);
        Check("search: every file under the cap found", files.Where(f => f.Size != 5000).All(f => found.TryGetValue(f.Repo, out var p) && p.Contains(f.Path)));
        Check("search: one size over the cap is reported", searchLog.Capped.Count == 1 && searchLog.Capped[0].Total == 1200 && found.Keys.Count(k => k.StartsWith("big/")) == 1000);
        Check("search: never past page 10", queries.All(q => !q.EndsWith(" p11")));
        Check("search: whole", searchLog.Failed.Count == 0 && searchLog.Unfinished.Count == 0);
        Check("search: few ranges over the cap asked", queries.Count(q => q.EndsWith(" p1") && Fake2Total(q) > 1000) - queries.Count(q => q.EndsWith(" p1") && Fake2Total(q) > 1000 && Covers5000(q)) <= 3);
        int Fake2Total(string q)
        {
            var range = q[(q.LastIndexOf("size:") + 5)..^3].Split("..").Select(long.Parse).ToArray();
            return files.Count(f => f.Size >= range[0] && f.Size <= range[1]);
        }
        bool Covers5000(string q)
        {
            var range = q[(q.LastIndexOf("size:") + 5)..^3].Split("..").Select(long.Parse).ToArray();
            return range[0] <= 5000 && range[1] >= 5000;
        }
        var failingLog = new RunLog();
        Check("search: a failed request makes it unfinished", !await Search.CollectAsync((_, _) => Task.FromResult<SearchPage?>(null), "Interface 1 extension:toc", new(), failingLog));
        Check("search: each failed range reported, and the walk goes on", failingLog.Failed.Count > 1 && failingLog.Failed[0].EndsWith("size:0..255"));
        var pageLog = new RunLog();
        var pageFound = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var pageDone = await Search.CollectAsync((q, n) => n == 3 ? Task.FromResult<SearchPage?>(null) : Fake(q, n), "Interface 16001 extension:toc", pageFound, pageLog);
        Check("search: a failed page is reported, the pages before it kept", !pageDone && pageLog.Failed.Any(f => f.EndsWith("(page 3)")) && pageFound.Count > 0);
        var timeLog = new RunLog();
        var asked = 0;
        var timeFound = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var timeDone = await Search.CollectAsync((q, n) => { asked++; return Fake(q, n); }, "Interface 16001 extension:toc", timeFound, timeLog, () => asked >= 3);
        Check("search: out of time, it stops and keeps what it found", !timeDone && asked == 3 && timeLog.Unfinished.Count == 1 && timeFound.Count > 0);
        var under = new List<string>();
        await Search.CollectAsync((q, n) => { if (n == 1) { under.Add(q); } return Task.FromResult<SearchPage?>(new(10, [])); }, "Interface 2 extension:toc", new(), new RunLog());
        Check("search: under the cap, one request per range, each twice as wide", under.Count == 11 && under[0].EndsWith("size:0..255") && under[1].EndsWith("size:256..767")
            && under[^1].EndsWith($"..{Search.MaxSize}"));
        Check("search: never searched first, then the oldest", Search.Order(["16001", "120105", "11509", "16001", "50504"],
            new Dictionary<string, string> { ["16001"] = "2026-11-20T00:00:00Z", ["11509"] = "2026-11-25T00:00:00Z", ["120105"] = "2026-11-01T00:00:00Z" })
            .SequenceEqual(["50504", "120105", "16001", "11509"]));

        // Parts.
        var beforeRow = JsonNode.Parse("""{"repo":"a/b","pushedAt":"2026-11-20T00:00:00Z","latestRelease":{"tag":"v2"},"releases":{"count":3},"downloads":{"total":10},"downloadHistory":[{"day":"2026-11-27","total":9}],"funding":[{"platform":"GITHUB","url":"https://github.com/sponsors/x","sponsors":true,"ownerOrMember":true},{"platform":"CUSTOM","url":"https://bit.ly/x","resolved":"https://ko-fi.com/x"}]}""")!.AsObject();
        Check("parts: an unchanged repo is kept", Refresh.Unchanged(beforeRow, JsonNode.Parse("""{"nameWithOwner":"a/b","pushedAt":"2026-11-20T00:00:00Z","latestRelease":{"tagName":"v2"}}""")));
        Check("parts: a push, a new release, a rename or no answer is read again", !Refresh.Unchanged(beforeRow, JsonNode.Parse("""{"nameWithOwner":"a/b","pushedAt":"2026-11-21T00:00:00Z","latestRelease":{"tagName":"v2"}}"""))
            && !Refresh.Unchanged(beforeRow, JsonNode.Parse("""{"nameWithOwner":"a/b","pushedAt":"2026-11-20T00:00:00Z","latestRelease":{"tagName":"v3"}}"""))
            && !Refresh.Unchanged(beforeRow, JsonNode.Parse("""{"nameWithOwner":"a/c","pushedAt":"2026-11-20T00:00:00Z","latestRelease":{"tagName":"v2"}}"""))
            && !Refresh.Unchanged(beforeRow, null));
        Check("parts: new Interfaces read every repo again", Refresh.SameEditions(JsonNode.Parse("""{"forever":"16001"}""")!.AsObject(), new Dictionary<string, string> { ["forever"] = "16001" })
            && !Refresh.SameEditions(JsonNode.Parse("""{"forever":"16001"}""")!.AsObject(), new Dictionary<string, string> { ["forever"] = "16002" })
            && !Refresh.SameEditions(null, new Dictionary<string, string> { ["forever"] = "16001" }));
        Check("parts: the change query names each repo", Refresh.Query(["a/b", "c/d"]).Contains("r1: repository(owner: \"c\", name: \"d\") { nameWithOwner pushedAt latestRelease { tagName } }", StringComparison.Ordinal));
        var fresh = JsonNode.Parse("""{"repo":"a/b","releases":null,"downloads":null,"funding":[{"platform":"GITHUB","url":"https://github.com/sponsors/x"},{"platform":"CUSTOM","url":"https://bit.ly/x"},{"platform":"CUSTOM","url":"https://new.example"}]}""")!.AsObject();
        Stats.Carry(fresh, beforeRow);
        Check("parts: a re-read row keeps the stats part's fields", fresh["downloads"]!["total"]!.GetValue<int>() == 10 && fresh["releases"]!["count"]!.GetValue<int>() == 3
            && fresh["downloadHistory"]!.AsArray().Count == 1 && fresh["funding"]![0]!["sponsors"]!.GetValue<bool>() && fresh["funding"]![1]!["resolved"]!.GetValue<string>() == "https://ko-fi.com/x"
            && fresh["funding"]![2]!.AsObject().Count == 2);
        var history = Stats.History(JsonNode.Parse("""[{"day":"2026-11-27","total":9},{"day":"2026-11-28","total":10}]""")!.AsArray(), "2026-11-28", 12);
        Check("parts: one total a day, today's replacing an earlier one", history.Count == 2 && history[1]!["total"]!.GetValue<long>() == 12);
        var month = Stats.History(new JsonArray(Enumerable.Range(1, 30).Select(i => (JsonNode)new JsonObject { ["day"] = $"2026-10-{i:00}", ["total"] = (long)i }).ToArray()), "2026-11-01", 31);
        Check("parts: the newest 30 days, oldest first", month.Count == Stats.HistoryDays && month[0]!["day"]!.GetValue<string>() == "2026-10-02" && month[^1]!["day"]!.GetValue<string>() == "2026-11-01");

        // Pacing.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Check("pacing: searches keep a margin under 10 a minute", Pacing.BeforeNextSearch("4", "1800000030", now) == Pacing.SearchGap && Pacing.SearchGap > TimeSpan.FromSeconds(6));
        Check("pacing: none left, wait for the reset", Pacing.BeforeNextSearch("0", "1800000030", now) == TimeSpan.FromSeconds(33));
        Check("pacing: none left, the reset already past", Pacing.BeforeNextSearch("0", "1799999990", now) == Pacing.SearchGap);
        Check("pacing: retry-after first", Pacing.AfterRefusal(TimeSpan.FromSeconds(90), "0", "1800000030", now, 0) == TimeSpan.FromSeconds(90));
        Check("pacing: retry-after never cut short", Pacing.AfterRefusal(TimeSpan.FromMinutes(25), null, null, now, 0) == TimeSpan.FromMinutes(25));
        Check("pacing: none left, until the reset", Pacing.AfterRefusal(null, "0", "1800000030", now, 0) == TimeSpan.FromSeconds(33));
        Check("pacing: none left again and again, exponentially longer", Pacing.AfterRefusal(null, "0", "1800000030", now, 3) == TimeSpan.FromMinutes(2));
        Check("pacing: neither, at least a minute", Pacing.AfterRefusal(null, "3", "1800000030", now, 0) == TimeSpan.FromMinutes(1));
        Check("pacing: neither, doubling", Pacing.AfterRefusal(null, null, null, now, 1) == TimeSpan.FromMinutes(2)
            && Pacing.AfterRefusal(null, null, null, now, Pacing.Attempts - 1) == TimeSpan.FromMinutes(32));
        Check("pacing: 429 is a rate limit", Pacing.IsRateLimit(429, "5", false, ""));
        Check("pacing: a 403 is one only when it says so", Pacing.IsRateLimit(403, "0", false, "") && Pacing.IsRateLimit(403, "5", true, "")
            && Pacing.IsRateLimit(403, "5", false, "You have exceeded a secondary rate limit") && !Pacing.IsRateLimit(403, "5", false, "Resource not accessible"));

        // Inputs.
        var editions = Inputs.Editions(JsonNode.Parse("""{ "editions": { "retail": { "interface": "120105" }, "forever": { "interface": "16001" }, "retail_ptr": { "of": "retail", "interface": "120107" } } }""")!);
        Check("inputs: live editions, no test clients", editions.Keys.SequenceEqual(["forever", "retail"]) && editions["retail"] == "120105");
        Check("inputs: no editions", Throws(() => Inputs.Editions(JsonNode.Parse("""{ "editions": {} }""")!)));

        Console.WriteLine(failures == 0 ? "Self-test passed." : $"{failures} self-test check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or NullReferenceException)
        {
            return true;
        }
    }
}
