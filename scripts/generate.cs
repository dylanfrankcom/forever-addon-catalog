#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Generates catalog.json from the add-ons whose releases passed the release check (checks.json), the repos discovery
// found (candidates.json) and the hand corrections in overrides/. Nobody writes an entry by hand any more. It also writes
// versions.json: each listed add-on's latest checked release for each edition, so the app finds updates without asking
// GitHub. A file whose content didn't change isn't rewritten, so its ETag stays valid.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md):
//   ... dotnet run scripts/generate.cs                     # writes ./catalog.json and ./icons/
//   ... dotnet run scripts/generate.cs -- --self-test      # offline tests
// Options: --catalog <file> (default ./catalog.json: read for its editions and the IDs and dates it already gave, then
// written), --candidates <file> (default ./candidates.json), --checks <file> (default ./checks.json), --overrides <dir>
// (default ./overrides), --optout <file> (default ./optout.json), --icons <dir> (default ./icons), --no-icons (keep the
// icons as they are), --versions <file> (default ./versions.json), // --confirmed <file> (default ./confirmed.json: CurseForge and Wago versions the maintainer confirmed), --summary <file>.
//
// The listing scope is one setting, Scope below: only add-ons whose release passed for that edition are listed, each
// with every edition its release was checked for. Widening it is a one-line change.
// Exit code 0 when catalog.json and versions.json are up to date, 1 when an input can't be read or the run would drop
// more than half the listed add-ons (a broken run: nothing is written, the last good files stay), 2 for bad arguments.

using System.IO.Compression;
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

string catalogArg = "catalog.json", candidatesArg = "candidates.json", checksArg = "checks.json", overridesArg = "overrides",
    optoutArg = "optout.json", iconsArg = "icons", versionsArg = "versions.json", confirmedArg = "confirmed.json";
string? summaryArg = null;
var icons = true;
for (var i = 0; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--catalog" when value is not null: catalogArg = value; i++; break;
        case "--candidates" when value is not null: candidatesArg = value; i++; break;
        case "--checks" when value is not null: checksArg = value; i++; break;
        case "--overrides" when value is not null: overridesArg = value; i++; break;
        case "--optout" when value is not null: optoutArg = value; i++; break;
        case "--icons" when value is not null: iconsArg = value; i++; break;
        case "--summary" when value is not null: summaryArg = value; i++; break;
        case "--versions" when value is not null: versionsArg = value; i++; break;
        case "--confirmed" when value is not null: confirmedArg = value; i++; break;
        case "--no-icons": icons = false; break;
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/generate.cs [--catalog <file>] [--candidates <file>] [--checks <file>] [--overrides <dir>] [--optout <file>] [--icons <dir>] [--no-icons] [--versions <file>] [--confirmed <file>] [--summary <file>] | --self-test");
            return 2;
    }
}

Inputs inputs;
try
{
    inputs = Inputs.Read(catalogArg, candidatesArg, checksArg, overridesArg, optoutArg);
    if (File.Exists(confirmedArg))
    {
        inputs = inputs with { Confirmed = JsonNode.Parse(File.ReadAllText(confirmedArg))!.AsObject() };
    }
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NullReferenceException or FormatException or KeyNotFoundException)
{
    Console.Error.WriteLine($"Can't read the inputs: {e.Message}");
    return 1;
}

var today = DateOnly.FromDateTime(DateTime.UtcNow);
var result = Generator.Build(inputs, today);
if (Generator.Shrank(inputs.Catalog, result) is { } shrank)
{
    Console.Error.WriteLine(shrank);
    return 1;
}
if (icons)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-generate");
    await Icons.UpdateAsync(result, inputs, iconsArg, url => Icons.DownloadAsync(http, url));
}
else
{
    Icons.Keep(result, inputs, iconsArg);
}
File.WriteAllText(catalogArg, Generator.Write(result, inputs.Catalog));
var previousVersions = File.Exists(versionsArg) ? File.ReadAllText(versionsArg) : null;
if (Versions.Write(result, inputs.Catalog, previousVersions, DateTime.UtcNow) is { } versions)
{
    File.WriteAllText(versionsArg, versions);
}
var report = result.Report();
Console.Write(report);
if (summaryArg is not null)
{
    File.AppendAllText(summaryArg, report);
}
return 0;

/// <summary>
/// What a player can go on to trust an add-on, from public data only (schema/README.md, "Trust signals"): whether a workflow in
/// its own repo built the file, its history, the author's catalog marker, badges, and warning signs.
/// </summary>
static class Trust
{
    /// <summary>"New" lasts this long from the first release; an owner account younger than this is a warning sign.</summary>
    public const int NewDays = 30, NewOwnerDays = 90;
    /// <summary>The release history covers this many releases: "new" needs all of them.</summary>
    const int HistoryReleases = 100;

    public static JsonObject Of(JsonObject row, IReadOnlyList<JsonNode> good, DateOnly today)
    {
        var trust = new JsonObject();
        var built = good.Select(g => Generator.Str(g["built"])).ToList();
        if (built.Count > 0 && built.All(b => b is not null))
        {
            trust["built"] = built[0]; // every listed edition's file was built by a workflow in the repo
        }
        var repoSince = Day(row["created"]);
        var ownerSince = Day(row["ownerCreated"]);
        if (repoSince is { } r)
        {
            trust["repoSince"] = Text(r);
        }
        if (ownerSince is { } o)
        {
            trust["ownerSince"] = Text(o);
        }
        var releases = row["releases"]?["count"]?.GetValue<int>();
        if (releases is { } count)
        {
            trust["releases"] = count;
        }
        if (row["stars"]?.GetValue<int>() is { } stars)
        {
            trust["stars"] = stars;
        }
        // Only the author's own .toc line: a wow-forever topic says the add-on is for the game, not that it's for this catalog.
        if (Generator.Str(row["toc"]?["catalog"])?.Trim().ToLowerInvariant() is { } marker && marker is "yes" or "no")
        {
            trust["marker"] = marker;
        }

        var warnings = new SortedSet<string>(good.SelectMany(g => g["warnings"]?.AsArray() ?? []).Select(w => Generator.Str(w?["sign"])).OfType<string>(), StringComparer.Ordinal);
        if (ownerSince is { } owner && today.DayNumber - owner.DayNumber < NewOwnerDays)
        {
            warnings.Add("new-owner");
        }
        if (releases == 1)
        {
            warnings.Add("single-release");
        }
        var badges = new List<string>();
        if (trust["built"] is not null)
        {
            badges.Add("built");
        }
        var days = (row["releases"]?["days"]?.AsArray() ?? []).Select(Day).OfType<DateOnly>().ToList();
        if (releases is > 0 and <= HistoryReleases && days.Count > 0 && today.DayNumber - days.Min().DayNumber < NewDays)
        {
            badges.Add("new");
        }
        if (badges.Count > 0)
        {
            trust["badges"] = Generator.Array(badges);
        }
        if (warnings.Count > 0)
        {
            trust["warnings"] = Generator.Array(warnings);
        }
        trust["careful"] = warnings.Count > 0;
        return trust;
    }

    /// <summary>Each entry's count of other listed add-ons from the same GitHub owner, and the report's warning signs.</summary>
    public static List<string> Settle(List<JsonObject> addons)
    {
        var owners = addons.Select(a => (Entry: a, Owner: a["sources"]?.AsArray().Select(s => Generator.Str(s?["repo"])).OfType<string>().FirstOrDefault()?.Split('/')[0]))
            .Where(a => a.Owner is not null && a.Entry["trust"] is JsonObject).GroupBy(a => a.Owner!, StringComparer.OrdinalIgnoreCase);
        foreach (var group in owners.Where(g => g.Count() > 1))
        {
            foreach (var (entry, _) in group)
            {
                entry["trust"]!["ownerAddons"] = group.Count() - 1;
            }
        }
        return addons.Where(a => a["trust"]?["warnings"] is JsonArray)
            .Select(a => $"{a["id"]}: warning signs: {string.Join(", ", Generator.Strings(a["trust"]!["warnings"]))}").ToList();
    }

    static DateOnly? Day(JsonNode? value) =>
        Generator.Str(value) is { Length: >= 10 } s && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    static string Text(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Links to an add-on's CurseForge, Wago and WoWInterface pages, from the IDs in its main .toc
/// (X-Curse-Project-ID, X-Wago-ID, X-WoWI-ID). Neither CurseForge nor Wago can be asked automatically, so an ID is
/// taken as the author states it ("from": "toc"), never as verified, and checked for consistency instead: none from a
/// fork, and none that two listed add-ons both claim, unless an override confirms one. The CurseForge and Wago sources an
/// override lists (a person checked them) give the entry those links ("from": "source"); a source the generator took
/// from a .toc is as unchecked as the link. An override sets or removes a link. Nothing here, or anywhere
/// in the catalog, asks CurseForge, Wago or WoWInterface: their links come only from declared IDs.
/// </summary>
static class SiteLinks
{
    public static readonly (string Key, string Field, System.Text.RegularExpressions.Regex Id, Func<string, string> Url)[] Sites =
    [
        ("curseforge", "curseforge", new("^[1-9][0-9]{0,9}$"), id => $"https://www.curseforge.com/projects/{id}"),
        ("wago", "wago", new("^[A-Za-z0-9]{6,12}$"), id => $"https://addons.wago.io/addons/{id}"),
        ("wowinterface", "wowi", new("^[1-9][0-9]{0,9}$"), id => $"https://www.wowinterface.com/downloads/info{id}"),
    ];

    public static readonly HashSet<string> Keys = Sites.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The sources that name a site page: their type and the field with the page's ID.</summary>
    static readonly (string Key, string Type, string Field)[] SourceSites = [("curseforge", "curseforge", "projectId"), ("wago", "wago", "slug")];

    /// <summary>
    /// Links an entry to the CurseForge and Wago pages its override's sources name ("from": "source"), in place of a
    /// .toc's: a person listed them. A link the override sets or removes stays as the override left it.
    /// </summary>
    public static void FromSources(JsonObject entry, JsonObject? over)
    {
        if (over?["sources"] is not JsonArray sources)
        {
            return;
        }
        foreach (var (key, type, field) in SourceSites)
        {
            if ((over["links"] as JsonObject)?.ContainsKey(key) == true
                || sources.OfType<JsonObject>().FirstOrDefault(s => Generator.Str(s["type"]) == type)?[field]?.ToString() is not { } id)
            {
                continue;
            }
            var site = Sites.First(s => s.Key == key);
            if (!site.Id.IsMatch(id))
            {
                continue;
            }
            if (entry["links"] is not JsonObject links)
            {
                entry["links"] = links = new JsonObject();
            }
            links[key] = site.Url(id);
            if (entry["from"] is not JsonObject from)
            {
                entry["from"] = from = new JsonObject();
            }
            from[key] = "source";
        }
    }

    public static void Add(JsonObject links, JsonObject from, JsonObject toc, bool fork)
    {
        if (fork)
        {
            return; // a fork's .toc keeps the original's IDs
        }
        foreach (var (key, field, id, url) in Sites)
        {
            if (Generator.Str(toc[field])?.Trim() is { } value && id.IsMatch(value))
            {
                links[key] = url(value);
                from[key] = "toc";
            }
        }
    }

    /// <summary>Leaves out the site links that can't be trusted; returns a notice for each.</summary>
    public static List<string> Settle(IReadOnlyList<JsonObject> addons, IReadOnlyDictionary<string, JsonObject>? overrides = null)
    {
        var notices = new List<string>();
        foreach (var entry in addons)
        {
            FromSources(entry, overrides?.GetValueOrDefault(entry["id"]!.GetValue<string>()));
        }
        static void Remove(JsonObject entry, string key)
        {
            entry["links"]!.AsObject().Remove(key);
            (entry["from"] as JsonObject)?.Remove(key);
        }
        foreach (var key in Keys)
        {
            var claims = addons.Where(a => Generator.Str(a["links"]?[key]) is not null)
                .GroupBy(a => Generator.Str(a["links"]![key])!.ToLowerInvariant()).Where(g => g.Count() > 1);
            foreach (var claim in claims)
            {
                var stated = claim.Where(a => Generator.Str(a["from"]?[key]) == "toc").ToList();
                foreach (var entry in stated)
                {
                    Remove(entry, key);
                }
                notices.Add($"{key} link {claim.Key} is claimed by {string.Join(", ", claim.Select(a => a["id"]))}; left out of {(stated.Count > 0 ? string.Join(", ", stated.Select(a => a["id"])) : "none (overrides set it)")}");
            }
        }
        return notices;
    }
}

/// <summary>The listing scope: the one setting that decides which add-ons are listed.</summary>
static class Settings
{
    /// <summary>Only add-ons whose latest release passed (or isn't updated yet) for this edition are listed.</summary>
    public const string Scope = "forever";

    /// <summary>The catalog format this generator writes.</summary>
    public const int SchemaVersion = 4;

    public const string IconBase = "https://raw.githubusercontent.com/dylanfrankcom/forever-addon-catalog/main/icons/";

    /// <summary>The most characters in a summary: longer ones are cut at a word.</summary>
    public const int SummaryLength = 120;
}

sealed record Inputs(JsonObject Catalog, Dictionary<string, JsonObject> Candidates, List<JsonObject> Checks,
    Dictionary<string, JsonObject> Overrides, JsonObject CuratedAuthors, List<string> OptOut, JsonObject? Confirmed = null)
{
    public static Inputs Read(string catalog, string candidates, string checks, string overrides, string optout)
    {
        var cat = JsonNode.Parse(File.ReadAllText(catalog))!.AsObject();
        var rows = JsonNode.Parse(File.ReadAllText(candidates))!["candidates"]!.AsArray()
            .ToDictionary(r => r!["repo"]!.GetValue<string>(), r => r!.AsObject(), StringComparer.OrdinalIgnoreCase);
        var checked_ = JsonNode.Parse(File.ReadAllText(checks))!["repos"]!.AsArray().Select(r => r!.AsObject()).ToList();
        var overrideFiles = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var authors = new JsonObject();
        if (Directory.Exists(overrides))
        {
            foreach (var file in Directory.GetFiles(overrides, "*.json").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                if (name == "_authors")
                {
                    authors = node["authors"]?.AsObject() ?? new JsonObject();
                }
                else
                {
                    overrideFiles[name] = node;
                }
            }
        }
        var opt = File.Exists(optout)
            ? JsonNode.Parse(File.ReadAllText(optout))!["repos"]!.AsArray().Select(r => Generator.Str(r!["repo"])!.Trim()).ToList()
            : [];
        return new(cat, rows, checked_, overrideFiles, authors, opt);
    }

    public bool OptedOut(string repo) => OptOut.Any(o => o.Contains('/')
        ? o.Equals(repo, StringComparison.OrdinalIgnoreCase)
        : repo.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Cleaning the author's own text: colour codes, textures, versions and load conditions out.</summary>
static partial class Text
{
    /// <summary>
    /// A name's leading tag in angle brackets as plain words: "&lt;DBM Core&gt; Main Core" gives "DBM Core Main Core", and a
    /// tag that repeats the next word goes ("&lt;BGNext&gt; BGNext Raid" gives "BGNext Raid"). Square brackets stay.
    /// </summary>
    public static string Untag(string name)
    {
        var m = LeadingTag().Match(name);
        if (!m.Success)
        {
            return name;
        }
        var tag = m.Groups[1].Value.Trim();
        var rest = m.Groups[2].Value.Trim();
        return rest.Length == 0 ? tag
            : rest.StartsWith(tag, StringComparison.OrdinalIgnoreCase) && (rest.Length == tag.Length || !char.IsLetterOrDigit(rest[tag.Length])) ? rest
            : $"{tag} {rest}";
    }

    [GeneratedRegex(@"^<([^<>]+)>\s*(.*)$")]
    private static partial Regex LeadingTag();

    public static string Clean(string? text, bool dropVersions)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        // Packager placeholders (@project-version@) are never part of a name or summary.
        var s = System.Text.RegularExpressions.Regex.Replace(text, "@[A-Za-z0-9_-]+@", "");
        s = Colour().Replace(s, "");
        s = s.Replace("|r", "", StringComparison.OrdinalIgnoreCase).Replace("|n", " ", StringComparison.OrdinalIgnoreCase);
        s = Texture().Replace(s, "");
        s = Conditions().Replace(s, "");
        if (dropVersions)
        {
            s = Version().Replace(s, "");
        }
        s = EmptyBrackets().Replace(s, "");
        s = Spaces().Replace(s, " ").Trim();
        // A leading "!" is a load-order prefix (!!!LuaWorkbench), not part of the name; a trailing one is (Ding!).
        return s.Trim(' ', '-', '|', ':', ',', '–', '—').Trim().TrimStart('!').TrimStart();
    }

    /// <summary>A summary: cleaned, then cut at a word to <see cref="Settings.SummaryLength"/> characters.</summary>
    public static string Summary(string? text)
    {
        var s = Clean(text, dropVersions: false);
        if (s.Length <= Settings.SummaryLength)
        {
            return s;
        }
        var cut = s.LastIndexOf(' ', Settings.SummaryLength - 1);
        return s[..(cut > 40 ? cut : Settings.SummaryLength - 1)].TrimEnd(' ', ',', ';', ':', '-') + "…";
    }

    /// <summary>An ID from a name: lowercase letters and digits, words joined by single hyphens (WeakAuras gives weakauras, DBM-Core dbm-core).</summary>
    public static string Slug(string text)
    {
        var s = NotIdChars().Replace(text.ToLowerInvariant(), "-").Trim('-');
        s = Hyphens().Replace(s, "-");
        return s.Length > 64 ? s[..64].TrimEnd('-') : s;
    }

    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}", RegexOptions.IgnoreCase)]
    private static partial Regex Colour();

    [GeneratedRegex(@"\|[TA][^|]*\|[ta]")]
    private static partial Regex Texture();

    [GeneratedRegex(@"\[(Allow|Exclude)LoadGameType[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex Conditions();

    // "v1.2.3", "1.2", "(1.0.4-beta)", "r123" ... standing alone.
    [GeneratedRegex(@"(?<![\w.-])[\(\[]?(v|r)?\d+(\.\d+)+([-_.+][0-9A-Za-z.]+)?[\)\]]?(?![\w.])|(?<![\w.])\(?v\d+\)?(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex Version();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotIdChars();

    [GeneratedRegex(@"-+")]
    private static partial Regex Hyphens();
}

/// <summary>The catalog's fixed tags, from the .toc's Category and the repository's topics.</summary>
static class Tags
{
    // Most specific first: "Bags & Inventory" is bags, not ui.
    static readonly (string Tag, string[] Words)[] Map =
    [
        ("auction-house", ["auction", "economy", "gold"]),
        ("bags", ["bag", "inventory", "bank"]),
        ("professions", ["profession", "crafting", "tradeskill", "gathering", "herbalism", "mining", "fishing", "cooking"]),
        ("quests", ["quest", "leveling", "level-up"]),
        ("map", ["map", "minimap", "navigation", "coordinates", "waypoint"]),
        ("dungeons", ["dungeon", "raid", "boss", "group finder", "lfg", "dbm"]),
        ("loot", ["loot"]),
        ("chat", ["chat", "whisper", "communication"]),
        ("class", ["class", "rotation", "hunter", "warlock", "rogue", "druid", "paladin", "priest", "mage", "shaman", "warrior"]),
        ("combat", ["combat", "damage", "dps", "threat", "cooldown", "buff", "debuff", "pvp", "healing"]),
        ("ui", ["user interface", "ui", "unit frame", "unit-frame", "action bar", "action-bar", "tooltip", "nameplate", "hud", "interface enhancement", "masque", "elvui"]),
    ];

    public static List<string> From(string? category, IEnumerable<string> topics)
    {
        var tags = new List<string>();
        foreach (var text in new[] { category ?? "" }.Concat(topics))
        {
            var words = " " + text.ToLowerInvariant().Replace('&', ' ').Replace('_', ' ') + " ";
            foreach (var (tag, keys) in Map)
            {
                if (!tags.Contains(tag) && keys.Any(k => Matches(words, k)))
                {
                    tags.Add(tag);
                }
            }
        }
        return tags.Take(3).ToList();
    }

    // "ui" as a word, not inside "guild"; longer keys as a prefix of a word ("quests", "professions").
    static bool Matches(string words, string key) => key.Length <= 3
        ? Regex.IsMatch(words, $@"(?<![a-z]){Regex.Escape(key)}(?![a-z])")
        : Regex.IsMatch(words, $@"(?<![a-z]){Regex.Escape(key)}");
}

/// <summary>One add-on whose release passed for the scope edition: its discovery row and its check.</summary>
sealed record Listing(string Repo, JsonObject Row, JsonObject Good, JsonObject Main, List<string> Folders, string MainFolder, int? CurseForge, string Published,
    string? TagPrefix = null)
{
    public string Owner => Repo[..Repo.IndexOf('/')];
}

/// <summary>
/// An author's support links, from their repos' FUNDING.yml as GitHub reads it. A wrong link sends a player's money to
/// the wrong person, so only links that pass every rule are kept, and every refusal is reported.
/// </summary>
static class Support
{
    static readonly System.Text.RegularExpressions.Regex Https = new(@"^https://[^/\s]+(/\S*)?$");
    static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "is.gd", "ow.ly", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc",
    };

    /// <summary>
    /// The links of one repo, or why each was refused: a GitHub Sponsors link needs a Sponsors listing and must be the
    /// repo owner's (or a public member's of the organisation that owns it); every link must be https, and a custom one
    /// can't be a link shortener.
    /// </summary>
    public static (List<string> Links, List<string> Refused) FromRow(JsonObject row)
    {
        var links = new List<string>();
        var refused = new List<string>();
        var repo = row["repo"]?.GetValue<string>() ?? "";
        foreach (var f in row["funding"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var platform = f["platform"]?.GetValue<string>() ?? "";
            var url = Upgrade(f["url"]?.GetValue<string>() ?? "");
            if (platform == "GITHUB")
            {
                var m = System.Text.RegularExpressions.Regex.Match(url, @"^https://github\.com/(?:sponsors/)?([A-Za-z0-9][A-Za-z0-9-]{0,38})/?$");
                if (!m.Success)
                {
                    refused.Add($"{repo}: {url} isn't a GitHub account");
                }
                else if (f["ownerOrMember"]?.GetValue<bool>() != true)
                {
                    refused.Add($"{repo}: github.com/sponsors/{m.Groups[1].Value} isn't the repo's owner or a public member of its organisation");
                }
                else if (f["sponsors"]?.GetValue<bool>() != true)
                {
                    refused.Add($"{repo}: {m.Groups[1].Value} has no GitHub Sponsors listing");
                }
                else
                {
                    links.Insert(links.Count(l => l.StartsWith("https://github.com/sponsors/", StringComparison.Ordinal)), $"https://github.com/sponsors/{m.Groups[1].Value}");
                }
                continue;
            }
            if (!Https.IsMatch(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                refused.Add($"{repo}: {url} isn't an https link");
            }
            else if (platform == "CUSTOM" && IsShortLink(uri))
            {
                // Discovery asked the shortener where it leads; the real page is listed, never the short link.
                var resolved = f["resolved"]?.GetValue<string>();
                if (resolved is not null && Https.IsMatch(resolved) && Uri.TryCreate(resolved, UriKind.Absolute, out var real) && !IsShortLink(real))
                {
                    links.Add(resolved);
                }
                else
                {
                    refused.Add($"{repo}: {url} is a link shortener that didn't resolve to an https page");
                }
            }
            else
            {
                links.Add(url);
            }
        }
        return (links, refused);
    }

    static bool IsShortLink(Uri uri) => Shorteners.Contains(uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host);

    /// <summary>
    /// The same page over https: FUNDING.yml's custom links are often written as "paypal.me/name" or "http://...". Any
    /// other scheme is left as it is, and refused.
    /// </summary>
    public static string Upgrade(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..]
        : !url.Contains("://", StringComparison.Ordinal) && url.Length > 0 && !url.StartsWith('/') ? "https://" + url
        : url;

    /// <summary>An author's links from all their repos, GitHub Sponsors first, each once (case and a final "/" ignored).</summary>
    public static (List<string> Links, List<string> Refused) ForAuthor(IEnumerable<JsonObject> rows)
    {
        var all = rows.OrderBy(r => r["repo"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase).Select(FromRow).ToList();
        var links = all.SelectMany(a => a.Links).ToList();
        var ordered = links.Where(l => l.StartsWith("https://github.com/sponsors/", StringComparison.Ordinal))
            .Concat(links.Where(l => !l.StartsWith("https://github.com/sponsors/", StringComparison.Ordinal)))
            .DistinctBy(l => l.TrimEnd('/').ToLowerInvariant()).ToList();
        return (ordered, all.SelectMany(a => a.Refused).Distinct().ToList());
    }

    /// <summary>Support links that appeared, changed or went away since the previous catalog, for a person to look at.</summary>
    public static List<string> Changes(JsonObject? before, JsonObject after)
    {
        var notes = new List<string>();
        foreach (var id in (before?.Select(p => p.Key) ?? []).Union(after.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var old = Generator.Strings(before?[id]?["sponsor"]);
            var now = Generator.Strings(after[id]?["sponsor"]);
            if (!old.SequenceEqual(now) && (old.Count > 0 || now.Count > 0) && (before?.ContainsKey(id) == true || now.Count > 0))
            {
                notes.Add($"{id}: support links {(old.Count == 0 ? "added" : now.Count == 0 ? "removed" : "changed")}: {(old.Count > 0 ? string.Join(" ", old) + " -> " : "")}{(now.Count > 0 ? string.Join(" ", now) : "none")}");
            }
        }
        return notes;
    }
}

sealed class Result
{
    public List<JsonObject> Addons { get; } = [];
    public JsonObject Authors { get; } = new();
    /// <summary>Each GitHub-released entry's last good release per edition, from the release check, by entry ID.</summary>
    public Dictionary<string, (string Repo, JsonObject Good)> Good { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, List<string>> Dropped { get; } = new(StringComparer.Ordinal);
    public List<string> Notices { get; } = [];
    /// <summary>Icons to fetch: an entry, the release file and the image's path inside it.</summary>
    public List<(JsonObject Entry, string ZipUrl, string Path)> IconJobs { get; } = [];

    public void Drop(string reason, string what)
    {
        if (!Dropped.TryGetValue(reason, out var list))
        {
            Dropped[reason] = list = [];
        }
        list.Add(what);
    }

    public string Report()
    {
        var md = new StringBuilder("## Generated catalog\n\n");
        md.AppendLine($"{Addons.Count} add-ons (scope {Settings.Scope}), {Authors.Count} authors, {Addons.Count(a => a["icon"] is not null)} with an icon.").AppendLine();
        foreach (var (reason, list) in Dropped)
        {
            md.AppendLine($"- Not listed, {reason}: {list.Count}" + (list.Count <= 10 ? $" ({string.Join(", ", list)})" : ""));
        }
        foreach (var notice in Notices)
        {
            md.AppendLine($"- {notice}");
        }
        return md.AppendLine().ToString();
    }
}

static class Generator
{
    static readonly string[] OverrideOnly = ["repo", "tagPrefix", "partOf", "$schema", "$comment"];
    static readonly string[] FieldOrder = ["id", "name", "otherNames", "summary", "summaries", "languages", "author", "tags", "icon", "added", "sources", "links", "folders", "tocPath",
        "editions", "requires", "requiresFolders", "optional", "conflicts", "notes", "ownApp", "linkOnly", "withdrawn", "overrides", "from"];

    public static List<string> Strings(JsonNode? node) => node?.AsArray().Select(s => s!.GetValue<string>()).ToList() ?? [];

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>The catalog schema's folder pattern.</summary>
    static readonly System.Text.RegularExpressions.Regex PlainFolderPattern = new("^[A-Za-z0-9_!]([A-Za-z0-9_.! -]*[A-Za-z0-9_!-])?$");

    static System.Text.RegularExpressions.Regex PlainFolder() => PlainFolderPattern;

    /// <summary>Many folders that don't share a name with the main one: a bundle of unrelated add-ons, not one add-on.</summary>
    public static bool IsCompilation(IReadOnlyList<string> folders, string main)
    {
        var stem = main.Length >= 3 ? main[..3] : main;
        return folders.Count(f => !f.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && !f.StartsWith('!')) > 5;
    }

    static int? CurseForgeId(JsonObject row) => int.TryParse(Str(row["toc"]?["curseforge"]), out var id) && id > 0 ? id : null;

    /// <summary>The GitHub repos, CurseForge IDs and folders an entry (previous or override) is known by.</summary>
    static (List<string> Repos, List<int> CurseForge, List<string> Folders) Keys(JsonObject entry)
    {
        var sources = entry["sources"]?.AsArray().Select(s => s!.AsObject()).ToList() ?? [];
        var repos = sources.Where(s => Str(s["type"]) == "github").Select(s => Str(s["repo"])!).ToList();
        if (Str(entry["repo"]) is { } repo)
        {
            repos.Add(repo);
        }
        var cf = sources.Where(s => Str(s["type"]) == "curseforge").Select(s => s["projectId"]!.GetValue<int>()).ToList();
        return (repos, cf, Strings(entry["folders"]));
    }

    /// <summary>The run report's heading for a dropped re-upload that may be a hand-listed add-on's official source.</summary>
    public const string OfficialSourceReason = "possibly the official source for a hand-listed add-on (not listed: confirm and switch, or ask)";

    /// <summary>
    /// What ties a repo dropped as a re-upload of the hand-listed <paramref name="entry"/> to it. The repo's claim (its
    /// .toc names the entry's CurseForge project or Wago ID) can be written by anyone; the catalog's side (its owner is a
    /// profile on the entry's curated author record, or the entry's own links point to it) was set by a person here.
    /// </summary>
    public static List<(string Text, bool CatalogSide)> OfficialSignals(JsonObject entry, Listing l, JsonObject curatedAuthors)
    {
        var signals = new List<(string Text, bool CatalogSide)>();
        var sources = entry["sources"]?.AsArray().Select(s => s!.AsObject()).ToList() ?? [];
        if (l.CurseForge is { } cf && sources.Any(s => Str(s["type"]) == "curseforge" && s["projectId"]?.GetValue<int>() == cf))
        {
            signals.Add(("its .toc names the entry's CurseForge project", false));
        }
        if (Str(l.Row["toc"]?["wago"])?.Trim() is { Length: > 0 } wago
            && (sources.Any(s => Str(s["type"]) == "wago" && string.Equals(Str(s["slug"]), wago, StringComparison.OrdinalIgnoreCase))
                || string.Equals(Str(entry["links"]?["wago"])?.TrimEnd('/').Split('/')[^1], wago, StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add(("its .toc names the entry's Wago project", false));
        }
        var profile = $"https://github.com/{l.Owner}";
        if (Str(entry["author"]) is { } author && curatedAuthors[author]?["profiles"]?.AsArray()
                .Any(p => string.Equals(Str(p)?.TrimEnd('/'), profile, StringComparison.OrdinalIgnoreCase)) == true)
        {
            signals.Add(("its owner is on the author's record", true));
        }
        var repoUrl = $"https://github.com/{l.Repo}";
        if (entry["links"]?.AsObject().Any(link => Str(link.Value)?.TrimEnd('/') is { } url
                && (url.Equals(repoUrl, StringComparison.OrdinalIgnoreCase) || url.StartsWith(repoUrl + "/", StringComparison.OrdinalIgnoreCase))) == true)
        {
            signals.Add(("the entry's own links point to it", true));
        }
        return signals;
    }

    static bool Matches(JsonObject entry, Listing l, bool byFolder)
    {
        var (repos, cf, folders) = Keys(entry);
        return repos.Contains(l.Repo, StringComparer.OrdinalIgnoreCase)
            || (l.CurseForge is { } id && cf.Contains(id))
            || (byFolder && folders.Contains(l.MainFolder, StringComparer.OrdinalIgnoreCase));
    }

    public static Result Build(Inputs inputs, DateOnly today)
    {
        var result = new Result();
        var editionIds = inputs.Catalog["editions"]!.AsObject().Select(e => e.Key).ToList();
        var previous = inputs.Catalog["addons"]?.AsArray().Select(a => a!.AsObject()).ToList() ?? [];

        // 1. Every add-on whose release passed for the scope edition.
        var listings = new List<Listing>();
        foreach (var check in inputs.Checks.OrderBy(c => c["repo"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
        {
            var repo = check["repo"]!.GetValue<string>();
            if (check["good"]?[Settings.Scope] is not JsonObject main)
            {
                continue;
            }
            if (inputs.OptedOut(repo))
            {
                result.Drop("on the never-add list", repo);
                continue;
            }
            if (!inputs.Candidates.TryGetValue(repo, out var row))
            {
                result.Drop("no longer found by discovery", repo);
                continue;
            }
            // A repo that releases several add-ons is listed only by the tag prefixes its overrides name, one entry each.
            var prefix = Str(check["tagPrefix"]);
            var prefixes = inputs.Overrides.Values.Where(o => string.Equals(Str(o["repo"]), repo, StringComparison.OrdinalIgnoreCase))
                .Select(o => Str(o["tagPrefix"])).OfType<string>().ToList();
            if (prefix is null && prefixes.Count > 0)
            {
                result.Drop("a repo that releases several add-ons: listed by tag prefix only", repo);
                continue;
            }
            if (prefix is not null && !prefixes.Contains(prefix, StringComparer.Ordinal))
            {
                result.Drop("a tag prefix no override names", $"{repo} {prefix}");
                continue;
            }
            // The author's own no, in the .toc: never listed (a yes is shown in trust.marker).
            if (Str(row["toc"]?["catalog"])?.Trim().Equals("no", StringComparison.OrdinalIgnoreCase) == true)
            {
                result.Drop("the author asked not to be listed (X-Forever-Catalog: no)", repo);
                continue;
            }
            var folders = Strings(main["folders"]);
            var mainFolder = Str(main["main"]) ?? folders.FirstOrDefault() ?? "";
            if (IsCompilation(folders, mainFolder))
            {
                result.Drop("a compilation of unrelated add-ons", repo);
                continue;
            }
            // (Discovery read the repo's .toc files, which belong to all of its add-ons when it releases several.)
            if (prefix is null && SeparateAddons(row, folders, mainFolder) is { Count: > 0 } separate)
            {
                // An entry is one add-on: a release that also ships another, unrelated one is someone's collection.
                result.Drop("a collection: the release also ships separate add-ons", $"{repo} ({mainFolder} with {string.Join(", ", separate)})");
                continue;
            }
            if (folders.FirstOrDefault(f => !PlainFolder().IsMatch(f)) is { } odd)
            {
                // The schema's folder names only: no paths, no leading dot, no trailing space or dot.
                result.Drop("a folder name the catalog can't list", $"{repo} ({odd})");
                continue;
            }
            // The repo's .toc names one CurseForge project: with several add-ons, it can't be told whose.
            listings.Add(new(repo, row, check["good"]!.AsObject(), main, folders, mainFolder, prefix is null ? CurseForgeId(row) : null,
                Str(main["published"]) ?? "", prefix));
        }

        // 2. One entry per add-on: repos that share a CurseForge project or a folder are copies of one add-on.
        var known = listings.ToDictionary(l => l, l => previous.Any(p => Matches(p, l, byFolder: false)) || inputs.Overrides.Values.Any(o => Matches(o, l, byFolder: false)));
        var kept = new List<Listing>();
        foreach (var group in Groups(listings))
        {
            var ranked = group.OrderByDescending(l => known[l]).ThenByDescending(l => l.Good.Count)
                .ThenByDescending(l => l.Published, StringComparer.Ordinal).ThenBy(l => l.Repo, StringComparer.OrdinalIgnoreCase).ToList();
            kept.Add(ranked[0]);
            foreach (var copy in ranked.Skip(1))
            {
                result.Drop("a copy of another listed add-on (same CurseForge project or folder)", $"{copy.Repo} (kept {ranked[0].Repo})");
            }
        }
        kept.Sort((a, b) => string.Compare(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase));

        // 3. IDs: an override's, else the one the previous catalog gave it (matched by repo, CurseForge project or main
        // folder, so a renamed repo keeps it), else a new one from the main folder.
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new Dictionary<Listing, string>();
        var overrideFor = new Dictionary<Listing, string>();
        foreach (var l in kept.ToList())
        {
            // An override naming a repo corrects that repo only. One without (an add-on written by hand because it isn't
            // released on GitHub) never takes a GitHub listing: a repo that shares its folder or CurseForge project is
            // someone else's re-upload, and its files must never reach the catalog or versions.json.
            var o = inputs.Overrides.FirstOrDefault(o => o.Value["repo"] is not null
                ? string.Equals(Str(o.Value["repo"]), l.Repo, StringComparison.OrdinalIgnoreCase) && Str(o.Value["tagPrefix"]) == l.TagPrefix
                : Matches(o.Value, l, byFolder: true)).Key;
            if (o is not null && inputs.Overrides[o]["repo"] is null)
            {
                // Still never listed from here, but one tied to the entry may be its author's own new source: reported
                // apart, with what ties it, for the agent to switch the entry or ask (CLAUDE.md, scripts/README.md).
                if (OfficialSignals(inputs.Overrides[o], l, inputs.CuratedAuthors) is { Count: > 0 } signals)
                {
                    var both = signals.Any(s => s.CatalogSide) && signals.Any(s => !s.CatalogSide);
                    result.Drop(OfficialSourceReason, $"{l.Repo} ({o}: {string.Join("; ", signals.Select(s => s.Text))}; "
                        + $"{(both ? "tied both ways" : signals.All(s => s.CatalogSide) ? "the catalog's side only" : "the repo's claim only")})");
                }
                else
                {
                    result.Drop("a re-upload of an add-on listed from its own site (same folder or CurseForge project as a hand-written entry)", $"{l.Repo} ({o})");
                }
                kept.Remove(l);
                continue;
            }
            if (o is not null && !overrideFor.ContainsValue(o))
            {
                overrideFor[l] = o;
                ids[l] = o;
                used.Add(o);
            }
        }
        foreach (var l in kept.Where(l => !ids.ContainsKey(l)))
        {
            var p = previous.FirstOrDefault(p => Matches(p, l, byFolder: true)) is { } match ? Str(match["id"]) : null;
            if (p is not null && used.Add(p))
            {
                ids[l] = p;
            }
        }
        var reserved = previous.Select(p => Str(p["id"])!).Concat(inputs.Overrides.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var l in kept.Where(l => !ids.ContainsKey(l)))
        {
            var stem = Text.Slug(l.MainFolder) is { Length: > 0 } s ? s : Text.Slug(l.Repo[(l.Repo.IndexOf('/') + 1)..]);
            var id = stem;
            if (id.Length == 0 || used.Contains(id) || reserved.Contains(id))
            {
                id = Text.Slug($"{stem}-{l.Owner}");
            }
            for (var n = 2; used.Contains(id) || reserved.Contains(id); n++)
            {
                id = $"{Text.Slug($"{stem}-{l.Owner}")}-{n}";
            }
            used.Add(id);
            ids[l] = id;
        }

        // 4. The entries.
        var authors = new Authors();
        var deps = new Dictionary<JsonObject, (List<string> Required, List<string> Optional)>();
        var reposOf = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        // Parts of another entry (an override's "partOf"): checked like any release, but their folders join that entry.
        var parts = new List<(string Id, string Into, List<string> Folders)>();
        foreach (var l in kept)
        {
            if (overrideFor.TryGetValue(l, out var partOverride) && Str(inputs.Overrides[partOverride]["partOf"]) is { } into)
            {
                parts.Add((ids[l], into, l.Folders));
                continue;
            }
            var entry = Entry(l, ids[l], previous, editionIds, today, authors);
            result.Good[ids[l]] = (l.Repo, l.Good);
            if (overrideFor.TryGetValue(l, out var o))
            {
                Apply(entry, inputs.Overrides[o]);
            }
            if (entry["otherNames"] is null && OtherName(l.Repo, l.TagPrefix, Str(entry["name"])) is { } other)
            {
                entry["otherNames"] = Array([other]);
            }
            if (entry["summary"] is null)
            {
                result.Drop("no summary (no .toc Notes, repository description or README, and no override)", l.Repo);
                continue;
            }
            var own = MainToc(l);
            deps[entry] = (Strings(own["dependencies"]), Strings(own["optionalDependencies"]));
            result.Addons.Add(entry);
            var authorId = entry["author"]!.GetValue<string>();
            (reposOf.TryGetValue(authorId, out var rows) ? rows : reposOf[authorId] = []).Add(l.Row);
            if (entry["icon"] is null && IconPath(Str(own["iconTexture"]), l.Folders) is { } icon)
            {
                result.IconJobs.Add((entry, Str(l.Main["url"])!, icon));
            }
        }

        // Only into a link-only entry: the app installs neither, so it only needs to know whose folders they are.
        foreach (var (id, into, folders) in parts)
        {
            var whole = result.Addons.FirstOrDefault(a => Str(a["id"]) == into);
            if (whole is null)
            {
                result.Drop("part of an add-on that isn't listed", $"{id} (part of {into})");
            }
            else if (whole["linkOnly"] is null)
            {
                result.Drop("part of an add-on that isn't link only", $"{id} (part of {into})");
            }
            else
            {
                whole["folders"] = Array(Strings(whole["folders"]).Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        // 5. Overrides with no listed add-on: complete entries for add-ons that aren't released on GitHub. An override
        // tied to a GitHub repo ("repo") is never listed by itself: that repo's release has to pass the check.
        foreach (var (id, o) in inputs.Overrides.Where(o => !overrideFor.ContainsValue(o.Key)))
        {
            if (o["repo"] is not null)
            {
                result.Drop("override for a repo whose release isn't listable", $"{id} ({Str(o["repo"])})");
                continue;
            }
            var entry = new JsonObject { ["id"] = id };
            Apply(entry, o);
            if (o["editions"] is JsonObject hand)
            {
                var (editions, unknown) = HandEditions(hand, inputs.Catalog["editions"] as JsonObject ?? []);
                foreach (var e in unknown)
                {
                    result.Drop("override edition the catalog doesn't cover", $"{id} ({e})");
                }
                if (editions.Count > 0)
                {
                    entry["editions"] = editions;
                }
            }
            entry["from"] = new JsonObject { ["entry"] = "override" };
            result.Addons.Add(entry);
        }

        // 6. Dependencies, as catalog IDs: the .toc's required and optional add-ons that other entries ship.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in result.Addons)
        {
            foreach (var f in Strings(a["folders"]))
            {
                owners.TryAdd(f, a["id"]!.GetValue<string>());
            }
        }
        var unresolved = 0;
        foreach (var (entry, (required, optional)) in deps)
        {
            var id = entry["id"]!.GetValue<string>();
            var own = Strings(entry["folders"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wanted = required.Where(f => !own.Contains(f) && !f.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)).ToList();
            unresolved += wanted.Count(f => !owners.ContainsKey(f));
            List<string> Resolve(IEnumerable<string> folders) => folders.Where(f => !own.Contains(f) && owners.ContainsKey(f))
                .Select(f => owners[f]).Where(o => o != id).Distinct().Order(StringComparer.Ordinal).ToList();
            if (entry["requires"] is null && Resolve(wanted) is { Count: > 0 } r)
            {
                entry["requires"] = Array(r);
            }
            // Required add-ons no entry ships, by folder, so apps can say what's missing (DBM-Core for the DBM packs).
            if (entry["requiresFolders"] is null && wanted.Where(f => !owners.ContainsKey(f) && PlainFolder().IsMatch(f))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } outside)
            {
                entry["requiresFolders"] = Array(outside);
            }
            if (entry["optional"] is null && Resolve(optional).Except(Strings(entry["requires"])).ToList() is { Count: > 0 } opt)
            {
                entry["optional"] = Array(opt);
            }
        }
        if (unresolved > 0)
        {
            result.Notices.Add($"Required add-ons that aren't in the catalog, in \"requiresFolders\": {unresolved}");
        }

        // An override can name an add-on that isn't listed this run: that reference is left out, and reported.
        var listed = result.Addons.Select(a => a["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in result.Addons)
        {
            foreach (var key in new[] { "requires", "optional", "conflicts" })
            {
                var named = Strings(entry[key]);
                if (named.Count == 0 || named.All(listed.Contains))
                {
                    continue;
                }
                foreach (var gone in named.Where(n => !listed.Contains(n)))
                {
                    result.Notices.Add($"{entry["id"]}: \"{key}\" names {gone}, which isn't listed; left out");
                }
                var remaining = named.Where(listed.Contains).ToList();
                if (remaining.Count > 0)
                {
                    entry[key] = Array(remaining);
                }
                else
                {
                    entry.Remove(key);
                }
            }
        }

        // 7. Authors: a curated record where there is one, else one made from the GitHub account.
        foreach (var a in result.Addons)
        {
            var author = a["author"]!.GetValue<string>();
            if (result.Authors.ContainsKey(author))
            {
                continue;
            }
            if (inputs.CuratedAuthors[author] is JsonObject curated)
            {
                result.Authors[author] = curated.DeepClone();
            }
            else if (authors.Record(author) is { } record)
            {
                var (links, refused) = Support.ForAuthor(reposOf.GetValueOrDefault(author) ?? []);
                if (links.Count > 0)
                {
                    record["sponsor"] = Array(links);
                }
                result.Notices.AddRange(refused.Select(r => $"{author}: support link refused, {r}"));
                result.Authors[author] = record;
            }
        }
        result.Notices.AddRange(Support.Changes(inputs.Catalog["authors"] as JsonObject, result.Authors));
        result.Notices.AddRange(SiteLinks.Settle(result.Addons, inputs.Overrides));
        result.Notices.AddRange(Confirmed.Apply(result.Addons, inputs.Confirmed));
        result.Notices.AddRange(Trust.Settle(result.Addons));
        result.Addons.Sort((a, b) => string.CompareOrdinal(a["id"]!.GetValue<string>(), b["id"]!.GetValue<string>()));
        return result;
    }

    /// <summary>Authors made from GitHub accounts: an ID per account, named as the first add-on's .toc names them (one name only).</summary>
    sealed class Authors
    {
        readonly Dictionary<string, (string Owner, string Name)> byId = new(StringComparer.Ordinal);

        public string IdFor(string owner, string? tocAuthor)
        {
            var id = Text.Slug(owner) is { Length: > 0 } a ? a : "author";
            for (var n = 2; byId.TryGetValue(id, out var other) && !other.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase); n++)
            {
                id = $"{Text.Slug(owner)}-{n}";
            }
            if (!byId.ContainsKey(id))
            {
                var name = Text.Clean(tocAuthor, dropVersions: true);
                byId[id] = (owner, name.Length is > 0 and <= 60 && !NotOneName(name) ? name : owner);
            }
            return id;
        }

        public JsonObject? Record(string id) => byId.TryGetValue(id, out var a)
            ? new JsonObject { ["name"] = a.Name, ["profiles"] = Array([$"https://github.com/{a.Owner}"]) }
            : null;
    }

    /// <summary>
    /// Discovery's main .toc, when it belongs to the main folder the release check found. Otherwise it's a bundled
    /// library's or helper's (Stubby in Auctioneer), and its Title, Notes, icon and dependencies would describe the
    /// wrong add-on: none are used, and the entry falls back to the folder name and the repository.
    /// </summary>
    /// <summary>
    /// The main folder's .toc fields: as the release check read them in the release itself when it recorded them (the
    /// source of truth, and the only one when the packager writes the .toc), over discovery's from the repo.
    /// </summary>
    static JsonObject MainToc(Listing l) => MainToc(l.Row, l.Main, l.MainFolder);

    public static JsonObject MainToc(JsonObject row, JsonObject good, string mainFolder)
    {
        var toc = OwnToc(row, mainFolder).DeepClone().AsObject();
        if (good["toc"] is JsonObject released)
        {
            foreach (var (key, value) in released)
            {
                toc[key] = value?.DeepClone();
            }
        }
        return toc;
    }

    public static JsonObject OwnToc(JsonObject row, string mainFolder)
    {
        // Discovery's pick first; then any .toc it read that belongs to the main folder (ForeverScout: discovery picked
        // its guide's .toc, the shallowest).
        var toc = row["toc"] as JsonObject;
        if (toc is not null && Belongs(Str(toc["path"]) ?? "", mainFolder, rootCounts: false))
        {
            return toc;
        }
        if (TocFor(row, mainFolder) is { } named)
        {
            return named;
        }
        // A .toc at the repo's root, with no other named after the folder, is the add-on's own.
        return toc is not null && Belongs(Str(toc["path"]) ?? "", mainFolder, rootCounts: true) ? toc : new JsonObject();
    }

    /// <summary>The .toc discovery read for a folder of the release, with its fields, or null.</summary>
    public static JsonObject? TocFor(JsonObject row, string folder) =>
        (row["tocs"]?.AsArray() ?? []).OfType<JsonObject>()
            .Where(t => t.ContainsKey("title") && Belongs(Str(t["path"]) ?? "", folder, rootCounts: false))
            .OrderBy(t => (Str(t["path"]) ?? "").Count(c => c == '/')).ThenBy(t => (Str(t["path"]) ?? "").Length).FirstOrDefault();

    /// <summary>
    /// Whether a .toc in the repo belongs to a folder of the release. The repo's layout isn't the zip's (src/Thing.toc,
    /// addon/Thing/Thing.toc), so match the .toc's folder or its own name, with or without an edition suffix
    /// (Thing_Forever.toc). A .toc at the root counts for the main folder when discovery picked it.
    /// </summary>
    static bool Belongs(string path, string folder, bool rootCounts)
    {
        var parts = path.Split('/');
        var stem = Path.GetFileNameWithoutExtension(parts[^1]);
        return (rootCounts && parts.Length == 1)
            || (parts.Length > 1 && parts[^2].Equals(folder, StringComparison.OrdinalIgnoreCase))
            || stem.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || (stem.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && stem.Length > folder.Length && stem[folder.Length] is '_' or '-');
    }

    static readonly System.Text.RegularExpressions.Regex LibraryFolder = new(@"^(Lib|Ace|CallbackHandler|Taintless)|[-_ ]\d+\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Folders of a release that are separate add-ons: their own .toc with a Title, a name not built on the main
    /// folder's, not a library or a load-first helper, and no dependency either way with another folder of the release.
    /// (Dominos with Masque_Dominos, RatingBuster with StatLogic, a suite's modules: each depends on another, so they're
    /// one add-on. PartyDiamond with SoftTarget: two add-ons in one zip.) A folder discovery has no .toc for is assumed
    /// to belong.
    /// </summary>
    public static List<string> SeparateAddons(JsonObject row, IReadOnlyList<string> folders, string main)
    {
        var stem = main.TrimStart('!');
        stem = stem.Length >= 3 ? stem[..3] : stem;
        var deps = folders.ToDictionary(f => f, f => TocFor(row, f) is { } t
            ? Strings(t["dependencies"]).Concat(Strings(t["optionalDependencies"])).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        // A Title or Notes that names another folder ties them too (HUI "(ex-ActionBarsEnhanced)" with its
        // ActionBarsEnhanced profile-migration folder).
        var words = folders.ToDictionary(f => f, f => TocFor(row, f) is { } t ? $"{Str(t["title"])} {Str(t["notes"])}" : "", StringComparer.OrdinalIgnoreCase);
        bool Names(string text, string folder) => folder.TrimStart('!').Length >= 4 && text.Contains(folder.TrimStart('!'), StringComparison.OrdinalIgnoreCase);
        return folders.Where(f => !f.Equals(main, StringComparison.OrdinalIgnoreCase) && !f.StartsWith('!')
                && !f.TrimStart('!').StartsWith(stem, StringComparison.OrdinalIgnoreCase) && !LibraryFolder.IsMatch(f)
                && Str(TocFor(row, f)?["title"]) is not null
                && !deps[f].Overlaps(folders.Where(o => !o.Equals(f, StringComparison.OrdinalIgnoreCase)))
                && !folders.Any(o => !o.Equals(f, StringComparison.OrdinalIgnoreCase) && (deps[o].Contains(f) || Names(words[o], f) || Names(words[f], o))))
            .ToList();
    }

    /// <summary>
    /// The repository's name as words, for search (Max-Camera-Distance or MaxCameraDistance gives Max Camera Distance),
    /// unless it's the add-on's name again once case and symbols are ignored. None for a repo that releases several add-ons.
    /// </summary>
    public static string? OtherName(string repo, string? tagPrefix, string? name)
    {
        if (tagPrefix is not null)
        {
            return null;
        }
        // WoW stays one word, not "Wo W".
        var words = repo[(repo.IndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Replace("WoW", " wow ", StringComparison.Ordinal);
        words = Regex.Replace(words, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // "wow-addon-tradewinds" and "olympus-addon" say nothing the name doesn't.
        string[] filler = ["wow", "addon", "addons", "wowaddon"];
        while (tokens.Count > 0 && filler.Contains(tokens[0], StringComparer.OrdinalIgnoreCase)) tokens.RemoveAt(0);
        while (tokens.Count > 0 && filler.Contains(tokens[^1], StringComparer.OrdinalIgnoreCase)) tokens.RemoveAt(tokens.Count - 1);
        words = string.Join(' ', tokens);
        static string Fold(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        // Nothing new for search when the name already holds it.
        return words.Length is 0 or > 100 || Fold(name).Contains(Fold(words), StringComparison.Ordinal) ? null : words;
    }

    /// <summary>
    /// The folders an edition installs when the check left some of the release's out (folders made only for other
    /// editions), else null: then it's all of the entry's folders.
    /// </summary>
    public static List<string>? EditionFolders(JsonNode good)
    {
        var leftOut = Strings(good["leftOut"]);
        return leftOut.Count == 0 ? null : [.. Strings(good["folders"]).Where(f => !leftOut.Contains(f, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>WoW's locales, in the order entries list them.</summary>
    public static readonly string[] Locales = ["enUS", "deDE", "esES", "esMX", "frFR", "itIT", "koKR", "ptBR", "ruRU", "zhCN", "zhTW"];

    /// <summary>
    /// The languages an add-on's text supports: the translation files its release ships and its .toc's translated Notes,
    /// plus English when it has translations and its own name and summary are in Latin script (the base text the
    /// translations are made from). With neither, the script of its name and summary when that isn't Latin: Chinese,
    /// Korean or Cyrillic. Latin script alone says nothing (English and German look alike), so the list is then empty.
    /// </summary>
    public static List<string> Languages(IEnumerable<string> files, IEnumerable<string> translated, string text)
    {
        var found = files.Concat(translated).Select(c => c switch { "enGB" => "enUS", "ptPT" => "ptBR", _ => c })
            .Where(Locales.Contains).ToHashSet(StringComparer.Ordinal);
        var han = text.Any(c => c is >= '\u3400' and <= '\u9fff');
        var hangul = text.Any(c => c is >= '\uac00' and <= '\ud7af');
        var cyrillic = text.Any(c => c is >= '\u0400' and <= '\u04ff');
        var kana = text.Any(c => c is >= '\u3040' and <= '\u30ff');
        var latinOnly = !han && !hangul && !cyrillic && !kana && text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (found.Count > 0)
        {
            if (latinOnly)
            {
                found.Add("enUS");
            }
        }
        else
        {
            if (han && !kana)
            {
                found.UnionWith(["zhCN", "zhTW"]);
            }
            if (hangul)
            {
                found.Add("koKR");
            }
            if (cyrillic)
            {
                found.Add("ruRU");
            }
        }
        return [.. Locales.Where(found.Contains)];
    }

    static JsonObject Entry(Listing l, string id, List<JsonObject> previous, List<string> editionIds, DateOnly today, Authors authors)
    {
        var toc = MainToc(l);
        var from = new JsonObject();
        var entry = new JsonObject { ["id"] = id };

        var name = Text.Untag(Text.Clean(Str(toc["title"]), dropVersions: true));
        from["name"] = name.Length > 0 ? "toc-title" : "folder";
        var shown = name.Length > 0 ? name : l.MainFolder.TrimStart('!');
        entry["name"] = shown.Length > 100 ? shown[..100].TrimEnd() : shown;

        foreach (var (source, value) in new[] { ("toc-notes", Str(toc["notes"])), ("repo-description", Str(l.Row["description"])), ("readme", Str(l.Row["readme"])) })
        {
            if (Text.Summary(value) is { Length: > 0 } summary)
            {
                entry["summary"] = summary;
                from["summary"] = source;
                break;
            }
        }
        if (Str(from["summary"]) == "toc-notes" && toc["localizedNotes"] is JsonObject localized)
        {
            var summaries = new JsonObject();
            foreach (var (locale, text) in localized.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (Regex.IsMatch(locale, "^[a-z]{2}[A-Z]{2}$") && Text.Summary(Str(text)) is { Length: > 0 } s && s != entry["summary"]!.GetValue<string>())
                {
                    summaries[locale] = s;
                }
            }
            if (summaries.Count > 0)
            {
                entry["summaries"] = summaries;
            }
        }

        if (Languages(Strings(l.Main["locales"]), (toc["localizedNotes"] as JsonObject)?.Select(p => p.Key) ?? [],
            $"{Str(entry["name"])} {Str(entry["summary"])}") is { Count: > 0 } languages)
        {
            entry["languages"] = Array(languages);
        }

        entry["author"] = authors.IdFor(l.Owner, Str(toc["author"]));
        from["author"] = "repo-owner";

        var categoryTags = Tags.From(Str(toc["category"]), []);
        var tags = Tags.From(Str(toc["category"]), Strings(l.Row["topics"]));
        if (tags.Count > 0)
        {
            entry["tags"] = Array(tags);
            from["tags"] = categoryTags.Count > 0 ? "toc-category" : "topics";
        }

        var old = previous.FirstOrDefault(p => Str(p["id"]) == id);
        entry["added"] = Str(old?["added"]) ?? today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var github = new JsonObject { ["type"] = "github", ["repo"] = l.Repo };
        if (l.TagPrefix is not null)
        {
            github["tagPrefix"] = l.TagPrefix;
        }
        github["assetPattern"] = @"(?i)\.zip$";
        var sources = new JsonArray(github);
        if (l.CurseForge is { } cf)
        {
            sources.Add(new JsonObject { ["type"] = "curseforge", ["projectId"] = cf });
        }
        entry["sources"] = sources;
        entry["links"] = new JsonObject { ["repository"] = $"https://github.com/{l.Repo}" };
        if (l.Row["issues"]?.GetValue<bool>() == true)
        {
            entry["links"]!["issues"] = $"https://github.com/{l.Repo}/issues";
        }
        SiteLinks.Add(entry["links"]!.AsObject(), from, toc, fork: l.Row["fork"]?.GetValue<bool>() == true);
        entry["folders"] = Array(l.Folders);

        var editions = new JsonObject();
        foreach (var e in editionIds.Where(l.Good.ContainsKey))
        {
            var good = l.Good[e]!;
            editions[e] = new JsonObject { ["interface"] = Str(good["interface"]), ["upToDate"] = good["upToDate"]?.GetValue<bool>() ?? false };
            if (EditionFolders(good) is { } installs)
            {
                editions[e]!["folders"] = Array(installs);
            }
            // The day (UTC) the checked release was published, for sorting by recently updated; left out, never guessed.
            if (Released(Str(good["published"])) is { } released)
            {
                editions[e]!["released"] = released;
            }
        }
        entry["editions"] = editions;
        // GitHub's public download counts for the release files, as discovery read them; left out when it has none.
        // (They're the whole repo's, so an add-on released beside others gets none.)
        if (l.TagPrefix is null && l.Row["downloads"] is JsonObject counts && counts["total"] is not null && counts["read"] is not null)
        {
            var downloads = new JsonObject { ["total"] = counts["total"]!.DeepClone() };
            if (counts["latest"] is { } latest)
            {
                downloads["latest"] = latest.DeepClone();
            }
            downloads["read"] = counts["read"]!.DeepClone();
            entry["downloads"] = downloads;
        }
        entry["trust"] = Trust.Of(l.Row, editionIds.Where(l.Good.ContainsKey).Select(e => l.Good[e]!).ToList(), today);
        entry["from"] = from;
        return entry;
    }

    /// <summary>The UTC day of a release's published time ("2026-11-20T10:00:00Z" gives "2026-11-20"), or null when it has none.</summary>
    public static string? Released(string? published) =>
        DateTimeOffset.TryParse(published, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// An override's hand-checked editions, as the catalog lists a checked release's: in the catalog's edition order, each
    /// with its Interface, upToDate when that is the edition's current Interface, and its folders and release day when
    /// given. Editions the catalog doesn't cover are returned apart, to be reported.
    /// </summary>
    public static (JsonObject Editions, List<string> Unknown) HandEditions(JsonObject hand, JsonObject catalogEditions)
    {
        var editions = new JsonObject();
        foreach (var (e, edition) in catalogEditions)
        {
            if (hand[e] is not JsonObject h)
            {
                continue;
            }
            var listed = new JsonObject { ["interface"] = Str(h["interface"]), ["upToDate"] = Str(h["interface"]) == Str(edition?["interface"]) };
            if (h["folders"] is JsonArray folders)
            {
                listed["folders"] = folders.DeepClone();
            }
            if (Str(h["released"]) is { } released)
            {
                listed["released"] = released;
            }
            editions[e] = listed;
        }
        return (editions, hand.Select(e => e.Key).Where(e => catalogEditions[e] is null).ToList());
    }

    /// <summary>An override's fields replace the generated ones (links key by key); each such value's source is "override".</summary>
    public static void Apply(JsonObject entry, JsonObject o)
    {
        var from = entry["from"] as JsonObject;
        foreach (var (key, value) in o)
        {
            // editions: only an entry made from an override takes them (HandEditions), never a checked release's.
            if (OverrideOnly.Contains(key) || key == "editions")
            {
                continue;
            }
            if (key == "links" && entry["links"] is JsonObject links && value is JsonObject extra)
            {
                foreach (var (k, v) in extra)
                {
                    // null removes a link (a wrong one a player reported); a site link set here is confirmed by a person.
                    if (v is null)
                    {
                        links.Remove(k);
                        from?.Remove(k);
                        continue;
                    }
                    links[k] = v.DeepClone();
                    if (from is not null && SiteLinks.Keys.Contains(k))
                    {
                        from[k] = "override";
                    }
                }
                continue;
            }
            entry[key] = value?.DeepClone();
            if (from is not null && key is "name" or "summary" or "summaries" or "author" or "tags" or "icon")
            {
                from[key] = "override";
            }
        }
    }

    /// <summary>The path of an IconTexture inside one of the add-on's own folders, or null for game art.</summary>
    public static string? IconPath(string? texture, IReadOnlyList<string> folders)
    {
        if (texture is null)
        {
            return null;
        }
        var parts = texture.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("Interface", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("AddOns", StringComparison.OrdinalIgnoreCase)
            || parts.Any(p => p is "." or ".."))
        {
            return null;
        }
        var folder = folders.FirstOrDefault(f => f.Equals(parts[2], StringComparison.OrdinalIgnoreCase));
        return folder is null ? null : string.Join('/', [folder, .. parts[3..]]);
    }

    public static bool NotOneName(string author) => author.IndexOfAny([',', ';', '&', '/']) >= 0
        || author.Contains(" and ", StringComparison.OrdinalIgnoreCase) || author.Contains("contributors", StringComparison.OrdinalIgnoreCase)
        || author.Contains(" others", StringComparison.OrdinalIgnoreCase) || author.Contains("team", StringComparison.OrdinalIgnoreCase);

    /// <summary>Groups of listings that share a CurseForge project or a folder.</summary>
    static List<List<Listing>> Groups(List<Listing> listings)
    {
        var parent = Enumerable.Range(0, listings.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < listings.Count; i++)
        {
            var keys = listings[i].Folders.Select(f => "f:" + f);
            if (listings[i].CurseForge is { } cf)
            {
                keys = keys.Append("c:" + cf);
            }
            foreach (var key in keys)
            {
                if (byKey.TryGetValue(key, out var j))
                {
                    parent[Find(i)] = Find(j);
                }
                else
                {
                    byKey[key] = i;
                }
            }
        }
        return Enumerable.Range(0, listings.Count).GroupBy(Find).Select(g => g.Select(i => listings[i]).ToList()).ToList();
    }

    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    public static JsonObject Ordered(JsonObject entry)
    {
        var ordered = new JsonObject();
        foreach (var key in FieldOrder.Where(entry.ContainsKey).Concat(entry.Select(p => p.Key).Where(k => !FieldOrder.Contains(k))).ToList())
        {
            ordered[key] = entry[key]?.DeepClone();
        }
        return ordered;
    }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Why this run looks broken, or null: it would list fewer than half the add-ons the catalog lists now (a truncated
    /// checks.json, say). Then nothing is written and the last good files stay.
    /// </summary>
    public static string? Shrank(JsonObject previous, Result result)
    {
        var before = previous["addons"]?.AsArray().Count ?? 0;
        return before >= 20 && result.Addons.Count * 2 < before
            ? $"This run lists {result.Addons.Count} add-ons, against {before} in the catalog now: it looks broken, so nothing was written."
            : null;
    }

    /// <summary>
    /// The new catalog.json. Its version goes up a minor version when entries or authors changed, and a major version
    /// when the format did.
    /// </summary>
    public static string Write(Result result, JsonObject previous)
    {
        var authors = new JsonObject(result.Authors.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => KeyValuePair.Create(a.Key, a.Value?.DeepClone())));
        var addons = new JsonArray(result.Addons.Select(a => (JsonNode)Ordered(a)).ToArray());
        var version = Version.Parse(Str(previous["version"]) ?? "0.0.0");
        var formatChanged = previous["schemaVersion"]?.GetValue<int>() != Settings.SchemaVersion;
        var changed = !JsonNode.DeepEquals(previous["addons"], addons) || !JsonNode.DeepEquals(previous["authors"], authors);
        var next = formatChanged ? new Version(version.Major + 1, 0, 0) : changed ? new Version(version.Major, version.Minor + 1, 0) : version;
        var root = new JsonObject
        {
            ["$schema"] = "./schema/catalog.schema.json",
            ["schemaVersion"] = Settings.SchemaVersion,
            ["version"] = next.ToString(3),
            ["editions"] = previous["editions"]!.DeepClone(),
            ["authors"] = authors,
            ["addons"] = addons,
        };
        return root.ToJsonString(Options) + "\n";
    }
}

/// <summary>
/// confirmed.json (schema/confirmed.schema.json): versions of CurseForge and Wago add-ons that players reported through
/// the relay and the maintainer confirmed by merging the reports workflow's pull request. Each goes on its source in
/// catalog.json as "confirmed", by edition, for the editions the entry lists. Nothing else sets it: an override's is
/// dropped.
/// </summary>
static class Confirmed
{
    public static List<string> Apply(List<JsonObject> addons, JsonObject? confirmed)
    {
        var notices = new List<string>();
        var byId = confirmed?["addons"]?.AsObject() ?? new JsonObject();
        var used = new HashSet<(string, string, string)>();
        foreach (var entry in addons)
        {
            var id = entry["id"]!.GetValue<string>();
            var editions = entry["editions"]?.AsObject();
            foreach (var source in entry["sources"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                source.Remove("confirmed");
                var type = Generator.Str(source["type"]);
                if (type is not ("curseforge" or "wago") || byId[id]?[type] is not JsonObject byEdition)
                {
                    continue;
                }
                var listed = new JsonObject();
                foreach (var (edition, value) in byEdition.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (editions?[edition] is null || value is not JsonObject v)
                    {
                        continue;
                    }
                    listed[edition] = new JsonObject { ["version"] = v["version"]?.DeepClone(), ["sha256"] = v["sha256"]?.DeepClone(), ["date"] = v["date"]?.DeepClone() };
                    used.Add((id, type, edition));
                }
                if (listed.Count > 0)
                {
                    source["confirmed"] = listed;
                }
            }
        }
        foreach (var (id, bySource) in byId)
        {
            foreach (var (type, byEdition) in bySource?.AsObject() ?? [])
            {
                foreach (var (edition, _) in byEdition?.AsObject() ?? [])
                {
                    if (!used.Contains((id, type, edition)))
                    {
                        notices.Add($"confirmed.json: {id} {type} {edition} isn't listed in the catalog; left out");
                    }
                }
            }
        }
        return notices;
    }
}

/// <summary>
/// versions.json (schema/versions.schema.json): for each listed add-on released on GitHub, its last good release for each
/// edition the release check passed it for (or found not updated yet), after the check's hold. Keyed by ID, then edition.
/// </summary>
static class Versions
{
    public const int SchemaVersion = 1;

    public static JsonObject Addons(Result result, JsonObject catalog)
    {
        var order = (catalog["editions"] as JsonObject)?.Select(e => e.Key).ToList() ?? [];
        var addons = new JsonObject();
        foreach (var (id, (repo, good)) in result.Good.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Only when the entry's own GitHub source is the repo the release was checked from.
            var entry = result.Addons.FirstOrDefault(a => a["id"]!.GetValue<string>() == id);
            var source = entry?["sources"]?.AsArray().OfType<JsonObject>().FirstOrDefault(s => Generator.Str(s["type"]) == "github");
            if (entry?["linkOnly"] is not null)
            {
                continue; // the app sends players to the author's page: it never downloads the file
            }
            if (source is null || !string.Equals(Generator.Str(source["repo"]), repo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var editions = new JsonObject();
            foreach (var (edition, node) in good.OrderBy(g => order.IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                if (node is not JsonObject g || Generator.Str(g["file"]) is not { } file || Generator.Str(g["sha256"]) is not { } sha || Generator.Str(g["url"]) is not { } url)
                {
                    continue;
                }
                var release = new JsonObject
                {
                    ["release"] = Generator.Str(g["release"]),
                    ["file"] = file,
                    ["url"] = url,
                };
                if (g["size"]?.GetValue<long>() is { } size)
                {
                    release["size"] = size;
                }
                release["sha256"] = sha;
                release["interface"] = Generator.Str(g["interface"]);
                release["upToDate"] = g["upToDate"]?.GetValue<bool>() ?? false;
                if (Generator.EditionFolders(g) is { } installs)
                {
                    release["folders"] = Generator.Array(installs);
                }
                release["published"] = (Generator.Str(g["published"]) ?? "")[..Math.Min(10, (Generator.Str(g["published"]) ?? "").Length)];
                editions[edition] = release;
            }
            if (editions.Count > 0)
            {
                addons[id] = editions;
            }
        }
        return addons;
    }

    /// <summary>The new versions.json, or null when its add-ons are the same as the previous file's (then it isn't rewritten).</summary>
    public static string? Write(Result result, JsonObject catalog, string? previous, DateTime now)
    {
        var addons = Addons(result, catalog);
        JsonNode? before = null;
        try
        {
            before = previous is null ? null : JsonNode.Parse(previous);
        }
        catch (JsonException)
        {
        }
        if (before?["schemaVersion"]?.GetValue<int>() == SchemaVersion && JsonNode.DeepEquals(before["addons"], addons))
        {
            return null;
        }
        var root = new JsonObject
        {
            ["$schema"] = "./schema/versions.schema.json",
            ["schemaVersion"] = SchemaVersion,
            ["checked"] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["addons"] = addons,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}

/// <summary>
/// Icons from the add-on's own files: the main .toc's IconTexture when it points into one of the add-on's folders (never
/// game art), read from the checked release file, converted to PNG and kept in icons/ as &lt;id&gt;.png.
/// </summary>
static class Icons
{
    const int MaxSide = 512;
    static readonly string[] Extensions = ["", ".blp", ".tga", ".png"];

    public static async Task<byte[]?> DownloadAsync(HttpClient http, string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            return await http.GetByteArrayAsync(url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    static JsonObject ReadCache(string dir)
    {
        var path = Path.Combine(dir, "icons.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!["icons"]!.AsObject() : new JsonObject();
    }

    static void Set(JsonObject entry, string id)
    {
        entry["icon"] = Settings.IconBase + id + ".png";
        if (entry["from"] is JsonObject from)
        {
            from["icon"] = "toc-icon";
        }
    }

    /// <summary>Fetches the icons that changed, keeps the rest, and removes icons no entry uses any more.</summary>
    public static async Task UpdateAsync(Result result, Inputs inputs, string dir, Func<string, Task<byte[]?>> download)
    {
        Directory.CreateDirectory(dir);
        var cache = ReadCache(dir);
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var zips = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var (entry, url, path) in result.IconJobs)
        {
            var id = entry["id"]!.GetValue<string>();
            var source = url + "#" + path;
            var file = Path.Combine(dir, id + ".png");
            var cached = cache[id]?.GetValue<string>();
            if (cached == source && File.Exists(file))
            {
                Set(entry, id);
                kept[id] = source;
                continue;
            }
            if (!zips.TryGetValue(url, out var bytes))
            {
                zips[url] = bytes = await download(url);
            }
            var png = bytes is null ? null : Extract(bytes, path);
            if (png is null)
            {
                if (bytes is null && cached is not null && File.Exists(file))
                {
                    Set(entry, id); // the download failed this time: keep the icon it had
                    kept[id] = cached;
                }
                else
                {
                    unreadable.Add(id);
                }
                continue;
            }
            File.WriteAllBytes(file, png);
            Set(entry, id);
            kept[id] = source;
        }
        foreach (var file in Directory.GetFiles(dir, "*.png"))
        {
            if (!kept.ContainsKey(Path.GetFileNameWithoutExtension(file)))
            {
                File.Delete(file);
            }
        }
        WriteCache(dir, kept);
        if (unreadable.Count > 0)
        {
            result.Notices.Add($"Icons that couldn't be read (left out): {unreadable.Count} ({string.Join(", ", unreadable.Take(10))})");
        }
    }

    /// <summary>With --no-icons: entries keep the icons already in icons/.</summary>
    public static void Keep(Result result, Inputs inputs, string dir)
    {
        var cache = ReadCache(dir);
        foreach (var (entry, url, path) in result.IconJobs)
        {
            var id = entry["id"]!.GetValue<string>();
            if (cache[id] is not null && File.Exists(Path.Combine(dir, id + ".png")))
            {
                Set(entry, id);
            }
        }
    }

    static void WriteCache(string dir, SortedDictionary<string, string> icons)
    {
        var root = new JsonObject
        {
            ["$comment"] = "Written by scripts/generate.cs: where each icon came from (release file#path), so it's fetched again only when that changes.",
            ["icons"] = new JsonObject(icons.Select(i => KeyValuePair.Create(i.Key, (JsonNode?)i.Value))),
        };
        File.WriteAllText(Path.Combine(dir, "icons.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n" }) + "\n");
    }

    /// <summary>The image at <paramref name="path"/> in the zip (WoW leaves the extension out), as PNG; null when it can't be read.</summary>
    public static byte[]? Extract(byte[] zipBytes, string path)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
            foreach (var ext in Extensions)
            {
                var wanted = path + ext;
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length > 16 << 20)
                {
                    continue;
                }
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return ToPng(buffer.ToArray());
            }
        }
        catch (InvalidDataException)
        {
        }
        return null;
    }

    /// <summary>A PNG of a PNG, BLP2 or TGA image, or null when it isn't one of those or is too large.</summary>
    public static byte[]? ToPng(byte[] data)
    {
        try
        {
            if (data.Length > 24 && data.AsSpan(0, 8).SequenceEqual(PngSignature))
            {
                var w = BigEndian(data, 16);
                var h = BigEndian(data, 20);
                return w is > 0 and <= MaxSide && h is > 0 and <= MaxSide ? data : null;
            }
            var image = data.Length > 4 && data[0] == 'B' && data[1] == 'L' && data[2] == 'P' && data[3] == '2' ? Blp(data) : Tga(data);
            return image is { } i && i.Width <= MaxSide && i.Height <= MaxSide ? EncodePng(i.Width, i.Height, i.Rgba) : null;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    static int BigEndian(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
    static int U16(byte[] d, int at) => d[at] | (d[at + 1] << 8);
    static int U32(byte[] d, int at) => d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24);

    /// <summary>TGA: true colour or greyscale, 8, 24 or 32 bits, plain or run-length encoded.</summary>
    public static (int Width, int Height, byte[] Rgba)? Tga(byte[] d)
    {
        int idLength = d[0], colorMap = d[1], type = d[2];
        int w = U16(d, 12), h = U16(d, 14), bpp = d[16], descriptor = d[17];
        if (colorMap != 0 || type is not (2 or 3 or 10 or 11) || w is 0 or > MaxSide || h is 0 or > MaxSide)
        {
            return null;
        }
        var grey = type is 3 or 11;
        var bytes = bpp / 8;
        if ((grey && bytes != 1) || (!grey && bytes is not (3 or 4)))
        {
            return null;
        }
        var pixels = new byte[w * h * bytes];
        var at = 18 + idLength;
        if (type is 2 or 3)
        {
            System.Array.Copy(d, at, pixels, 0, pixels.Length);
        }
        else
        {
            for (var p = 0; p < pixels.Length;)
            {
                var header = d[at++];
                var count = (header & 0x7f) + 1;
                if ((header & 0x80) != 0)
                {
                    for (var i = 0; i < count; i++, p += bytes)
                    {
                        System.Array.Copy(d, at, pixels, p, bytes);
                    }
                    at += bytes;
                }
                else
                {
                    System.Array.Copy(d, at, pixels, p, count * bytes);
                    at += count * bytes;
                    p += count * bytes;
                }
            }
        }
        var rgba = new byte[w * h * 4];
        var topDown = (descriptor & 0x20) != 0;
        for (var y = 0; y < h; y++)
        {
            var row = topDown ? y : h - 1 - y;
            for (var x = 0; x < w; x++)
            {
                var s = (y * w + x) * bytes;
                var t = (row * w + x) * 4;
                if (grey)
                {
                    rgba[t] = rgba[t + 1] = rgba[t + 2] = pixels[s];
                    rgba[t + 3] = 255;
                }
                else
                {
                    rgba[t] = pixels[s + 2];
                    rgba[t + 1] = pixels[s + 1];
                    rgba[t + 2] = pixels[s];
                    rgba[t + 3] = bytes == 4 ? pixels[s + 3] : (byte)255;
                }
            }
        }
        return (w, h, rgba);
    }

    /// <summary>BLP2, the game's texture format: palettized, DXT1/3/5 or plain BGRA (not its JPEG kind).</summary>
    public static (int Width, int Height, byte[] Rgba)? Blp(byte[] d)
    {
        int compression = d[8], alphaDepth = d[9], alphaType = d[10];
        int w = U32(d, 12), h = U32(d, 16), offset = U32(d, 20);
        if (U32(d, 4) != 1 || w is <= 0 or > MaxSide || h is <= 0 or > MaxSide)
        {
            return null;
        }
        var rgba = new byte[w * h * 4];
        switch (compression)
        {
            case 1:
                for (var i = 0; i < w * h; i++)
                {
                    var c = 148 + d[offset + i] * 4;
                    rgba[i * 4] = d[c + 2];
                    rgba[i * 4 + 1] = d[c + 1];
                    rgba[i * 4 + 2] = d[c];
                    var a = offset + w * h;
                    rgba[i * 4 + 3] = alphaDepth switch
                    {
                        0 => 255,
                        1 => (byte)(((d[a + i / 8] >> (i % 8)) & 1) * 255),
                        4 => (byte)(((d[a + i / 2] >> (i % 2 * 4)) & 0xf) * 17),
                        8 => d[a + i],
                        _ => throw new ArgumentException("alpha depth"),
                    };
                }
                return (w, h, rgba);
            case 2:
                var blockBytes = alphaType == 0 || alphaDepth <= 1 ? 8 : 16;
                var at = offset;
                for (var by = 0; by < (h + 3) / 4; by++)
                {
                    for (var bx = 0; bx < (w + 3) / 4; bx++, at += blockBytes)
                    {
                        DecodeBlock(d, at, blockBytes == 8 ? -1 : alphaType == 1 ? 3 : 5, alphaDepth == 1, rgba, w, h, bx * 4, by * 4);
                    }
                }
                return (w, h, rgba);
            case 3:
                for (var i = 0; i < w * h; i++)
                {
                    var s = offset + i * 4;
                    rgba[i * 4] = d[s + 2];
                    rgba[i * 4 + 1] = d[s + 1];
                    rgba[i * 4 + 2] = d[s];
                    rgba[i * 4 + 3] = d[s + 3];
                }
                return (w, h, rgba);
            default:
                return null;
        }
    }

    /// <summary>One 4x4 DXT block: <paramref name="dxt"/> is -1 for DXT1 (colour only), 3 or 5.</summary>
    static void DecodeBlock(byte[] d, int at, int dxt, bool dxt1Alpha, byte[] rgba, int w, int h, int x0, int y0)
    {
        var alpha = new byte[16];
        var colourAt = at;
        if (dxt == 3)
        {
            for (var i = 0; i < 16; i++)
            {
                alpha[i] = (byte)(((d[at + i / 2] >> (i % 2 * 4)) & 0xf) * 17);
            }
            colourAt += 8;
        }
        else if (dxt == 5)
        {
            int a0 = d[at], a1 = d[at + 1];
            var table = new int[8];
            table[0] = a0;
            table[1] = a1;
            for (var i = 2; i < 8; i++)
            {
                table[i] = a0 > a1 ? ((8 - i) * a0 + (i - 1) * a1) / 7 : i < 6 ? ((6 - i) * a0 + (i - 1) * a1) / 5 : i == 6 ? 0 : 255;
            }
            ulong bits = 0;
            for (var i = 0; i < 6; i++)
            {
                bits |= (ulong)d[at + 2 + i] << (8 * i);
            }
            for (var i = 0; i < 16; i++)
            {
                alpha[i] = (byte)table[(int)((bits >> (3 * i)) & 7)];
            }
            colourAt += 8;
        }
        else
        {
            System.Array.Fill(alpha, (byte)255);
        }
        int c0 = U16(d, colourAt), c1 = U16(d, colourAt + 2);
        var colours = new (int R, int G, int B, int A)[4];
        colours[0] = Rgb565(c0);
        colours[1] = Rgb565(c1);
        if (c0 > c1 || dxt != -1)
        {
            colours[2] = Mix(colours[0], colours[1], 2, 1, 3);
            colours[3] = Mix(colours[0], colours[1], 1, 2, 3);
        }
        else
        {
            colours[2] = Mix(colours[0], colours[1], 1, 1, 2);
            colours[3] = (0, 0, 0, dxt1Alpha ? 0 : 255);
        }
        var indices = U32(d, colourAt + 4);
        for (var i = 0; i < 16; i++)
        {
            int x = x0 + i % 4, y = y0 + i / 4;
            if (x >= w || y >= h)
            {
                continue;
            }
            var c = colours[(indices >> (2 * i)) & 3];
            var t = (y * w + x) * 4;
            rgba[t] = (byte)c.R;
            rgba[t + 1] = (byte)c.G;
            rgba[t + 2] = (byte)c.B;
            rgba[t + 3] = (byte)(dxt == -1 ? c.A : alpha[i]);
        }

        static (int, int, int, int) Rgb565(int c) => (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31, 255);
        static (int, int, int, int) Mix((int R, int G, int B, int A) a, (int R, int G, int B, int A) b, int wa, int wb, int n) =>
            ((a.R * wa + b.R * wb) / n, (a.G * wa + b.G * wb) / n, (a.B * wa + b.B * wb) / n, 255);
    }

    static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>An 8-bit RGBA PNG.</summary>
    public static byte[] EncodePng(int w, int h, byte[] rgba)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * w * 4, w * 4);
            }
        }
        var png = new MemoryStream();
        png.Write(PngSignature);
        var header = new byte[13];
        header[0] = (byte)(w >> 24); header[1] = (byte)(w >> 16); header[2] = (byte)(w >> 8); header[3] = (byte)w;
        header[4] = (byte)(h >> 24); header[5] = (byte)(h >> 16); header[6] = (byte)(h >> 8); header[7] = (byte)h;
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length];
        s.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xffffffffu, typeBytes), data) ^ 0xffffffffu;
        s.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        }
        return c;
    }).ToArray();

    static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        }
        return crc;
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

    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    static string? Str(JsonNode? n) => n?.GetValue<string>();
    static List<string> List(JsonNode? n) => Generator.Strings(n);

    static JsonObject Row(string repo, string title, string? notes, string? description = null, string? readme = null, string? author = null,
        string? category = null, string? curseforge = null, string[]? deps = null, string[]? optional = null, string? icon = null, string[]? topics = null) => new()
    {
        ["repo"] = repo,
        ["description"] = description,
        ["readme"] = readme,
        ["topics"] = Generator.Array(topics ?? []),
        ["toc"] = new JsonObject
        {
            ["title"] = title, ["notes"] = notes, ["author"] = author, ["category"] = category, ["curseforge"] = curseforge, ["iconTexture"] = icon,
            ["localizedNotes"] = notes is null ? null : new JsonObject { ["deDE"] = "Auf Deutsch", ["xx"] = "bad locale" },
            ["dependencies"] = Generator.Array(deps ?? []), ["optionalDependencies"] = Generator.Array(optional ?? []),
        },
    };

    static JsonObject Check_(string repo, string[] folders, params string[] editions)
    {
        var good = new JsonObject();
        foreach (var e in editions)
        {
            good[e] = new JsonObject
            {
                ["release"] = "v1", ["published"] = "2026-11-20T10:00:00Z", ["upToDate"] = true, ["interface"] = e == "forever" ? "16001" : "11509",
                ["url"] = $"https://github.com/{repo}/releases/download/v1/x.zip", ["folders"] = Generator.Array(folders), ["main"] = folders[0],
                ["file"] = "x.zip", ["size"] = 1234L, ["sha256"] = new string('a', 64),
            };
        }
        return new JsonObject { ["repo"] = repo, ["state"] = "checked", ["good"] = good };
    }

    static JsonObject Prefixed(JsonObject check, string prefix)
    {
        check["tagPrefix"] = prefix;
        return check;
    }

    static byte[] Zip(params (string Path, byte[] Bytes)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    /// <summary>A 2x2 32-bit TGA: red, green (top row) and blue, white (bottom row), stored bottom-up.</summary>
    static byte[] Tga()
    {
        var d = new byte[18 + 16];
        d[2] = 2; d[12] = 2; d[14] = 2; d[16] = 32; d[17] = 8;
        byte[] pixels = [255, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255]; // BGRA: blue, white, red, green
        pixels.CopyTo(d, 18);
        return d;
    }

    public static async Task<int> Run()
    {
        // Confirmed versions from confirmed.json, on their sources.
        var hash = new string('a', 64);
        var withSources = Obj("""{"id":"q","editions":{"forever":{}},"sources":[{"type":"github","repo":"o/q"},{"type":"curseforge","projectId":1,"confirmed":{"forever":{"version":"old","sha256":"HASH","date":"2026-01-01"}}},{"type":"wago","slug":"q"}]}""".Replace("HASH", hash));
        var confirmedNotices = Confirmed.Apply([withSources], Obj("""{"addons":{"q":{"curseforge":{"forever":{"version":"1.2","sha256":"HASH","date":"2026-11-20","reports":3},"retail":{"version":"1.2","sha256":"HASH","date":"2026-11-20","reports":1}}},"gone":{"wago":{"forever":{"version":"1","sha256":"HASH","date":"2026-11-20","reports":1}}}}}""".Replace("HASH", hash)));
        var cf = withSources["sources"]![1]!;
        Check("confirmed: on its source, for the editions the entry lists, without the report count", Str(cf["confirmed"]!["forever"]!["version"]) == "1.2"
            && cf["confirmed"]!["forever"]!["reports"] is null && cf["confirmed"]!["retail"] is null && withSources["sources"]![2]!["confirmed"] is null);
        Check("confirmed: one for an entry or edition that isn't listed is a notice", confirmedNotices.Count == 2
            && confirmedNotices.Any(n => n.Contains("gone wago forever", StringComparison.Ordinal)) && confirmedNotices.Any(n => n.Contains("q curseforge retail", StringComparison.Ordinal)));
        Confirmed.Apply([withSources], null);
        Check("confirmed: only confirmed.json sets it", withSources["sources"]![1]!["confirmed"] is null);

        // Cleaning the author's text.
        Check("clean: colour codes in capitals, and a leading !", Text.Clean("|CFF0294FFm|CFFBD26E5Media|r", dropVersions: true) == "mMedia"
            && Text.Clean("!!!LuaWorkbench", dropVersions: true) == "LuaWorkbench" && Text.Clean("Ding!", dropVersions: true) == "Ding!");
        Check("own toc: the main folder's", Generator.OwnToc(Obj("""{"toc":{"path":"Auctioneer/Auctioneer.toc","title":"A"}}"""), "Auctioneer")["title"] is not null
            && Generator.OwnToc(Obj("""{"toc":{"path":"Thing.toc","title":"T"}}"""), "Thing")["title"] is not null
            && Generator.OwnToc(Obj("""{"toc":{"path":"src/Thing.toc","title":"T"}}"""), "Thing")["title"] is not null
            && Generator.OwnToc(Obj("""{"toc":{"path":"addon/Thing/Thing_Forever.toc","title":"T"}}"""), "Thing")["title"] is not null
            && Generator.OwnToc(Obj("""{"toc":{"path":"Everbuff/EverbuffJournal.toc","title":"T"}}"""), "EverbuffJournal")["title"] is not null);
        Check("own toc: a library's isn't used", Generator.OwnToc(Obj("""{"toc":{"path":"Stubby/Stubby.toc","title":"Stubby"}}"""), "Auctioneer").Count == 0
            && Generator.OwnToc(Obj("""{"toc":{"path":"Thingamajig.toc","title":"T"}}"""), "Thing").Count > 0
            && Generator.OwnToc(Obj("""{"toc":{"path":"x/Thingamajig.toc","title":"T"}}"""), "Thing").Count == 0);
        var scout = Obj("""{"toc":{"path":"Guide.toc","title":"Scout Guide"},"tocs":[{"path":"Guide.toc","title":"Scout Guide"},{"path":"ForeverScout/ForeverScout.toc","title":"Forever Scout","notes":"Scouts."}]}""");
        Check("own toc: found among the other .toc files when discovery picked another folder's", Str(Generator.OwnToc(scout, "ForeverScout")["title"]) == "Forever Scout"
            && Str(Generator.OwnToc(scout, "Guide")["title"]) == "Scout Guide");
        JsonObject Repo(params (string Path, string Title, string[] Deps, string[] Optional)[] tocs) => new()
        {
            ["tocs"] = new JsonArray(tocs.Select(x => (JsonNode)new JsonObject { ["path"] = x.Path, ["title"] = x.Title, ["dependencies"] = Generator.Array(x.Deps), ["optionalDependencies"] = Generator.Array(x.Optional) }).ToArray()),
        };
        Check("separate: two unrelated add-ons in one release", Generator.SeparateAddons(Repo(("PartyDiamond/PartyDiamond.toc", "Party Diamond", [], []), ("SoftTarget/SoftTarget.toc", "SoftTarget", [], [])),
            ["PartyDiamond", "SoftTarget"], "PartyDiamond").SequenceEqual(["SoftTarget"]));
        Check("separate: a plugin depending on the main folder belongs", Generator.SeparateAddons(Repo(("Dominos/Dominos.toc", "Dominos", [], []), ("Masque_Dominos/Masque_Dominos.toc", "Masque Dominos", ["Dominos", "Masque"], [])),
            ["Dominos", "Masque_Dominos"], "Dominos").Count == 0);
        Check("separate: a library the main folder depends on belongs", Generator.SeparateAddons(Repo(("RatingBuster/RatingBuster.toc", "RatingBuster", ["StatLogic"], []), ("StatLogic/StatLogic.toc", "StatLogic", [], [])),
            ["RatingBuster", "StatLogic"], "RatingBuster").Count == 0);
        Check("separate: modules named after the main folder, Lib folders, ! helpers and folders with no .toc belong", Generator.SeparateAddons(Repo(("Thing/Thing.toc", "Thing", [], []), ("Thing_Config/Thing_Config.toc", "Thing Config", [], []),
            ("LibFoo/LibFoo.toc", "LibFoo", [], []), ("!ThingLoad/!ThingLoad.toc", "Loader", [], [])), ["Thing", "Thing_Config", "LibFoo", "!ThingLoad", "Unknown"], "Thing").Count == 0);
        Check("separate: an optional dependency counts as a tie", Generator.SeparateAddons(Repo(("Keel/Keel.toc", "Keel", [], ["RenderTuner"]), ("RenderTuner/RenderTuner.toc", "RenderTuner", [], [])),
            ["Keel", "RenderTuner"], "Keel").Count == 0);
        Check("separate: a Title naming another folder ties them", Generator.SeparateAddons(Repo(("HUI.toc", "HUI (ex-ActionBarsEnhanced)", [], []), ("ActionBarsEnhanced/ActionBarsEnhanced.toc", "Profiles migration", [], [])),
            ["ActionBarsEnhanced", "HUI"], "ActionBarsEnhanced").Count == 0);
        var released = Generator.MainToc(scout, Obj("""{"toc":{"title":"Forever Scout from the zip","author":"Zip"}}"""), "ForeverScout");
        Check("main toc: the release's own .toc wins over the repo's", Str(released["title"]) == "Forever Scout from the zip" && Str(released["notes"]) == "Scouts."
            && Str(Generator.MainToc(scout, Obj("{}"), "ForeverScout")["title"]) == "Forever Scout");
        Check("clean: packager placeholders go", Text.Clean("Carbonite Maps @project-version@", dropVersions: true) == "Carbonite Maps");
        Check("clean: a library's version in its name stays", Text.Clean("AceGUI-3.0: SFX Widgets", dropVersions: true) == "AceGUI-3.0: SFX Widgets"
            && Text.Clean("Thing - 1.2.3", dropVersions: true) == "Thing");
        JsonObject Site(string id, string? cf, string? wago, string? wowi, bool fork = false)
        {
            var e = new JsonObject { ["id"] = id, ["links"] = new JsonObject { ["repository"] = $"https://github.com/x/{id}" }, ["from"] = new JsonObject() };
            SiteLinks.Add(e["links"]!.AsObject(), e["from"]!.AsObject(), new JsonObject { ["curseforge"] = cf, ["wago"] = wago, ["wowi"] = wowi }, fork);
            return e;
        }
        var s1 = Site("one", "913973", "b6XV0OGp", "26644");
        Check("site links: from the .toc's IDs, stated by the author", Str(s1["links"]!["curseforge"]) == "https://www.curseforge.com/projects/913973"
            && Str(s1["links"]!["wago"]) == "https://addons.wago.io/addons/b6XV0OGp" && Str(s1["links"]!["wowinterface"]) == "https://www.wowinterface.com/downloads/info26644"
            && Str(s1["from"]!["wago"]) == "toc");
        Check("site links: malformed IDs and forks give none", Site("bad", "12a", "x y", "-1")["links"]!.AsObject().Count == 1 && Site("fork", "1", "abcdefgh", "2", fork: true)["links"]!.AsObject().Count == 1);
        var twin = Site("twin", null, "b6XV0OGp", null);
        var lone = Site("lone", "777", null, null);
        var notices = SiteLinks.Settle([s1, twin, lone]);
        Check("site links: an ID two add-ons claim goes to neither", s1["links"]!["wago"] is null && twin["links"]!["wago"] is null && s1["from"]!["wago"] is null
            && s1["links"]!["curseforge"] is not null && notices.Any(n => n.Contains("claimed by one, twin", StringComparison.Ordinal)));
        var confirmed = Site("confirmed", null, null, null);
        Generator.Apply(confirmed, Obj("""{"links":{"wago":"https://addons.wago.io/addons/SAMEone1"}}"""));
        var copy = Site("copy", null, "SAMEone1", null);
        SiteLinks.Settle([confirmed, copy]);
        Check("site links: an override confirms one claim, the copy's is dropped", Str(confirmed["links"]!["wago"]) == "https://addons.wago.io/addons/SAMEone1"
            && Str(confirmed["from"]!["wago"]) == "override" && copy["links"]!["wago"] is null);
        var reported = Site("reported", "5", null, null);
        Generator.Apply(reported, Obj("""{"links":{"curseforge":null}}"""));
        Check("site links: an override removes a wrong one", reported["links"]!["curseforge"] is null && reported["from"]!["curseforge"] is null);
        var handSites = Obj("""{"id":"hand","sources":[{"type":"curseforge","projectId":4242},{"type":"wago","slug":"HandWago1"}],"from":{"entry":"override"}}""");
        var claimer = Site("claimer", "4242", "HandWago1", null);
        var handNotices = SiteLinks.Settle([handSites, claimer], new Dictionary<string, JsonObject> { ["hand"] = Obj("""{"sources":[{"type":"curseforge","projectId":4242},{"type":"wago","slug":"HandWago1"}]}""") });
        Check("site links: from the sources, and a source's page is a claim a .toc can't share", Str(handSites["links"]!["curseforge"]) == "https://www.curseforge.com/projects/4242"
            && Str(handSites["links"]!["wago"]) == "https://addons.wago.io/addons/HandWago1" && Str(handSites["from"]!["curseforge"]) == "source"
            && claimer["links"]!["curseforge"] is null && claimer["links"]!["wago"] is null && handNotices.Count == 2);
        var removed = Obj("""{"id":"removed","sources":[{"type":"curseforge","projectId":5151}],"links":{"curseforge":"https://www.curseforge.com/projects/9"},"from":{"curseforge":"override"}}""");
        SiteLinks.Settle([removed], new Dictionary<string, JsonObject> { ["removed"] = Obj("""{"sources":[{"type":"curseforge","projectId":5151}],"links":{"curseforge":"https://www.curseforge.com/projects/9"}}""") });
        Check("site links: an override's link wins over a source's", Str(removed["links"]!["curseforge"]) == "https://www.curseforge.com/projects/9");
        var tocToo = Site("toctoo", "777", null, null);
        tocToo["sources"] = new JsonArray(Obj("""{"type":"curseforge","projectId":778}"""));
        SiteLinks.Settle([tocToo]);
        Check("site links: a source the generator took from a .toc confirms nothing", Str(tocToo["links"]!["curseforge"]) == "https://www.curseforge.com/projects/777" && Str(tocToo["from"]!["curseforge"]) == "toc");
        SiteLinks.Settle([tocToo], new Dictionary<string, JsonObject> { ["toctoo"] = Obj("""{"repo":"x/toctoo","sources":[{"type":"curseforge","projectId":778}]}""") });
        Check("site links: an override's source in place of a different .toc ID", Str(tocToo["links"]!["curseforge"]) == "https://www.curseforge.com/projects/778" && Str(tocToo["from"]!["curseforge"]) == "source");
        Check("clean: colours and version", Text.Clean("|cff00ff00Questie|r v12.1.1", dropVersions: true) == "Questie");
        Check("untag: a leading <tag> as words, a repeated one once, square brackets kept", Text.Untag("<DBM Core> Main Core") == "DBM Core Main Core"
            && Text.Untag("<Camera> Max Distance") == "Camera Max Distance" && Text.Untag("<BGNext> BGNext 金团表格") == "BGNext 金团表格"
            && Text.Untag("<BG> BGNext") == "BG BGNext" && Text.Untag("<Solo>") == "Solo" && Text.Untag("Titan Panel [Spacer]") == "Titan Panel [Spacer]"
            && Text.Untag("A <b> c") == "A <b> c");
        Check("clean: textures and conditions", Text.Clean("|TInterface\\Icons\\X:16|t Foo [AllowLoadGameType classic]", dropVersions: true) == "Foo");
        Check("clean: version in brackets", Text.Clean("Bar (1.2.3)", dropVersions: true) == "Bar" && Text.Clean("Atlas 2 Loot", dropVersions: true) == "Atlas 2 Loot");
        var long_ = Text.Summary(string.Join(" ", Enumerable.Repeat("word", 60)));
        Check("summary: cut at a word", long_.Length <= Settings.SummaryLength && long_.EndsWith("word…", StringComparison.Ordinal));
        Check("slug", Text.Slug("WeakAuras") == "weakauras" && Text.Slug("DBM-Core") == "dbm-core" && Text.Slug("!BugGrabber") == "buggrabber" && Text.Slug("__") == "");
        Check("tags: category", Tags.From("Bags & Inventory", []).SequenceEqual(["bags"]));
        Check("tags: whole words", Tags.From("Guild", ["wow-addon"]).Count == 0 && Tags.From(null, ["quests", "ui"]).SequenceEqual(["quests", "ui"]));
        Check("icon: own folder", Generator.IconPath("Interface\\AddOns\\Foo\\media\\icon", ["Foo"]) == "Foo/media/icon");
        Check("icon: game art and others", Generator.IconPath("Interface\\Icons\\INV_Misc", ["Foo"]) is null && Generator.IconPath("Interface/AddOns/Bar/i", ["Foo"]) is null
            && Generator.IconPath("Interface\\AddOns\\Foo\\..\\x", ["Foo"]) is null);

        // A repo dropped as a re-upload of a hand-listed entry: what ties it to the entry, each signal alone.
        var handListed = Obj("""{"author":"pm","sources":[{"type":"wago","slug":"abc123"},{"type":"curseforge","projectId":77}],"links":{"homepage":"https://github.com/pm/Thing/"}}""");
        var onRecord = Obj("""{"pm":{"name":"PM","profiles":["https://github.com/pm/"]}}""");
        Listing At(string repo, int? cf = null, string? wago = null)
        {
            var row = Row(repo, "Thing", null, curseforge: cf?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (wago is not null)
            {
                row["toc"]!["wago"] = wago;
            }
            return new Listing(repo, row, new JsonObject(), new JsonObject(), ["Thing"], "Thing", cf, "");
        }
        string[] Signals(Listing l, JsonObject authors) => [.. Generator.OfficialSignals(handListed, l, authors).Select(s => s.Text)];
        Check("official source: its .toc names the CurseForge project", Signals(At("z/Other", cf: 77), new JsonObject()).SequenceEqual(["its .toc names the entry's CurseForge project"]));
        Check("official source: its .toc names the Wago project", Signals(At("z/Other", wago: "ABC123"), new JsonObject()).SequenceEqual(["its .toc names the entry's Wago project"]));
        Check("official source: its owner is on the author's record", Signals(At("pm/Elsewhere"), onRecord).SequenceEqual(["its owner is on the author's record"]));
        Check("official source: the entry's own links point to it", Signals(At("pm/Thing"), new JsonObject()).SequenceEqual(["the entry's own links point to it"])
            && Signals(At("pm/Thingy"), new JsonObject()).Length == 0);
        Check("official source: tied both ways", Generator.OfficialSignals(handListed, At("pm/Thing", cf: 77), onRecord) is var both
            && both.Any(s => s.CatalogSide) && both.Any(s => !s.CatalogSide));
        Check("official source: a mirror matching nothing has no signal", Signals(At("z/Mirror", cf: 78, wago: "other"), onRecord).Length == 0);

        // A whole run.
        var previous = Obj("""
            {"schemaVersion":3,"version":"3.4.0","editions":{"retail":{},"classic_era":{},"forever":{}},"authors":{},
             "addons":[{"id":"questie","added":"2026-01-01","sources":[{"type":"github","repo":"Questie/Questie"},{"type":"curseforge","projectId":334372}],"folders":["Questie"]}]}
            """);
        var rows = new[]
        {
            Row("Questie/QuestieRenamed", "Questie", "Shows quests.", curseforge: "334372", author: "Aero, Logon", category: "Quests"),
            Row("a/Thing", "|cff00ff00Thing|r v1.0", "Does the thing.", author: "Ann", category: "Combat", deps: ["LibFoo", "Blizzard_X", "Missing"], optional: ["Questie"],
                icon: "Interface\\AddOns\\Thing\\icon"),
            Row("b/ThingCopy", "Thing", "A copy.", category: "Combat"),
            Row("c/Bundle", "Bundle", "Many things."),
            Row("d/Quiet", "Quiet", null),
            Row("e/LibFoo", "LibFoo", null, description: "A library."),
            Row("f/Readme", "Readme", null, readme: "Read me first."),
            Row("g/Gone", "Gone", "Opted out."),
            Row("i/Bang", "Bang", "Odd folder."),
            Row("j/Grabber", "Grabber", "Loads first."),
            Row("k/CfOnlyMirror", "CF Only", "A re-upload."),
            Row("m/CfOnlyClaim", "CF Only", "Says it's CF Only.", curseforge: "5"),
            Row("l/NoThanks", "No Thanks", "Says no."),
        }.ToDictionary(r => r["repo"]!.GetValue<string>(), r => r, StringComparer.OrdinalIgnoreCase);
        rows["Questie/QuestieRenamed"]["downloads"] = Obj("""{"total":1200,"latest":300,"read":"2026-11-26"}""");
        foreach (var (key, value) in Obj("""{"created":"2019-01-02T00:00:00Z","ownerCreated":"2018-05-06T00:00:00Z","stars":900,"releases":{"count":250,"days":["2026-11-20","2026-11-01"]}}"""))
        {
            rows["Questie/QuestieRenamed"][key] = value?.DeepClone();
        }
        rows["Questie/QuestieRenamed"]["toc"]!["catalog"] = " Yes";
        rows["l/NoThanks"]["toc"]!["catalog"] = "No";
        rows["e/LibFoo"]["topics"] = Generator.Array(["wow-forever"]);
        foreach (var (key, value) in Obj("""{"created":"2026-11-10T00:00:00Z","ownerCreated":"2026-10-01T00:00:00Z","stars":0,"releases":{"count":1,"days":["2026-11-12"]}}"""))
        {
            rows["a/Thing"][key] = value?.DeepClone();
        }
        var checks = new List<JsonObject>
        {
            Check_("Questie/QuestieRenamed", ["Questie", "QuestieDB"], "forever", "classic_era"),
            Check_("a/Thing", ["Thing"], "forever", "classic_era"),
            Check_("b/ThingCopy", ["Thing"], "forever"),
            Check_("c/Bundle", ["Bundle", "One", "Two", "Three", "Four", "Five", "Six"], "forever"),
            Check_("d/Quiet", ["Quiet"], "forever"),
            Check_("e/LibFoo", ["LibFoo"], "forever"),
            Check_("f/Readme", ["Readme"], "forever"),
            Check_("g/Gone", ["Gone"], "forever"),
            Check_("i/Bang", ["Bang."], "forever"),
            Check_("j/Grabber", ["!Grabber"], "forever"),
            Check_("k/CfOnlyMirror", ["CfOnly"], "forever"),
            Check_("m/CfOnlyClaim", ["CfOnlyNew"], "forever"),
            Check_("h/RetailOnly", ["RetailOnly"], "retail"),
            Check_("l/NoThanks", ["NoThanks"], "forever"),
        };
        foreach (var e in new[] { "forever", "classic_era" })
        {
            checks[0]["good"]![e]!["built"] = ".github/workflows/release.yml";
        }
        checks[1]["good"]!["forever"]!["built"] = ".github/workflows/release.yml"; // classic_era's file wasn't
        checks[1]["good"]!["forever"]!["warnings"] = JsonNode.Parse("""[{"sign":"hidden-code","files":["Thing/x.lua"]}]""");
        var overrides = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["questie"] = Obj("""{"repo":"Questie/QuestieRenamed","author":"breakbb","notes":"Three folders.","ownApp":{"name":"Questie Manager","url":"https://example.com/qm","marker":".questie-manager"},"links":{"issues":"https://github.com/Questie/Questie/issues"},"tags":["quests"]}"""),
            ["cfonly"] = Obj("""{"name":"CF Only","summary":"Only on CurseForge.","author":"x","sources":[{"type":"curseforge","projectId":5}],"folders":["CfOnly"],"conflicts":["thing","notlisted"],"editions":{"forever":{"interface":"16001","released":"2026-11-20"},"mars":{"interface":"1"}}}"""),
            ["stale"] = Obj("""{"repo":"z/Stale","notes":"gone"}"""),
            ["grabber"] = Obj("""{"repo":"j/Grabber","linkOnly":{"url":"https://example.com/grabber"}}"""),
        };
        var curated = Obj("""{"breakbb":{"name":"BreakBB"},"x":{"name":"X"},"unused":{"name":"Nobody"}}""");
        var inputs = new Inputs(previous, rows, checks, overrides, curated, ["g/Gone"]);
        var result = Generator.Build(inputs, new DateOnly(2026, 11, 26));
        var byId = result.Addons.ToDictionary(a => a["id"]!.GetValue<string>());
        var qt = byId["questie"]["trust"]!;
        Check("author: a .toc's no keeps it out", !byId.ContainsKey("nothanks")
            && result.Dropped.TryGetValue("the author asked not to be listed (X-Forever-Catalog: no)", out var said) && said.SequenceEqual(["l/NoThanks"]));
        Check("author: the wow-forever topic alone is no marker", byId["libfoo"]["trust"]?["marker"] is null);
        Check("author: link only is listed, never in versions.json", Generator.Str(byId["grabber"]["linkOnly"]!["url"]) == "https://example.com/grabber"
            && byId["grabber"].Select(p => p.Key).SkipWhile(k => k != "notes" && k != "linkOnly").First() == "linkOnly");
        var several = new Inputs(Obj("""{"editions":{"forever":{}},"addons":[]}"""),
            new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase) { ["m/Many"] = Row("m/Many", "Many", "The repo's own notes.", curseforge: "77") },
            [Check_("m/Many", ["Stray"], "forever"), Prefixed(Check_("m/Many", ["One"], "forever"), "one/"), Prefixed(Check_("m/Many", ["Two"], "forever"), "two/"),
                Prefixed(Check_("m/Many", ["Three"], "forever"), "three/")],
            new Dictionary<string, JsonObject>(StringComparer.Ordinal)
            {
                ["many-one"] = Obj("""{"repo":"m/Many","tagPrefix":"one/","name":"One","summary":"The first add-on."}"""),
                ["many-two"] = Obj("""{"repo":"m/Many","tagPrefix":"two/","name":"Two","summary":"The second add-on.","requires":["many-one"]}"""),
            }, Obj("{}"), []);
        var manyResult = Generator.Build(several, new DateOnly(2026, 11, 26));
        var manyIds = manyResult.Addons.Select(a => a["id"]!.GetValue<string>()).ToList();
        var one = manyResult.Addons.FirstOrDefault(a => Str(a["id"]) == "many-one");
        Check("tag prefix: one entry each, from its overrides", manyIds.SequenceEqual(["many-one", "many-two"])
            && List(one?["folders"]).SequenceEqual(["One"]) && Str(one?["name"]) == "One" && one?["tagPrefix"] is null);
        Check("tag prefix: the GitHub source names it, with no CurseForge project or download counts", one?["sources"]?.AsArray().Count == 1
            && Str(one?["sources"]?[0]?["tagPrefix"]) == "one/" && one!["sources"]![0]!.AsObject().Select(p => p.Key).SequenceEqual(["type", "repo", "tagPrefix", "assetPattern"])
            && one?["downloads"] is null);
        Check("tag prefix: the repo itself and an unnamed prefix are left out",
            manyResult.Dropped.TryGetValue("a repo that releases several add-ons: listed by tag prefix only", out var whole) && whole.SequenceEqual(["m/Many"])
            && manyResult.Dropped.TryGetValue("a tag prefix no override names", out var unnamed) && unnamed.SequenceEqual(["m/Many three/"]));
        Check("tag prefix: each in versions.json", Versions.Addons(manyResult, several.Catalog).Select(p => p.Key).SequenceEqual(["many-one", "many-two"]));
        var withParts = several with
        {
            Checks = [Prefixed(Check_("m/Many", ["One"], "forever"), "one/"), Prefixed(Check_("m/Many", ["Two"], "forever"), "two/"),
                Prefixed(Check_("m/Many", ["Pack"], "forever"), "pack/"), Prefixed(Check_("m/Many", ["Extra"], "forever"), "extra/")],
            Overrides = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
            {
                ["many-one"] = Obj("""{"repo":"m/Many","tagPrefix":"one/","name":"One","summary":"The first add-on.","linkOnly":{"url":"https://example.com/one"}}"""),
                ["many-two"] = Obj("""{"repo":"m/Many","tagPrefix":"two/","name":"Two","summary":"The second add-on."}"""),
                ["many-pack"] = Obj("""{"repo":"m/Many","tagPrefix":"pack/","partOf":"many-one"}"""),
                ["many-extra"] = Obj("""{"repo":"m/Many","tagPrefix":"extra/","partOf":"many-two"}"""),
            },
        };
        var partsResult = Generator.Build(withParts, new DateOnly(2026, 11, 26));
        var joined = partsResult.Addons.FirstOrDefault(a => Str(a["id"]) == "many-one");
        Check("part of: its folders join a link-only entry, with no entry of its own",
            partsResult.Addons.Select(a => a["id"]!.GetValue<string>()).SequenceEqual(["many-one", "many-two"]) && List(joined?["folders"]).SequenceEqual(["One", "Pack"])
            && joined?["partOf"] is null && !Versions.Addons(partsResult, withParts.Catalog).ContainsKey("many-pack"));
        Check("part of: never into an entry the app installs", List(partsResult.Addons.First(a => Str(a["id"]) == "many-two")["folders"]).SequenceEqual(["Two"])
            && partsResult.Dropped.TryGetValue("part of an add-on that isn't link only", out var notWhole) && notWhole.SequenceEqual(["many-extra (part of many-two)"]));
        Check("ownApp: from the override, after notes", Generator.Str(byId["questie"]["ownApp"]!["marker"]) == ".questie-manager"
            && byId["questie"].Select(p => p.Key).SkipWhile(k => k != "notes").Skip(1).First() == "ownApp");
        Check("trust: built by its own repo, its history and the author's marker", Generator.Str(qt["built"]) == ".github/workflows/release.yml"
            && Generator.Str(qt["repoSince"]) == "2019-01-02" && Generator.Str(qt["ownerSince"]) == "2018-05-06" && (int)qt["releases"]! == 250 && (int)qt["stars"]! == 900
            && Generator.Str(qt["marker"]) == "yes" && Generator.Strings(qt["badges"]).SequenceEqual(["built"]) && qt["warnings"] is null && !(bool)qt["careful"]!);
        var tt = byId["thing"]["trust"]!;
        Check("trust: a new add-on from a new account with hidden code is handled with care", tt["built"] is null
            && Generator.Strings(tt["badges"]).SequenceEqual(["new"]) && Generator.Strings(tt["warnings"]).SequenceEqual(["hidden-code", "new-owner", "single-release"])
            && (bool)tt["careful"]! && result.Notices.Contains("thing: warning signs: hidden-code, new-owner, single-release"));
        Check("trust: no history read gives no badges or warnings", byId["readme"]["trust"]!.AsObject().Select(p => p.Key).SequenceEqual(["careful"]));
        Check("relations: a reference to an add-on that isn't listed is left out", Generator.Strings(byId["cfonly"]["conflicts"]).SequenceEqual(["thing"])
            && result.Notices.Any(n => n.Contains("names notlisted", StringComparison.Ordinal)));
        Check("override editions: listed with upToDate, the catalog's editions only, the rest reported", byId["cfonly"]["editions"]!.AsObject().Select(e => e.Key).SequenceEqual(["forever"])
            && Str(byId["cfonly"]["editions"]!["forever"]!["interface"]) == "16001" && Str(byId["cfonly"]["editions"]!["forever"]!["released"]) == "2026-11-20"
            && result.Dropped.TryGetValue("override edition the catalog doesn't cover", out var unknownEditions) && unknownEditions.SequenceEqual(["cfonly (mars)"]));
        var (hand, _) = Generator.HandEditions(Obj("""{"retail":{"interface":"110000"},"forever":{"interface":"16001","folders":["CfOnly"]}}"""),
            Obj("""{"retail":{"interface":"120105"},"classic_era":{"interface":"11508"},"forever":{"interface":"16001"}}"""));
        Check("override editions: in the catalog's order, upToDate against each edition's current Interface", hand.Select(e => e.Key).SequenceEqual(["retail", "forever"])
            && !hand["retail"]!["upToDate"]!.GetValue<bool>() && hand["forever"]!["upToDate"]!.GetValue<bool>() && hand["forever"]!["folders"]!.AsArray().Count == 1);
        Check("mirror: a re-upload of a hand-written entry isn't listed", result.Dropped.TryGetValue("a re-upload of an add-on listed from its own site (same folder or CurseForge project as a hand-written entry)", out var mirrors)
            && mirrors.SequenceEqual(["k/CfOnlyMirror (cfonly)"]) && byId["cfonly"]["sources"]!.AsArray().All(s => Generator.Str(s!["type"]) != "github"));
        Check("official source: a repo claiming a hand-listed entry is reported apart, not listed", result.Dropped.TryGetValue(Generator.OfficialSourceReason, out var official)
            && official.SequenceEqual(["m/CfOnlyClaim (cfonly: its .toc names the entry's CurseForge project; the repo's claim only)"])
            && !byId.Values.Any(a => a["sources"]!.AsArray().Any(s => Generator.Str(s!["repo"]) == "m/CfOnlyClaim")));
        var versions = Versions.Addons(result, previous);
        Check("versions: listed GitHub add-ons only, each checked edition", versions.Select(v => v.Key).SequenceEqual(["libfoo", "questie", "readme", "thing"])
            && versions["questie"]!.AsObject().Select(e => e.Key).SequenceEqual(["classic_era", "forever"]));
        var q1 = versions["questie"]!["forever"]!;
        Check("versions: a release's fields", Generator.Str(q1["file"]) == "x.zip" && q1["size"]!.GetValue<long>() == 1234 && Generator.Str(q1["published"]) == "2026-11-20"
            && Generator.Str(q1["interface"]) == "16001" && q1["upToDate"]!.GetValue<bool>());
        var versionsText = Versions.Write(result, previous, null, new DateTime(2026, 11, 26, 4, 0, 0, DateTimeKind.Utc))!;
        Check("versions: written with the run's time", versionsText.Contains("\"checked\": \"2026-11-26T04:00:00Z\"", StringComparison.Ordinal) && versionsText.EndsWith("}\n", StringComparison.Ordinal));
        Check("versions: an unchanged run doesn't rewrite it", Versions.Write(result, previous, versionsText, new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc)) is null);
        Check("versions: a changed release rewrites it", Versions.Write(result, previous, versionsText.Replace("x.zip", "old.zip", StringComparison.Ordinal), DateTime.UtcNow) is not null);
        var many = Obj("""{"addons":[]}""");
        for (var i = 0; i < 40; i++)
        {
            many["addons"]!.AsArray().Add(Obj($$"""{"id":"a{{i}}"}"""));
        }
        Check("guard: a run that loses more than half the add-ons writes nothing", Generator.Shrank(many, result) is not null && Generator.Shrank(previous, result) is null);
        static JsonObject Fund(string repo, string ownerType, string json) => Obj($$"""{"repo":"{{repo}}","ownerType":"{{ownerType}}","funding":{{json}}}""");
        var (good, _) = Support.FromRow(Fund("a/x", "User", """[{"platform":"KO_FI","url":"https://ko-fi.com/a"},{"platform":"GITHUB","url":"https://github.com/a","sponsors":true,"ownerOrMember":true}]"""));
        Check("support: Sponsors first, the rest in order", good.SequenceEqual(["https://github.com/sponsors/a", "https://ko-fi.com/a"]));
        Check("support: a Sponsors login that isn't the owner", Support.FromRow(Fund("a/x", "User", """[{"platform":"GITHUB","url":"https://github.com/b","sponsors":true,"ownerOrMember":false}]""")) is ({ Count: 0 }, [var notOwner]) && notOwner.Contains("isn't the repo's owner"));
        Check("support: no Sponsors listing", Support.FromRow(Fund("a/x", "User", """[{"platform":"GITHUB","url":"https://github.com/a","sponsors":false,"ownerOrMember":true}]""")) is ({ Count: 0 }, [var noListing]) && noListing.Contains("no GitHub Sponsors listing"));
        Check("support: not checked counts as no", Support.FromRow(Fund("a/x", "User", """[{"platform":"GITHUB","url":"https://github.com/a"}]""")).Links.Count == 0);
        Check("support: custom links must be https and not a shortener", Support.FromRow(Fund("a/x", "User", """[{"platform":"CUSTOM","url":"ftp://example.com/give"},{"platform":"CUSTOM","url":"https://bit.ly/x"},{"platform":"CUSTOM","url":"https://example.com/give"}]""")) is (["https://example.com/give"], { Count: 2 }));
        Check("support: a short link is listed as where it leads", Support.FromRow(Fund("a/x", "User", """[{"platform":"CUSTOM","url":"https://bit.ly/x","resolved":"https://ko-fi.com/a"}]""")).Links.SequenceEqual(["https://ko-fi.com/a"])
            && Support.FromRow(Fund("a/x", "User", """[{"platform":"CUSTOM","url":"https://bit.ly/x","resolved":"https://tinyurl.com/y"}]""")).Links.Count == 0);
        Check("support: http and scheme-less links are read as https", Support.Upgrade("http://paypal.me/a") == "https://paypal.me/a"
            && Support.Upgrade("paypal.me/a") == "https://paypal.me/a" && Support.Upgrade("https://x.org") == "https://x.org" && Support.Upgrade("ftp://x") == "ftp://x");
        var (merged, _) = Support.ForAuthor([Fund("a/y", "User", """[{"platform":"KO_FI","url":"https://ko-fi.com/a/"}]"""), Fund("a/x", "User", """[{"platform":"KO_FI","url":"https://ko-fi.com/A"},{"platform":"PATREON","url":"https://patreon.com/a"}]""")]);
        Check("support: an author's links from every repo, once", merged.SequenceEqual(["https://ko-fi.com/A", "https://patreon.com/a"]));
        var changes = Support.Changes(Obj("""{"a":{"name":"A","sponsor":["https://ko-fi.com/a"]},"b":{"name":"B","sponsor":["https://patreon.com/b"]},"c":{"name":"C"}}"""),
            Obj("""{"a":{"name":"A","sponsor":["https://ko-fi.com/a2"]},"b":{"name":"B"},"c":{"name":"C"},"d":{"name":"D","sponsor":["https://patreon.com/d"]}}"""));
        Check("support: changes are reported", changes.Count == 3 && changes.Any(c => c.StartsWith("a: support links changed", StringComparison.Ordinal))
            && changes.Any(c => c.StartsWith("b: support links removed", StringComparison.Ordinal)) && changes.Any(c => c.StartsWith("d: support links added", StringComparison.Ordinal)));
        Check("ids", byId.Keys.Order(StringComparer.Ordinal).SequenceEqual(["cfonly", "grabber", "libfoo", "questie", "readme", "thing"]));
        Check("dropped", result.Dropped.Keys.Any(k => k.StartsWith("a copy", StringComparison.Ordinal)) && result.Dropped.Keys.Any(k => k.StartsWith("a compilation", StringComparison.Ordinal))
            && result.Dropped.Keys.Any(k => k.StartsWith("no summary", StringComparison.Ordinal)) && result.Dropped.ContainsKey("on the never-add list")
            && result.Dropped.Keys.Any(k => k.StartsWith("override for a repo", StringComparison.Ordinal))
            && result.Dropped.TryGetValue("a folder name the catalog can't list", out var odd) && odd.SequenceEqual(["i/Bang (Bang.)"]));
        var q = byId["questie"];
        Check("rename keeps the ID and date", Str(q["added"]) == "2026-01-01" && Str(q["sources"]![0]!["repo"]) == "Questie/QuestieRenamed");
        Check("override merged", Str(q["notes"]) == "Three folders." && Str(q["author"]) == "breakbb" && Str(q["from"]!["author"]) == "override"
            && Str(q["links"]!["issues"]) == "https://github.com/Questie/Questie/issues" && Str(q["links"]!["repository"]) == "https://github.com/Questie/QuestieRenamed");
        Check("editions", q["editions"]!.AsObject().Select(e => e.Key).SequenceEqual(["classic_era", "forever"]) && Str(q["editions"]!["forever"]!["interface"]) == "16001");
        Check("editions: the release's day", Str(q["editions"]!["forever"]!["released"]) == "2026-11-20");
        Check("editions: folders only when some are left out", Generator.EditionFolders(Obj("""{"folders":["DBM-Core","DBM-Midnight","DBM-GUI"],"leftOut":["dbm-midnight"]}"""))!
            .SequenceEqual(["DBM-Core", "DBM-GUI"]) && Generator.EditionFolders(Obj("""{"folders":["Thing"]}""")) is null && q["editions"]!["forever"]!["folders"] is null);
        Check("downloads: GitHub's counts, as discovery read them; none when it read none", q["downloads"] is JsonObject dl && (long)dl["total"]! == 1200
            && (long)dl["latest"]! == 300 && Str(dl["read"]) == "2026-11-26" && byId["thing"]["downloads"] is null);
        Check("released: UTC day, or none", Generator.Released("2026-11-20T23:30:00-02:00") == "2026-11-21" && Generator.Released(null) is null && Generator.Released("soon") is null);
        var t = byId["thing"];
        Check("name cleaned, from the .toc", Str(t["name"]) == "Thing" && Str(t["from"]!["name"]) == "toc-title" && Str(t["summary"]) == "Does the thing."
            && Str(t["from"]!["summary"]) == "toc-notes" && Str(t["summaries"]!["deDE"]) == "Auf Deutsch" && t["summaries"]!["xx"] is null);
        Check("new entry dated today", Str(t["added"]) == "2026-11-26");
        Check("tags", List(t["tags"]).SequenceEqual(["combat"]) && Str(t["from"]!["tags"]) == "toc-category");
        Check("languages: translations, English as their base, and a non-Latin script alone", List(t["languages"]).SequenceEqual(["enUS", "deDE"])
            && Generator.Languages(["deDE", "enGB"], [], "Thing").SequenceEqual(["enUS", "deDE"]) && Generator.Languages(["zhTW"], [], "金团表格").SequenceEqual(["zhTW"])
            && Generator.Languages([], [], "BGNext 金团表格").SequenceEqual(["zhCN", "zhTW"]) && Generator.Languages([], [], "Русские звуки").SequenceEqual(["ruRU"])
            && Generator.Languages([], [], "Plain English").Count == 0 && Generator.Languages([], ["xx", "deDE"], "Thing").SequenceEqual(["enUS", "deDE"])
            && Generator.Languages([], [], "日本語のテキスト").Count == 0);
        Check("dependencies", List(t["requires"]).SequenceEqual(["libfoo"]) && List(t["optional"]).SequenceEqual(["questie"]));
        Check("otherNames: the repository's name as words, unless it's the name again", Generator.OtherName("l/Max-Camera-Distance", null, "<Camera> Max Distance") == "Max Camera Distance"
            && Generator.OtherName("l/MaxCameraDistance", null, "Zoom") == "Max Camera Distance" && Generator.OtherName("l/my_thing", null, "X") == "my thing"
            && Generator.OtherName("l/SoapstoneWoW", null, "Soap") == "Soapstone"
            && Generator.OtherName("l/LibSharedMedia-3.0", null, "LibSharedMedia-3.0") is null && Generator.OtherName("a/Thing", null, "Thing") is null
            && Generator.OtherName("m/Many", "two/", "Two") is null && t["otherNames"] is null
            && Generator.OtherName("t/wow-addon-tradewinds", null, "Tradewinds") is null && Generator.OtherName("t/ToxiUI", null, "ElvUI ToxiUI Forever") is null
            && Generator.OtherName("t/Plater-Nameplates", null, "Plater") == "Plater Nameplates");
        Check("requiresFolders: required folders no entry ships, not Blizzard's", List(t["requiresFolders"]).SequenceEqual(["Missing"])
            && byId["libfoo"]["requiresFolders"] is null);
        Check("summary from the description and README", Str(byId["libfoo"]["from"]!["summary"]) == "repo-description" && Str(byId["readme"]["from"]!["summary"]) == "readme");
        Check("authors", Str(result.Authors["a"]!["name"]) == "Ann" && Str(result.Authors["a"]!["profiles"]![0]) == "https://github.com/a"
            && Str(result.Authors["breakbb"]!["name"]) == "BreakBB" && result.Authors["unused"] is null && result.Authors["questie"] is null);
        Check("complete override", Str(byId["cfonly"]["name"]) == "CF Only" && Str(byId["cfonly"]["from"]!["entry"]) == "override");
        Check("icon job", result.IconJobs.Count == 1 && result.IconJobs[0].Path == "Thing/icon");

        // Versions: the format's change is a major version, a change of entries a minor one, none none.
        var written = Generator.Write(result, previous);
        var first = Obj(written);
        Check("version: new format", Str(first["version"]) == "4.0.0" && first["schemaVersion"]!.GetValue<int>() == 4 && !written.Contains('\r'));
        Check("version: no change", Str(Obj(Generator.Write(Generator.Build(inputs with { Catalog = first }, new DateOnly(2026, 11, 27)), first))["version"]) == "4.0.0");
        var fewer = inputs with { Catalog = first, Overrides = new(overrides.Where(o => o.Key != "cfonly")) };
        Check("version: entries changed", Str(Obj(Generator.Write(Generator.Build(fewer, new DateOnly(2026, 11, 27)), first))["version"]) == "4.1.0");
        Check("stable IDs", Generator.Build(inputs with { Catalog = first }, new DateOnly(2026, 11, 27)).Addons.Select(a => Str(a["id"])).SequenceEqual(byId.Keys.Order(StringComparer.Ordinal)));

        // Images.
        var tga = Icons.Tga(Tga())!.Value;
        Check("tga: bottom-up to top-down", tga.Width == 2 && tga.Rgba.AsSpan(0, 4).SequenceEqual(new byte[] { 255, 0, 0, 255 }) && tga.Rgba.AsSpan(8, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 }));
        byte[] rle = [0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0, 0x81, 1, 2, 3];
        Check("tga: run-length", Icons.Tga(rle)!.Value.Rgba.SequenceEqual(new byte[] { 3, 2, 1, 255, 3, 2, 1, 255 }));
        var blp = new byte[148 + 4];
        "BLP2"u8.CopyTo(blp);
        blp[4] = 1; blp[8] = 3; blp[12] = 1; blp[16] = 1; blp[20] = 148; // plain BGRA, 1x1, its pixel right after the header
        blp[148] = 10; blp[149] = 20; blp[150] = 30; blp[151] = 40;
        Check("blp: plain", Icons.Blp(blp)!.Value.Rgba.SequenceEqual(new byte[] { 30, 20, 10, 40 }));
        var dxt = new byte[148 + 8];
        "BLP2"u8.CopyTo(dxt);
        dxt[4] = 1; dxt[8] = 2; dxt[12] = 4; dxt[16] = 4; dxt[20] = 148;
        dxt[148] = 0x00; dxt[149] = 0xf8; // colour 0 red, colour 1 black, every index 0
        var red = Icons.Blp(dxt)!.Value.Rgba;
        Check("blp: DXT1", red.Length == 64 && red[0] == 255 && red[1] == 0 && red[2] == 0 && red[3] == 255 && red[60] == 255);
        var png = Icons.ToPng(Tga())!;
        Check("png", png.AsSpan(1, 3).SequenceEqual("PNG"u8) && png[19] == 2 && png[23] == 2 && Icons.ToPng(png) == png);
        Check("png: not an image", Icons.ToPng([1, 2, 3, 4, 5]) is null);
        var zip = Zip(("Thing/icon.tga", Tga()));
        Check("extract: the extension WoW leaves out", Icons.Extract(zip, "Thing/icon") is { } extracted && extracted.AsSpan(1, 3).SequenceEqual("PNG"u8));

        // Icons kept in icons/, fetched again only when their source changes.
        var dir = Path.Combine(Path.GetTempPath(), "generate-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "stale.png"), [1]);
            var downloads = 0;
            await Icons.UpdateAsync(result, inputs, dir, _ => { downloads++; return Task.FromResult<byte[]?>(zip); });
            Check("icons: written", downloads == 1 && File.Exists(Path.Combine(dir, "thing.png")) && !File.Exists(Path.Combine(dir, "stale.png"))
                && Str(byId["thing"]["icon"]) == Settings.IconBase + "thing.png" && Str(byId["thing"]["from"]!["icon"]) == "toc-icon");
            var again = Generator.Build(inputs, new DateOnly(2026, 11, 26));
            await Icons.UpdateAsync(again, inputs, dir, _ => throw new InvalidOperationException("downloaded again"));
            Check("icons: cached", again.Addons.First(a => Str(a["id"]) == "thing")["icon"] is not null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Console.WriteLine(failures == 0 ? "generate.cs self-test passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
