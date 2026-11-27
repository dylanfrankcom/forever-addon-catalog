#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Finds add-ons for every edition on public GitHub repos and writes candidates.json, which the release check works from.
// It lists nothing by itself and never edits catalog.json.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md), with a GitHub token in GITHUB_TOKEN:
//   ... dotnet run scripts/discover.cs                               # every edition in ./catalog.json
//   ... dotnet run scripts/discover.cs -- --edition forever          # one edition (repeatable), for a quicker run
//   ... dotnet run scripts/discover.cs -- --no-search                # only re-read the repos already in candidates.json
//   ... dotnet run scripts/discover.cs -- --self-test                # offline tests
// Options: --catalog <file> (default ./catalog.json), --optout <file> (default ./optout.json), --overrides <folder>
// (default ./overrides), --out <file> (default
// ./candidates.json, also read as the previous run), --summary <file> (also append the report there, as the workflow does
// with $GITHUB_STEP_SUMMARY).
//
// Official APIs only, read-only:
// 1. Code search for .toc files listing each edition's current Interface, split by file size until no query passes the
//    1,000-result cap. Code search allows 10 requests a minute, so requests are spaced 7 seconds apart.
// 2. GraphQL, 10 repos a query: last push, archived or fork, description and topics, the latest release and its files,
//    the newest tags, .pkgmeta, and the .toc files themselves (read again at HEAD, so a stale search index doesn't count).
// 3. README.md's first sentence, only for repos whose main .toc has no Notes and that have no description: the
//    generator's last fallback for a summary. (Reading every README makes the queries heavy enough to time out.)
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
var onlyEditions = new List<string>();
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
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/discover.cs [--edition <id>]... [--no-search] [--catalog <file>] [--optout <file>] [--out <file>] [--summary <file>] | --self-test");
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
    previous = File.Exists(outArg) ? Candidates.Read(File.ReadAllText(outArg)) : new(StringComparer.OrdinalIgnoreCase);
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
var found = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
if (search)
{
    var interfaces = editions.Where(e => onlyEditions.Count == 0 || onlyEditions.Contains(e.Key)).Select(e => e.Value).Distinct().ToList();
    foreach (var iface in interfaces)
    {
        Console.Error.WriteLine($"Searching for .toc files listing {iface} ...");
        await Search.CollectAsync(github.SearchCodeAsync, $"Interface {iface} extension:toc", found, log);
    }
}
foreach (var (repo, row) in previous)
{
    var paths = found.TryGetValue(repo, out var p) ? p : found[repo] = new(StringComparer.Ordinal);
    foreach (var toc in row["tocs"]?.AsArray() ?? [])
    {
        paths.Add(toc!["path"]!.GetValue<string>());
    }
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

Console.Error.WriteLine($"Reading {found.Count} repos ...");
var reads = await github.ReadReposAsync(found, log);
var rows = Candidates.Build(found.Keys, reads, previous, editions, optout, log, forks);
await github.ReadReadmesAsync(rows, previous);
await github.CheckSponsorsAsync(rows, previous);
await Funding.ResolveShortLinksAsync(rows, previous);
File.WriteAllText(outArg, Candidates.Write(editions, rows));

var report = log.Report(rows, editions, search);
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

    public string Report(IReadOnlyCollection<JsonObject> rows, IReadOnlyDictionary<string, string> editions, bool searched)
    {
        var md = new StringBuilder("## Discovery\n\n");
        md.AppendLine($"{rows.Count} candidate repos{(searched ? "" : " (no search: the previous run's repos read again)")}, from {Requests} code search requests.").AppendLine();
        md.AppendLine("| Edition | Interface | Repos |").AppendLine("|---|---|---|");
        foreach (var (id, iface) in editions)
        {
            md.AppendLine($"| {id} | {iface} | {rows.Count(r => r["editions"]!.AsArray().Any(e => e!.GetValue<string>() == id))} |");
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
            md.AppendLine($"- **Failed:** `{query}`");
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

    /// <summary>Collects every file the query finds, halving the size range until each part is under the cap.</summary>
    public static async Task CollectAsync(Func<string, int, Task<SearchPage?>> page, string query, Dictionary<string, SortedSet<string>> found, RunLog log,
        long lo = 0, long hi = MaxSize)
    {
        var q = $"{query} size:{lo}..{hi}";
        var first = await page(q, 1);
        if (first is null)
        {
            log.Failed.Add(q);
            return;
        }
        if (first.Total > Cap && hi > lo)
        {
            var mid = lo + (hi - lo) / 2;
            await CollectAsync(page, query, found, log, lo, mid);
            await CollectAsync(page, query, found, log, mid + 1, hi);
            return;
        }
        if (first.Total > Cap)
        {
            log.Capped.Add((q, first.Total));
        }
        var items = new List<(string Repo, string Path)>(first.Items);
        for (var n = 2; items.Count < Math.Min(first.Total, Cap) && n <= Cap / 100; n++)
        {
            var more = await page(q, n);
            if (more is null)
            {
                log.Failed.Add($"{q} (page {n})");
                break;
            }
            if (more.Items.Count == 0)
            {
                break;
            }
            items.AddRange(more.Items);
        }
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

    public static string Write(IReadOnlyDictionary<string, string> editions, IEnumerable<JsonObject> rows)
    {
        var root = new JsonObject
        {
            ["$comment"] = "Written by scripts/discover.cs. Never edit it by hand.",
            ["editions"] = new JsonObject(editions.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))),
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

        var main = current[0].Fields;
        string? Field(string key) => main.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        var localized = new JsonObject(main.Where(f => f.Key.StartsWith("Notes-", StringComparison.OrdinalIgnoreCase) && f.Value.Length > 0)
            .OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => KeyValuePair.Create(f.Key[6..], (JsonNode?)f.Value)));
        var listed = editions.Where(e => current.Any(t => t.Interfaces.Contains(e.Value))).Select(e => e.Key).ToList();

        var release = node["latestRelease"];
        var tags = node["refs"]?["nodes"]?.AsArray().Select(t => new JsonObject
        {
            ["name"] = t!["name"]!.GetValue<string>(),
            ["date"] = t["target"]?["committedDate"]?.GetValue<string>() ?? t["target"]?["target"]?["committedDate"]?.GetValue<string>(),
        }).ToArray() ?? [];

        var row = new JsonObject
        {
            ["repo"] = node["nameWithOwner"]?.GetValue<string>() ?? repo,
            ["editions"] = Strings(listed),
            ["description"] = node["description"]?.GetValue<string>() is { Length: > 0 } d ? d : null,
            ["topics"] = Strings(node["repositoryTopics"]?["nodes"]?.AsArray().Select(t => t!["topic"]!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>()),
            ["defaultBranch"] = node["defaultBranchRef"]?["name"]?.GetValue<string>(),
            ["pushedAt"] = node["pushedAt"]?.GetValue<string>(),
            ["pkgmeta"] = node["pkgmeta"] is not null,
            ["issues"] = node["hasIssuesEnabled"]?.GetValue<bool>() ?? false,
            ["ownerType"] = node["owner"]?["__typename"]?.GetValue<string>(),
            ["funding"] = Funding.FromNode(node),
            ["readme"] = null, // filled in afterwards, only for repos with no other summary
            ["toc"] = new JsonObject
            {
                ["path"] = current[0].Path,
                ["title"] = Field("Title"),
                ["author"] = Field("Author"),
                ["notes"] = Field("Notes"),
                ["localizedNotes"] = localized.Count > 0 ? localized : null,
                ["version"] = Field("Version"),
                ["category"] = Field("Category"),
                ["iconTexture"] = Field("IconTexture"),
                ["curseforge"] = Field("X-Curse-Project-ID"),
                ["wago"] = Field("X-Wago-ID"),
                ["wowi"] = Field("X-WoWI-ID"),
                ["dependencies"] = Strings(Toc.Dependencies(main)),
                ["optionalDependencies"] = Strings(Toc.List(Field("OptionalDeps"))),
            },
            ["tocs"] = new JsonArray(tocs.OrderBy(t => t.Path, Comparer<string>.Create(Toc.Compare))
                .Select(t => (JsonNode)new JsonObject { ["path"] = t.Path, ["interfaces"] = Strings(t.Interfaces) }).ToArray()),
            ["latestRelease"] = release is null ? null : new JsonObject
            {
                ["tag"] = release["tagName"]?.GetValue<string>(),
                ["name"] = release["name"]?.GetValue<string>(),
                ["publishedAt"] = release["publishedAt"]?.GetValue<string>(),
                ["prerelease"] = release["isPrerelease"]?.GetValue<bool>() ?? false,
                ["assets"] = new JsonArray(release["releaseAssets"]?["nodes"]?.AsArray().Select(a => (JsonNode)new JsonObject
                {
                    ["name"] = a!["name"]!.GetValue<string>(),
                    ["size"] = a["size"]?.GetValue<long>(),
                    ["url"] = a["downloadUrl"]?.GetValue<string>(),
                    ["digest"] = a["digest"]?.GetValue<string>(),
                }).ToArray() ?? []),
            },
            ["tags"] = new JsonArray(tags),
        };
        return (row, null);
    }

    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}

sealed class GitHub : IDisposable
{
    const int BatchSize = 10;
    static readonly TimeSpan CodeSearchGap = TimeSpan.FromSeconds(7); // code search allows 10 requests a minute

    readonly HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
    DateTime lastCodeSearch = DateTime.MinValue;
    int searchRequests;

    public GitHub(string token)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-discover");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public int SearchRequests => searchRequests;

    /// <summary>Sends a request, waiting out rate limits and retrying server errors; null when it still fails.</summary>
    async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> make, string what)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var response = await http.SendAsync(make());
                var text = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    return JsonNode.Parse(text);
                }
                var status = (int)response.StatusCode;
                if (status is 403 or 429 or 500 or 502 or 503 or 504)
                {
                    var wait = RetryWait(response, attempt);
                    Console.Error.WriteLine($"  {what}: {status}, waiting {wait.TotalSeconds:0} s");
                    await Task.Delay(wait);
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
        return null;
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

        static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : t > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : t;
    }

    public async Task<SearchPage?> SearchCodeAsync(string query, int page)
    {
        var wait = lastCodeSearch + CodeSearchGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        var url = $"search/code?per_page=100&page={page}&q={Uri.EscapeDataString(query)}";
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), $"search {query} (page {page})");
        lastCodeSearch = DateTime.UtcNow;
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
        for (var start = 0; start < names.Count; start += BatchSize)
        {
            var chunk = names.Skip(start).Take(BatchSize).ToList();
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
            if ((start / BatchSize) % 20 == 0)
            {
                Console.Error.WriteLine($"  read {Math.Min(start + BatchSize, names.Count)}/{names.Count}");
            }
        }
        return reads;
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

    public void Dispose() => http.Dispose();
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
        nameWithOwner description isArchived isFork pushedAt hasIssuesEnabled
        owner { __typename login }
        fundingLinks { platform url }
        defaultBranchRef { name }
        repositoryTopics(first: 20) { nodes { topic { name } } }
        latestRelease { tagName name publishedAt isPrerelease releaseAssets(first: 20) { nodes { name size downloadUrl digest } } }
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
              "nameWithOwner": "{{name}}", "description": "A thing", "isArchived": false, "isFork": false, "pushedAt": "2026-11-20T10:00:00Z",
              "defaultBranchRef": { "name": "main" },
              "repositoryTopics": { "nodes": [ { "topic": { "name": "wow" } }, { "topic": { "name": "addon" } } ] },
              "latestRelease": { "tagName": "v2", "name": "Two", "publishedAt": "2026-11-19T10:00:00Z", "isPrerelease": false,
                "releaseAssets": { "nodes": [ { "name": "Thing-v2.zip", "size": 1234, "downloadUrl": "https://github.com/a/b/releases/download/v2/Thing-v2.zip", "digest": "sha256:00ff" } ] } },
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
            ("Thing.toc", "## Interface: 120105, 16001\n## Title: |cff00ff00Thing|r\n## Notes: Does things\n## Notes-frFR: Fait\n## Author: Someone\n## Version: 2.0\n## X-Curse-Project-ID: 123\n## X-Wago-ID: abc\n## X-WoWI-ID: 9\n## Category: Bags\n## IconTexture: Interface\\Icons\\X\n## Dependencies: Ace3\n## OptionalDeps: Masque"),
            ("Old.toc", "## Interface: 11508"),
            ("Deleted.toc", null)), Editions);
        Check("row: kept", row is not null && reason is null);
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
        Check("keep: a fork an override names", Candidates.Row("a/b", fork, Editions, known: true).Row is not null);
        var archived = Repo("a/b", ("X.toc", "## Interface: 16001"));
        archived["isArchived"] = true;
        Check("drop: archived", Candidates.Row("a/b", archived, Editions).Reason == "archived");
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
        var failingLog = new RunLog();
        await Search.CollectAsync((_, _) => Task.FromResult<SearchPage?>(null), "Interface 1 extension:toc", new(), failingLog);
        Check("search: a failed request is reported", failingLog.Failed.Count == 1);

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
