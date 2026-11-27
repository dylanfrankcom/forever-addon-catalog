#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Checks, now and then, that each add-on's site links in catalog.json still lead somewhere, and writes linkchecks.json,
// which the generator reads: a link it took from a .toc is left out after several "not found" answers in a row.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md):
//   ... dotnet run scripts/linkcheck.cs                        # ./catalog.json, ./linkchecks.json
//   ... dotnet run scripts/linkcheck.cs -- --self-test         # offline tests
// Options: --catalog <file> (default ./catalog.json), --out <file> (default ./linkchecks.json, also read as the
// previous run), --summary <file> (also append the report there).
//
// Polite by design: one request at a time, a second apart, one try per link per run, no page scraping.
// - CurseForge: never requested. Its site refuses automated requests, so its links are recorded as unchecked.
// - Wago: a HEAD request to the page. 404 or 410 is "not found".
// - WoWInterface: its public API (api.mmoui.com), which gives the add-on's name for an ID it lists; a name that doesn't
//   match the catalog's is reported, never acted on. The API leaves out some categories (Beta-version add-ons), so its
//   404 is recorded as "not-in-api", never as "not found": a WoWInterface link is never dropped by this check.
// - homepage: a HEAD request (GET when HEAD isn't allowed).
// A blocked request (403, 429, 503) or a failure is recorded but never counts as "not found", so a link is never dropped
// because a site turned an automated request away.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    return Tests.Run();
}

string catalogPath = "catalog.json", outPath = "linkchecks.json";
string? summaryPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--catalog" when i + 1 < args.Length: catalogPath = args[++i]; break;
        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
        case "--summary" when i + 1 < args.Length: summaryPath = args[++i]; break;
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/linkcheck.cs [--catalog f] [--out f] [--summary f] | --self-test");
            return 2;
    }
}

var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!;
var previous = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath))?["links"]?.AsObject() : null;
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-linkcheck (+https://github.com/dylanfrankcom/forever-addon-catalog)");

var links = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
var mismatches = new List<string>();
var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mmZ", System.Globalization.CultureInfo.InvariantCulture);
var targets = LinkCheck.Targets(catalog.AsObject()).ToList();
Console.Error.WriteLine($"Checking {targets.Count} links ...");
foreach (var (id, name, site, url) in targets)
{
    if (links.ContainsKey(url))
    {
        continue;
    }
    var before = previous?[url] as JsonObject;
    LinkCheck.Answer answer;
    if (site == "curseforge")
    {
        answer = new("unchecked", null, "CurseForge refuses automated requests");
    }
    else
    {
        answer = await LinkCheck.AskAsync(http, site, url);
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
    links[url] = LinkCheck.Record(before, answer, id, site, now);
    if (answer.Name is { } found && !LinkCheck.SameName(name, found))
    {
        mismatches.Add($"{id}: {url} is \"{found}\" on {site}, the catalog says \"{name}\"");
    }
}

var output = new JsonObject
{
    ["$comment"] = "Written by scripts/linkcheck.cs. Never edit it by hand.",
    ["checked"] = now,
    ["links"] = new JsonObject(links.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))),
};
File.WriteAllText(outPath, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");

var report = LinkCheck.Report(links, mismatches);
Console.Write(report);
if (summaryPath is not null)
{
    File.AppendAllText(summaryPath, report);
}
return 0;

static class LinkCheck
{
    /// <summary>What one request found: ok, not-found, blocked, error or unchecked.</summary>
    public sealed record Answer(string State, string? Name, string? Reason = null);

    public static readonly string[] Sites = ["curseforge", "wago", "wowinterface", "homepage"];

    /// <summary>Each listed add-on's site links and homepage: (id, name, site, url).</summary>
    public static IEnumerable<(string Id, string Name, string Site, string Url)> Targets(JsonObject catalog) =>
        (catalog["addons"]?.AsArray() ?? []).OfType<JsonObject>().SelectMany(a => Sites
            .Where(s => a["links"]?[s] is JsonValue)
            .Select(s => ((string)a["id"]!, (string?)a["name"] ?? "", s, (string)a["links"]![s]!)));

    /// <summary>What an HTTP status means for a link.</summary>
    public static string StateOf(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 400 => "ok",
        404 or 410 => "not-found",
        401 or 403 or 429 or 503 => "blocked",
        _ => "error",
    };

    public static async Task<Answer> AskAsync(HttpClient http, string site, string url)
    {
        try
        {
            if (site == "wowinterface" && Regex.Match(url, @"/info([0-9]+)") is { Success: true } m)
            {
                using var api = await http.GetAsync($"https://api.mmoui.com/v3/game/WOW/filedetails/{m.Groups[1].Value}.json");
                return Api(StateOf(api.StatusCode), StateOf(api.StatusCode) == "ok" ? await api.Content.ReadAsStringAsync() : null);
            }
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            if (head.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                using var get = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                return new(StateOf(get.StatusCode), null);
            }
            return new(StateOf(head.StatusCode), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new("error", null, e.GetType().Name);
        }
    }

    /// <summary>
    /// WoWInterface's API answer: the add-on's name when it lists the ID. A 404 or an empty answer is "not-in-api", never
    /// "not-found": the API leaves out whole categories (Beta-version add-ons have pages but no API entry).
    /// </summary>
    public static Answer Api(string state, string? body)
    {
        if (state == "not-found")
        {
            return new("not-in-api", null, "WoWInterface's API doesn't list it (Beta-version add-ons aren't in it)");
        }
        if (state != "ok")
        {
            return new(state, null);
        }
        var name = (JsonNode.Parse(body ?? "null") as JsonArray)?.FirstOrDefault()?["UIName"]?.GetValue<string>();
        return name is null ? new("not-in-api", null, "WoWInterface's API gave no add-on") : new("ok", name);
    }

    /// <summary>The link's record after this answer: "not found" answers are counted in a row; only ok resets them.</summary>
    public static JsonObject Record(JsonObject? before, Answer answer, string id, string site, string now)
    {
        var count = before?["notFound"]?.GetValue<int>() ?? 0;
        count = answer.State switch { "not-found" => count + 1, "ok" => 0, _ => count };
        var record = new JsonObject { ["id"] = id, ["site"] = site, ["state"] = answer.State, ["checked"] = now, ["notFound"] = count };
        if (answer.Name is not null)
        {
            record["name"] = answer.Name;
        }
        if (answer.Reason is not null)
        {
            record["reason"] = answer.Reason;
        }
        if (answer.State == "ok" || before?["lastOk"] is null)
        {
            record["lastOk"] = answer.State == "ok" ? now : null;
        }
        else
        {
            record["lastOk"] = before["lastOk"]!.DeepClone();
        }
        return record;
    }

    static string Plain(string s) => Regex.Replace(Regex.Replace(s, @"\|c[0-9A-Fa-f]{8}|\|r", ""), "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    /// <summary>Loosely the same name: one contains the other once spaces, colours and punctuation are gone.</summary>
    public static bool SameName(string ours, string theirs)
    {
        var a = Plain(ours);
        var b = Plain(theirs);
        return a.Length == 0 || b.Length == 0 || a.Contains(b) || b.Contains(a);
    }

    public static string Report(IDictionary<string, JsonObject> links, List<string> mismatches)
    {
        var text = new StringBuilder("## Link check\n\n");
        text.Append($"{links.Count} links: ");
        text.Append(string.Join(", ", links.Values.GroupBy(l => (string)l["state"]!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")));
        text.Append(".\n\n");
        var broken = links.Where(l => l.Value["notFound"]!.GetValue<int>() > 0).ToList();
        if (broken.Count > 0)
        {
            text.Append($"Not found ({broken.Count}; a link from a .toc is left out after 3 in a row):\n");
            foreach (var (url, l) in broken)
            {
                text.Append($"- {l["id"]}: {url} ({l["notFound"]} in a row)\n");
            }
            text.Append('\n');
        }
        if (mismatches.Count > 0)
        {
            text.Append($"Names that don't match ({mismatches.Count}; check them, nothing is changed):\n");
            foreach (var m in mismatches)
            {
                text.Append($"- {m}\n");
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}

static class Tests
{
    static int failures;

    static void Check(string name, bool ok)
    {
        if (!ok)
        {
            failures++;
            Console.WriteLine($"FAIL {name}");
        }
    }

    public static int Run()
    {
        Check("status: ok, not found, blocked, error", LinkCheck.StateOf(HttpStatusCode.OK) == "ok" && LinkCheck.StateOf(HttpStatusCode.NotFound) == "not-found"
            && LinkCheck.StateOf(HttpStatusCode.Gone) == "not-found" && LinkCheck.StateOf(HttpStatusCode.Forbidden) == "blocked"
            && LinkCheck.StateOf(HttpStatusCode.TooManyRequests) == "blocked" && LinkCheck.StateOf(HttpStatusCode.InternalServerError) == "error");
        var first = LinkCheck.Record(null, new("not-found", null), "x", "wago", "t1");
        var second = LinkCheck.Record(first, new("blocked", null), "x", "wago", "t2");
        var third = LinkCheck.Record(second, new("not-found", null), "x", "wago", "t3");
        var back = LinkCheck.Record(third, new("ok", null), "x", "wago", "t4");
        Check("record: not found counts in a row; blocked doesn't reset or add; ok resets", (int)first["notFound"]! == 1 && (int)second["notFound"]! == 1
            && (int)third["notFound"]! == 2 && (int)back["notFound"]! == 0 && (string?)back["lastOk"] == "t4" && third["lastOk"] is null);
        var kept = LinkCheck.Record(back, new("error", null, "HttpRequestException"), "x", "wago", "t5");
        Check("record: the last ok is kept through a failure", (string?)kept["lastOk"] == "t4" && (string?)kept["reason"] == "HttpRequestException");
        Check("wowinterface: a name when listed; a 404 is not-in-api, never not-found", LinkCheck.Api("ok", """[{"UIName":"KeyUI"}]""") == new LinkCheck.Answer("ok", "KeyUI")
            && LinkCheck.Api("not-found", null).State == "not-in-api" && LinkCheck.Api("ok", "[]").State == "not-in-api" && LinkCheck.Api("blocked", null).State == "blocked"
            && (int)LinkCheck.Record(null, LinkCheck.Api("not-found", null), "x", "wowinterface", "t")["notFound"]! == 0);
        Check("names: loosely the same", LinkCheck.SameName("|cff00ff00Key UI|r", "KeyUI") && LinkCheck.SameName("Questie", "Questie Classic")
            && !LinkCheck.SameName("Questie", "Bagnon"));
        var catalog = JsonNode.Parse("""{"addons":[{"id":"a","name":"A","links":{"repository":"https://github.com/x/a","wago":"https://addons.wago.io/addons/abcdefgh","curseforge":"https://www.curseforge.com/projects/1"}},{"id":"b","name":"B","links":{"repository":"https://github.com/x/b"}}]}""")!.AsObject();
        Check("targets: site links and homepages only", LinkCheck.Targets(catalog).Select(t => t.Site).SequenceEqual(["curseforge", "wago"]));
        var report = LinkCheck.Report(new Dictionary<string, JsonObject> { ["u"] = third }, ["a: u is \"B\""]);
        Check("report: lists not found and name mismatches", report.Contains("x: u (2 in a row)", StringComparison.Ordinal) && report.Contains("Names that don't match", StringComparison.Ordinal));
        Console.WriteLine(failures == 0 ? "linkcheck.cs self-test passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
