#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Writes a detail file per listed GitHub add-on, addons/<id>.json, from what its author already wrote: the README, recent
// release notes (or CHANGELOG.md), screenshots, stats, the languages its .toc has, and technical details. The app fetches
// one only when the player opens that add-on, so catalog.json stays small. Format: schema/details.schema.json.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md), with a GitHub token in GITHUB_TOKEN, after generate.cs:
//   ... dotnet run scripts/details.cs                       # every listed add-on with a GitHub source
//   ... dotnet run scripts/details.cs -- --only id1,id2     # just these (nothing is removed)
//   ... dotnet run scripts/details.cs -- --self-test        # offline tests
// Options: --catalog <file> (default ./catalog.json), --candidates <file> (default ./candidates.json), --out <dir>
// (default ./addons).
//
// Everything read is untrusted: Markdown is passed through as text only. Raw HTML is turned into Markdown where it has a
// Markdown form (images, links, headings, bold) and dropped otherwise; scripts, styles, frames and comments go entirely;
// links keep http(s), mailto and #anchors only; images keep https only; relative links and images are made absolute on
// GitHub. The README and notes are size-capped. A file is rewritten only when its content changed, so its ETag stays
// valid. A repo that can't be read keeps its last file; a file whose add-on is no longer listed is removed.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    return Tests.Run();
}

string catalogPath = "catalog.json", candidatesPath = "candidates.json", outDir = "addons";
HashSet<string>? only = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--catalog" when i + 1 < args.Length: catalogPath = args[++i]; break;
        case "--candidates" when i + 1 < args.Length: candidatesPath = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--only" when i + 1 < args.Length: only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal); break;
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/details.cs [--catalog f] [--candidates f] [--out dir] [--only id,id] | --self-test");
            return 2;
    }
}
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("GITHUB_TOKEN isn't set: GraphQL needs a token.");
    return 2;
}

var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!;
var rows = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(candidatesPath))
{
    foreach (var row in JsonNode.Parse(File.ReadAllText(candidatesPath))!["candidates"]!.AsArray().OfType<JsonObject>())
    {
        rows[(string)row["repo"]!] = row;
    }
}

var targets = new List<Target>();
var listed = new HashSet<string>(StringComparer.Ordinal);
foreach (var addon in catalog["addons"]!.AsArray().OfType<JsonObject>())
{
    var id = (string)addon["id"]!;
    listed.Add(id);
    var repo = addon["sources"]?.AsArray().OfType<JsonObject>().Where(s => (string?)s["type"] == "github").Select(s => (string?)s["repo"]).FirstOrDefault();
    if (repo is null || (only is not null && !only.Contains(id)))
    {
        continue;
    }
    var folders = addon["folders"]!.AsArray().Select(f => (string)f!).ToList();
    var row = rows.GetValueOrDefault(repo);
    var editions = row?["editions"]?.AsArray().Select(e => (string)e!).ToList() ?? addon["editions"]?.AsObject().Select(e => e.Key).ToList() ?? [];
    targets.Add(new Target(id, repo, folders, editions, Toc.PathFor(row, folders)) { AddonName = (string?)addon["name"] });
}
// A repo that holds several listed add-ons: its root README is about the repo, not each add-on.
foreach (var group in targets.GroupBy(t => t.Repo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
{
    foreach (var t in group)
    {
        t.Shared = true;
    }
}

Directory.CreateDirectory(outDir);
using var github = new GitHub(token);
int written = 0, unchanged = 0, failed = 0;
var kept = new HashSet<string>(StringComparer.Ordinal);
foreach (var batch in targets.Chunk(10))
{
    var first = await github.FirstRoundAsync(batch);
    var second = await github.SecondRoundAsync(batch, first);
    var contributors = await Task.WhenAll(batch.Select(t => github.ContributorsAsync(t.Repo)));
    for (var k = 0; k < batch.Length; k++)
    {
        var t = batch[k];
        kept.Add(t.Id);
        if (first.GetValueOrDefault(t.Id) is not { } repoData)
        {
            failed++;
            Console.Error.WriteLine($"  {t.Id} ({t.Repo}): couldn't be read; its last file stays");
            continue;
        }
        var detail = Details.Build(t, repoData, second.GetValueOrDefault(t.Id), contributors[k]);
        var text = Details.Serialize(detail);
        var path = Path.Combine(outDir, t.Id + ".json");
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            unchanged++;
        }
        else
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            written++;
        }
    }
    Console.Error.Write($"\r  {kept.Count}/{targets.Count}");
}
Console.Error.WriteLine();

var removed = 0;
if (only is null)
{
    foreach (var file in Directory.GetFiles(outDir, "*.json"))
    {
        var id = Path.GetFileNameWithoutExtension(file);
        if (!kept.Contains(id))
        {
            File.Delete(file);
            removed++;
        }
    }
}
Console.WriteLine($"Detail files: {written} written, {unchanged} unchanged, {removed} removed, {failed} couldn't be read.");
return 0;

/// <summary>One listed add-on with a GitHub source.</summary>
sealed record Target(string Id, string Repo, List<string> Folders, List<string> Editions, string? TocPath)
{
    public string Owner => Repo.Split('/')[0];
    public string Name => Repo.Split('/')[1];
    public string TocDir => TocPath is { } p && p.LastIndexOf('/') is var s and > 0 ? p[..s] : "";

    /// <summary>The add-on's name in the catalog.</summary>
    public string? AddonName { get; init; }

    /// <summary>Whether the repo holds other listed add-ons too (a mono-repo, or one released by tag prefix).</summary>
    public bool Shared { get; set; }
}

/// <summary>Which README an add-on shows.</summary>
static class Readmes
{
    /// <summary>
    /// Whether a README is about this add-on: always for an add-on with its repo to itself; for one that shares its repo,
    /// the README in its own folder, or a root README whose title (its first heading) is the add-on's name.
    /// </summary>
    public static bool Fits(Target t, string path, string text)
    {
        if (!t.Shared)
        {
            return true;
        }
        var dir = path.LastIndexOf('/') is var s and > 0 ? path[..s] : "";
        if (dir.Length > 0)
        {
            return string.Equals(dir, t.TocDir, StringComparison.OrdinalIgnoreCase);
        }
        return Title(text) is { } title && t.AddonName is { } name && Plain(title).Length > 0 && Plain(title) == Plain(name);
    }

    /// <summary>The README's first heading (# Title, or a line underlined with = or -), when it starts with one.</summary>
    public static string? Title(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2).ToList();
        if (lines.Count == 0)
        {
            return null;
        }
        if (lines[0].StartsWith('#'))
        {
            return lines[0].TrimStart('#').Trim().TrimEnd('#').Trim();
        }
        return lines.Count == 2 && lines[1].Length > 1 && (lines[1].All(c => c == '=') || lines[1].All(c => c == '-')) ? lines[0] : null;
    }

    static string Plain(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>What the two GraphQL rounds read for one repo.</summary>
sealed record Second(string? ReadmePath, string? Readme, string? ChangelogPath, string? Changelog, List<string> ShotPaths);

static class Details
{
    public const int AboutMax = 50_000, NotesMax = 8_000, ChangelogMax = 20_000, MaxReleases = 5, MaxShots = 12;
    static readonly Regex ImageFile = new(@"\.(png|jpe?g|gif|webp)$", RegexOptions.IgnoreCase);

    public static JsonObject Build(Target t, JsonObject repo, Second? second, int? contributors)
    {
        var branch = (string?)repo["defaultBranchRef"]?["name"] ?? "HEAD";
        var detail = new JsonObject { ["schemaVersion"] = 1, ["id"] = t.Id, ["repo"] = t.Repo };

        if (second?.Readme is { } readme && Readmes.Fits(t, second.ReadmePath!, readme))
        {
            var (about, cut) = Markdown.Clean(readme, t.Repo, branch, DirOf(second.ReadmePath!), AboutMax);
            if (about.Length > 0)
            {
                detail["about"] = about;
                if (cut)
                {
                    detail["aboutTruncated"] = true;
                }
            }
        }

        var releases = new JsonArray();
        foreach (var r in (repo["recent"]?["nodes"]?.AsArray() ?? []).OfType<JsonObject>()
            .Where(r => r["isPrerelease"]?.GetValue<bool>() != true && r["isDraft"]?.GetValue<bool>() != true && r["publishedAt"] is not null)
            .Take(MaxReleases))
        {
            var tag = (string)r["tagName"]!;
            var release = new JsonObject { ["tag"] = tag };
            var name = ((string?)r["name"])?.Trim();
            if (!string.IsNullOrEmpty(name) && name != tag)
            {
                release["name"] = name.Length > 200 ? name[..200] : name;
            }
            release["published"] = Day((string)r["publishedAt"]!);
            var (notes, cut) = Markdown.Clean((string?)r["description"] ?? "", t.Repo, branch, "", NotesMax);
            if (notes.Length > 0)
            {
                release["notes"] = notes;
                if (cut)
                {
                    release["truncated"] = true;
                }
            }
            releases.Add(release);
        }
        if (releases.Count > 0)
        {
            detail["releases"] = releases;
        }
        if (!releases.Any(r => r!["notes"] is not null) && second?.Changelog is { } changelog)
        {
            var (text, cut) = Markdown.Clean(changelog, t.Repo, branch, DirOf(second.ChangelogPath!), ChangelogMax);
            if (text.Length > 0)
            {
                detail["changelog"] = text;
                if (cut)
                {
                    detail["changelogTruncated"] = true;
                }
            }
        }

        var shots = Markdown.Screenshots((string?)detail["about"] ?? "")
            .Concat((second?.ShotPaths ?? []).Where(p => ImageFile.IsMatch(p)).Order(StringComparer.Ordinal)
                .Select(p => $"https://raw.githubusercontent.com/{t.Repo}/{Markdown.EscapePath(branch)}/{Markdown.EscapePath(p)}"))
            .Distinct(StringComparer.Ordinal).Take(MaxShots).ToList();
        if (shots.Count > 0)
        {
            detail["screenshots"] = new JsonArray(shots.Select(s => (JsonNode)s).ToArray());
        }

        var stats = new JsonObject { ["stars"] = repo["stargazerCount"]?.GetValue<int>() ?? 0 };
        if (repo["hasIssuesEnabled"]?.GetValue<bool>() == true)
        {
            stats["openIssues"] = repo["issues"]?["totalCount"]?.GetValue<int>() ?? 0;
        }
        if (contributors is { } c)
        {
            stats["contributors"] = c;
        }
        var dates = (repo["history"]?["nodes"]?.AsArray() ?? []).OfType<JsonObject>()
            .Where(r => r["isPrerelease"]?.GetValue<bool>() != true && r["isDraft"]?.GetValue<bool>() != true && r["publishedAt"] is not null)
            .Select(r => Day((string)r["publishedAt"]!)).ToList();
        if (dates.Count > 0)
        {
            stats["lastRelease"] = dates[0];
            stats["releaseDates"] = new JsonArray(dates.Select(d => (JsonNode)d).ToArray());
        }
        detail["stats"] = stats;

        var toc = Toc.Read((string?)repo["toc"]?["text"]);
        if (toc.Languages.Count > 0)
        {
            detail["languages"] = Array(toc.Languages);
        }
        var technical = new JsonObject { ["folders"] = Array(t.Folders), ["editions"] = Array(t.Editions) };
        if (toc.Requires.Count > 0) technical["requires"] = Array(toc.Requires);
        if (toc.Optional.Count > 0) technical["optional"] = Array(toc.Optional);
        if (toc.SavedVariables.Count > 0) technical["savedVariables"] = Array(toc.SavedVariables);
        if (toc.SavedVariablesPerCharacter.Count > 0) technical["savedVariablesPerCharacter"] = Array(toc.SavedVariablesPerCharacter);
        detail["technical"] = technical;
        return detail;
    }

    static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());

    static string DirOf(string path) => path.LastIndexOf('/') is var s and > 0 ? path[..s] : "";

    /// <summary>The UTC day of a GitHub timestamp.</summary>
    public static string Day(string at) =>
        DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonObject detail) => detail.ToJsonString(Options).Replace("\r\n", "\n") + "\n";
}

/// <summary>Makes an author's Markdown safe to pass on: text only, with absolute links.</summary>
static class Markdown
{
    const RegexOptions I = RegexOptions.IgnoreCase;
    static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})");
    static readonly Regex InlineCode = new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)");
    static readonly Regex Comment = new(@"<!--[\s\S]*?(-->|$)");
    static readonly Regex Dropped = new(@"<(script|style|iframe|object|embed|noscript|svg|video|audio|form|template|math)\b[\s\S]*?(</\1\s*>|$)", I);
    static readonly Regex AutoLink = new(@"<(https?://[^\s<>]+)>", I);
    static readonly Regex Img = new(@"<img\b[^>]*>", I);
    static readonly Regex Anchor = new(@"<a\b([^>]*)>([\s\S]*?)</a\s*>", I);
    static readonly Regex Heading = new(@"<h([1-6])\b[^>]*>([\s\S]*?)</h\1\s*>", I);
    static readonly Regex Bold = new(@"<(b|strong)\b[^>]*>([^<\n]*?)</\1\s*>", I);
    static readonly Regex Italic = new(@"<(i|em)\b[^>]*>([^<\n]*?)</\1\s*>", I);
    static readonly Regex CodeTag = new(@"<code\b[^>]*>([^<`\n]*?)</code\s*>", I);
    static readonly Regex Break = new(@"<br\s*/?>", I);
    static readonly Regex Block = new(@"</?(p|div|details|summary|center|table|thead|tbody|tr|td|th|ul|ol|li|blockquote|hr|section|article|header|footer|picture|source|figure|figcaption)\b[^>]*>", I);
    public static readonly Regex Tag = new(@"</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>");
    public static readonly Regex Link = new(@"(!?)\[((?:[^\[\]]|\[[^\[\]]*\])*)\]\(\s*<?([^\s()<>]*(?:\([^\s()<>]*\)[^\s()<>]*)*)>?(\s+(?:""[^""]*""|'[^']*'))?\s*\)");
    public static readonly Regex Definition = new(@"^( {0,3}\[[^\]]+\]:[ \t]*)<?(\S+?)>?([ \t]+.*)?$", RegexOptions.Multiline);
    static readonly Regex ImageExt = new(@"\.(png|jpe?g|gif|webp|svg|bmp)$", I);
    static readonly Regex Blanks = new(@"\n{3,}");

    /// <summary>The cleaned text, and whether it was cut at <paramref name="max"/> characters.</summary>
    public static (string Text, bool Truncated) Clean(string text, string repo, string branch, string baseDir, int max)
    {
        text = new string(text.Replace("\r\n", "\n").Replace('\r', '\n').Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray()).TrimStart('﻿');
        var output = new StringBuilder();
        var prose = new StringBuilder();
        string? fence = null;
        foreach (var line in text.Split('\n'))
        {
            var m = Fence.Match(line);
            if (fence is null && m.Success)
            {
                output.Append(Prose(prose.ToString(), repo, branch, baseDir));
                prose.Clear();
                fence = m.Groups[1].Value;
                output.Append(line).Append('\n');
            }
            else if (fence is not null)
            {
                output.Append(line).Append('\n');
                if (m.Success && m.Groups[1].Value[0] == fence[0] && m.Groups[1].Value.Length >= fence.Length && line.Trim().Trim(fence[0]).Length == 0)
                {
                    fence = null;
                }
            }
            else
            {
                prose.Append(line).Append('\n');
            }
        }
        output.Append(Prose(prose.ToString(), repo, branch, baseDir));
        if (fence is not null)
        {
            output.Append(fence).Append('\n');
        }
        var result = Blanks.Replace(output.ToString(), "\n\n").Trim();
        if (result.Length <= max)
        {
            return (result, false);
        }
        var cut = result.LastIndexOf("\n\n", max, StringComparison.Ordinal);
        result = result[..(cut > max / 2 ? cut : max)].TrimEnd();
        var open = result.Split('\n').Select(l => Fence.Match(l)).Where(x => x.Success).Select(x => x.Groups[1].Value).ToList();
        string? still = null;
        foreach (var f in open)
        {
            still = still is null ? f : f[0] == still[0] && f.Length >= still.Length ? null : still;
        }
        if (still is not null)
        {
            result += "\n" + still;
        }
        return (result, true);
    }

    static string Prose(string text, string repo, string branch, string baseDir)
    {
        if (text.Length == 0)
        {
            return text;
        }
        var codes = new List<string>();
        text = InlineCode.Replace(text, m => { codes.Add(m.Value); return $"\u0001{codes.Count - 1}\u0001"; });
        text = Comment.Replace(text, "");
        text = Dropped.Replace(text, "");
        text = AutoLink.Replace(text, m => $"[{m.Groups[1].Value}]({m.Groups[1].Value})");
        text = Img.Replace(text, m =>
        {
            var src = Attr(m.Value, "src");
            return src is null ? "" : $"![{Attr(m.Value, "alt")?.Replace("]", "") ?? ""}]({src})";
        });
        text = Anchor.Replace(text, m => Attr(m.Groups[1].Value, "href") is { } href ? $"[{m.Groups[2].Value.Trim()}]({href})" : m.Groups[2].Value);
        text = Heading.Replace(text, m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + m.Groups[2].Value.Trim() + "\n");
        text = Bold.Replace(text, m => $"**{m.Groups[2].Value}**");
        text = Italic.Replace(text, m => $"*{m.Groups[2].Value}*");
        text = CodeTag.Replace(text, m => $"`{m.Groups[1].Value}`");
        text = Break.Replace(text, "\n");
        text = Block.Replace(text, "\n");
        text = Tag.Replace(text, "");
        text = Link.Replace(text, m =>
        {
            var image = m.Groups[1].Value == "!";
            var target = Resolve(m.Groups[3].Value, image, repo, branch, baseDir);
            var label = m.Groups[2].Value;
            return target is null ? (image ? "" : label) : $"{m.Groups[1].Value}[{label}]({target})";
        });
        text = Definition.Replace(text, m => Resolve(m.Groups[2].Value, ImageExt.IsMatch(m.Groups[2].Value.Split('?', '#')[0]), repo, branch, baseDir) is { } target
            ? m.Groups[1].Value + target + m.Groups[3].Value : "");
        return Regex.Replace(text, "\u0001(\\d+)\u0001", m => codes[int.Parse(m.Groups[1].Value)]);
    }

    static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(""([^""]*)""|'([^']*)'|([^\s""'>]+))", I);
        return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value).Trim() : null;
    }

    /// <summary>An absolute target for a link or image, or null when it isn't safe or can't be resolved.</summary>
    public static string? Resolve(string url, bool image, string repo, string branch, string baseDir)
    {
        url = url.Trim();
        if (url.Length == 0)
        {
            return null;
        }
        if (url.StartsWith('#'))
        {
            return image ? null : url;
        }
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        if (Regex.IsMatch(url, "^[A-Za-z][A-Za-z0-9+.-]*:"))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }
            if (image)
            {
                if (uri.Scheme != "https")
                {
                    return null;
                }
                // A github.com/<o>/<r>/blob/<b>/<path> image is an HTML page; its raw file is the image.
                var raw = Regex.Match(uri.AbsoluteUri, @"^https://github\.com/([^/]+/[^/]+)/(?:blob|raw)/(.+?)(\?raw=true)?$", I);
                return raw.Success ? $"https://raw.githubusercontent.com/{raw.Groups[1].Value}/{raw.Groups[2].Value}" : uri.AbsoluteUri;
            }
            return uri.Scheme is "https" or "http" or "mailto" ? (uri.Scheme == "mailto" ? url : uri.AbsoluteUri) : null;
        }
        var cut = url.IndexOfAny(['?', '#']);
        var suffix = cut >= 0 ? url[cut..] : "";
        var path = cut >= 0 ? url[..cut] : url;
        var parts = new List<string>();
        foreach (var part in ((path.StartsWith('/') ? "" : baseDir) + "/" + path).Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(Uri.UnescapeDataString(part));
        }
        var joined = EscapePath(string.Join('/', parts));
        if (image || ImageExt.IsMatch(path))
        {
            return parts.Count == 0 ? null : $"https://raw.githubusercontent.com/{repo}/{EscapePath(branch)}/{joined}";
        }
        return $"https://github.com/{repo}/{(parts.Count == 0 ? "" : $"blob/{EscapePath(branch)}/{joined}")}{(suffix.StartsWith('#') ? suffix : "")}".TrimEnd('/');
    }

    public static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    static readonly Regex ShotHost = new(@"^https://(raw\.githubusercontent\.com/|user-images\.githubusercontent\.com/|github\.com/user-attachments/assets/)", I);
    static readonly Regex Badge = new(@"badge|shields|/logo|icon|banner|\.svg($|\?)", I);

    /// <summary>The images in cleaned Markdown that are likely screenshots: on GitHub, not badges, logos or icons.</summary>
    public static IEnumerable<string> Screenshots(string markdown) =>
        Link.Matches(markdown).Where(m => m.Groups[1].Value == "!").Select(m => m.Groups[3].Value)
            .Where(u => ShotHost.IsMatch(u) && !Badge.IsMatch(u)
                && (u.Contains("/user-attachments/assets/", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(u.Split('?')[0], @"\.(png|jpe?g|gif|webp)$", I)));
}

/// <summary>What the main folder's .toc says that the detail file shows.</summary>
sealed record Toc(List<string> Languages, List<string> Requires, List<string> Optional, List<string> SavedVariables, List<string> SavedVariablesPerCharacter)
{
    static readonly Regex Field = new(@"^##\s*([A-Za-z-]+)\s*:(.*)$", RegexOptions.Multiline);
    static readonly Regex Name = new(@"^[A-Za-z0-9_!.\- ]{1,64}$");
    static readonly Regex Variable = new(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$");

    public static Toc Read(string? text)
    {
        var languages = new SortedSet<string>(StringComparer.Ordinal);
        List<string> requires = [], optional = [], saved = [], perCharacter = [];
        foreach (Match m in Field.Matches((text ?? "").Replace("\r", "")))
        {
            var key = m.Groups[1].Value;
            var values = m.Groups[2].Value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (Regex.Match(key, "^(?:Title|Notes)-([a-z]{2}[A-Z]{2})$") is { Success: true } lang)
            {
                languages.Add(lang.Groups[1].Value);
            }
            else if (key.Equals("Dependencies", StringComparison.OrdinalIgnoreCase) || key.Equals("RequiredDeps", StringComparison.OrdinalIgnoreCase))
            {
                requires.AddRange(values.Where(v => Name.IsMatch(v)));
            }
            else if (key.Equals("OptionalDeps", StringComparison.OrdinalIgnoreCase))
            {
                optional.AddRange(values.Where(v => Name.IsMatch(v)));
            }
            else if (key.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
            {
                saved.AddRange(values.Where(v => Variable.IsMatch(v)));
            }
            else if (key.Equals("SavedVariablesPerCharacter", StringComparison.OrdinalIgnoreCase))
            {
                perCharacter.AddRange(values.Where(v => Variable.IsMatch(v)));
            }
        }
        return new([.. languages], [.. requires.Distinct()], [.. optional.Distinct()], [.. saved.Distinct()], [.. perCharacter.Distinct()]);
    }

    /// <summary>The .toc of the main folder (the catalog's first): in a folder of that name, or named after it.</summary>
    public static string? PathFor(JsonObject? row, List<string> folders)
    {
        var paths = (row?["tocs"]?.AsArray() ?? []).OfType<JsonObject>().Select(t => (string?)t["path"]).OfType<string>().ToList();
        var main = folders.FirstOrDefault() ?? "";
        bool Ours(string p)
        {
            var dir = p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
            var file = Path.GetFileNameWithoutExtension(p);
            return dir.Split('/')[^1].Equals(main, StringComparison.OrdinalIgnoreCase)
                || file.Equals(main, StringComparison.OrdinalIgnoreCase) || file.StartsWith(main + "_", StringComparison.OrdinalIgnoreCase) || file.StartsWith(main + "-", StringComparison.OrdinalIgnoreCase);
        }
        return paths.Where(Ours).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault() ?? (string?)row?["toc"]?["path"];
    }
}

sealed class GitHub : IDisposable
{
    readonly HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
    readonly SemaphoreSlim rest = new(6);

    public GitHub(string token)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-details");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => http.Dispose();

    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    const string TreeFields = "... on Tree { entries { name type } }";

    /// <summary>Stats, the root and .toc folder listings, the .toc, and releases, by add-on ID.</summary>
    public async Task<Dictionary<string, JsonObject>> FirstRoundAsync(Target[] batch)
    {
        var query = new StringBuilder("query {");
        for (var i = 0; i < batch.Length; i++)
        {
            var t = batch[i];
            query.Append($" r{i}: repository(owner: {Q(t.Owner)}, name: {Q(t.Name)}) {{ defaultBranchRef {{ name }} stargazerCount hasIssuesEnabled issues(states: OPEN) {{ totalCount }}");
            query.Append($" root: object(expression: \"HEAD:\") {{ {TreeFields} }}");
            if (t.TocDir.Length > 0)
            {
                query.Append($" dir: object(expression: {Q("HEAD:" + t.TocDir)}) {{ {TreeFields} }}");
            }
            if (t.TocPath is { } toc)
            {
                query.Append($" toc: object(expression: {Q("HEAD:" + toc)}) {{ ... on Blob {{ text }} }}");
            }
            query.Append(" recent: releases(first: 10, orderBy: {field: CREATED_AT, direction: DESC}) { nodes { tagName name publishedAt isPrerelease isDraft description } }");
            query.Append(" history: releases(first: 50, orderBy: {field: CREATED_AT, direction: DESC}) { nodes { publishedAt isPrerelease isDraft } } }");
        }
        query.Append(" }");
        var data = await GraphQlAsync(query.ToString(), "details batch");
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var i = 0; i < batch.Length; i++)
        {
            if (data?[$"r{i}"] is JsonObject repo)
            {
                result[batch[i].Id] = repo;
            }
        }
        return result;
    }

    static readonly Regex Readme = new(@"^readme(\.(md|markdown|txt))?$", RegexOptions.IgnoreCase);
    static readonly Regex Changelog = new(@"^(changelog|changes|history)(\.(md|markdown|txt))?$", RegexOptions.IgnoreCase);

    /// <summary>The README, CHANGELOG and screenshots folder each repo's listings showed.</summary>
    public async Task<Dictionary<string, Second>> SecondRoundAsync(Target[] batch, Dictionary<string, JsonObject> first)
    {
        var wanted = new List<(int Index, Target T, string? Readme, string? Changelog, string? Shots)>();
        foreach (var (t, i) in batch.Select((t, i) => (t, i)))
        {
            if (first.GetValueOrDefault(t.Id) is not { } repo)
            {
                continue;
            }
            string? Find(Regex pattern, string type)
            {
                // A shared repo's add-on looks in its own folder first: the root README is the repo's (Build checks it fits).
            var places = t.Shared ? new[] { ("dir", t.TocDir), ("root", "") } : new[] { ("root", ""), ("dir", t.TocDir) };
            foreach (var (key, dir) in places)
                {
                    var names = (repo[key]?["entries"]?.AsArray() ?? []).OfType<JsonObject>().Where(e => (string?)e["type"] == type).Select(e => (string)e["name"]!).ToList();
                    var hit = names.Where(n => pattern.IsMatch(n)).OrderBy(n => n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || !n.Contains('.')).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
                    if (hit is not null)
                    {
                        return dir.Length > 0 ? dir + "/" + hit : hit;
                    }
                }
                return null;
            }
            var entry = (i, t, Find(Readme, "blob"), Find(Changelog, "blob"), Find(new Regex("^screenshots?$", RegexOptions.IgnoreCase), "tree"));
            if (entry.Item3 is not null || entry.Item4 is not null || entry.Item5 is not null)
            {
                wanted.Add(entry);
            }
        }
        var result = new Dictionary<string, Second>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return result;
        }
        var query = new StringBuilder("query {");
        foreach (var w in wanted)
        {
            query.Append($" r{w.Index}: repository(owner: {Q(w.T.Owner)}, name: {Q(w.T.Name)}) {{");
            if (w.Readme is not null) query.Append($" readme: object(expression: {Q("HEAD:" + w.Readme)}) {{ ... on Blob {{ text }} }}");
            if (w.Changelog is not null) query.Append($" changelog: object(expression: {Q("HEAD:" + w.Changelog)}) {{ ... on Blob {{ text }} }}");
            if (w.Shots is not null) query.Append($" shots: object(expression: {Q("HEAD:" + w.Shots)}) {{ ... on Tree {{ entries {{ path type }} }} }}");
            query.Append(" }");
        }
        query.Append(" }");
        var data = await GraphQlAsync(query.ToString(), "details files");
        foreach (var w in wanted)
        {
            var repo = data?[$"r{w.Index}"];
            result[w.T.Id] = new Second(w.Readme, (string?)repo?["readme"]?["text"], w.Changelog, (string?)repo?["changelog"]?["text"],
                (repo?["shots"]?["entries"]?.AsArray() ?? []).OfType<JsonObject>().Where(e => (string?)e["type"] == "blob").Select(e => (string)e["path"]!).ToList());
        }
        return result;
    }

    /// <summary>How many contributors GitHub lists (anonymous ones included), or null when it won't say.</summary>
    public async Task<int?> ContributorsAsync(string repo)
    {
        await rest.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using var response = await http.GetAsync($"repos/{repo}/contributors?per_page=1&anon=1");
                    if ((int)response.StatusCode is 403 or 429 && attempt < 3 && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60));
                        continue;
                    }
                    if ((int)response.StatusCode == 204)
                    {
                        return 0;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    if (response.Headers.TryGetValues("Link", out var links) && LastPage(string.Join(",", links)) is { } last)
                    {
                        return last;
                    }
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsArray().Count ?? 0;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
                }
            }
            return null;
        }
        finally
        {
            rest.Release();
        }
    }

    /// <summary>The page number of rel="last" in a Link header.</summary>
    public static int? LastPage(string link) =>
        Regex.Match(link, @"[?&]page=(\d+)[^>]*>;\s*rel=""last""") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    async Task<JsonNode?> GraphQlAsync(string query, string what)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var response = await http.PostAsync("graphql", new StringContent(new JsonObject { ["query"] = query }.ToJsonString(), Encoding.UTF8, "application/json"));
                var text = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var json = JsonNode.Parse(text);
                    if (json?["data"] is JsonObject data)
                    {
                        return data;
                    }
                }
                var status = (int)response.StatusCode;
                Console.Error.WriteLine($"  {what}: {status}, retrying");
                await Task.Delay(TimeSpan.FromSeconds(status is 403 or 429 ? 60 : 10 * (attempt + 1)));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                Console.Error.WriteLine($"  {what}: {e.GetType().Name}, retrying");
                await Task.Delay(TimeSpan.FromSeconds(10 * (attempt + 1)));
            }
        }
        return null;
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

    static string C(string text, int max = 10_000) => Markdown.Clean(text, "o/r", "main", "docs", max).Text;

    public static int Run()
    {
        Check("img tag becomes an image, made absolute", C("<p align=\"center\"><img src=\"shot.png\" alt=\"Shot\" width=\"300\"></p>") == "![Shot](https://raw.githubusercontent.com/o/r/main/docs/shot.png)");
        Check("a tag becomes a link", C("<a href=\"https://x.org/a\">X</a>") == "[X](https://x.org/a)");
        Check("scripts, styles and comments go", C("a<script>alert(1)</script>b<style>p{}</style>c<!-- hidden -->d") == "abcd");
        Check("unknown tags go, text stays", C("<span onclick=\"x()\">hi</span> <kbd>K</kbd>") == "hi K");
        Check("heading tag", C("<h2 align=center>Title</h2>") == "## Title");
        Check("relative link from the README's folder", C("[Guide](guide.md#setup)") == "[Guide](https://github.com/o/r/blob/main/docs/guide.md#setup)");
        Check("root-relative link", C("[L](/LICENSE)") == "[L](https://github.com/o/r/blob/main/LICENSE)");
        Check("a path above the repo is dropped", C("[x](../../etc)") == "x");
        Check("javascript: link keeps only its text", C("[click](javascript:alert(1))") == "click");
        Check("data: image goes", C("![a](data:image/png;base64,AAAA)") == "");
        Check("http image goes, http link stays", C("![a](http://x.org/a.png) [b](http://x.org/)") == "[b](http://x.org/)");
        Check("anchor stays", C("[top](#top)") == "[top](#top)");
        Check("blob image becomes raw", C("![s](https://github.com/o/r/blob/main/s.png?raw=true)") == "![s](https://raw.githubusercontent.com/o/r/main/s.png)");
        Check("reference definition", C("[logo]: img/logo.png") == "[logo]: https://raw.githubusercontent.com/o/r/main/docs/img/logo.png");
        Check("code fences are left alone", C("```lua\nif a<b then print(\"<b>\") end\n```") == "```lua\nif a<b then print(\"<b>\") end\n```");
        Check("inline code is left alone", C("Use `<Shift>` to drag") == "Use `<Shift>` to drag");
        Check("autolink", C("<https://x.org/a>") == "[https://x.org/a](https://x.org/a)");
        var (cut, truncated) = Markdown.Clean("Intro.\n\n```\n" + new string('x', 200) + "\n```", "o/r", "main", "", 100);
        Check("cut at the cap closes an open fence", truncated && cut.EndsWith("\n```", StringComparison.Ordinal) && cut.Length < 120);
        Check("an unclosed fence is closed", C("```\ncode") == "```\ncode\n```");
        Check("screenshots: GitHub images, not badges", Markdown.Screenshots("![a](https://raw.githubusercontent.com/o/r/main/a.png) ![b](https://img.shields.io/x.png) ![c](https://raw.githubusercontent.com/o/r/main/badge.png) ![d](https://github.com/user-attachments/assets/1234)")
            .SequenceEqual(["https://raw.githubusercontent.com/o/r/main/a.png", "https://github.com/user-attachments/assets/1234"]));
        var toc = Toc.Read("## Title: X\r\n## Title-deDE: X\n## Notes-frFR: Y\n## Dependencies: Ace3, Lib Stub\n## OptionalDeps: Masque\n## SavedVariables: XDB, Bad-Name\n## SavedVariablesPerCharacter: XCharDB\n");
        Check("toc: languages, dependencies and saved variables", toc.Languages.SequenceEqual(["deDE", "frFR"]) && toc.Requires.SequenceEqual(["Ace3", "Lib Stub"])
            && toc.Optional.SequenceEqual(["Masque"]) && toc.SavedVariables.SequenceEqual(["XDB"]) && toc.SavedVariablesPerCharacter.SequenceEqual(["XCharDB"]));
        var row = JsonNode.Parse("""{"tocs":[{"path":"Libs/Lib.toc"},{"path":"Thing/Thing_Vanilla.toc"},{"path":"Thing/Thing.toc"}],"toc":{"path":"Libs/Lib.toc"}}""")!.AsObject();
        Check("toc of the main folder", Toc.PathFor(row, ["Thing"]) == "Thing/Thing.toc" && Toc.PathFor(row, ["Other"]) == "Libs/Lib.toc");
        Check("Link header's last page", GitHub.LastPage("<https://api.github.com/x?per_page=1&anon=1&page=2>; rel=\"next\", <https://api.github.com/x?per_page=1&anon=1&page=37>; rel=\"last\"") == 37
            && GitHub.LastPage("") is null);

        var repo = JsonNode.Parse("""
            {"defaultBranchRef":{"name":"main"},"stargazerCount":12,"hasIssuesEnabled":true,"issues":{"totalCount":3},
             "toc":{"text":"## Title-deDE: X\n## SavedVariables: ThingDB"},
             "recent":{"nodes":[{"tagName":"v3-beta","publishedAt":"2026-11-25T10:00:00Z","isPrerelease":true,"isDraft":false,"description":"beta"},
                                {"tagName":"v2","name":"Version 2","publishedAt":"2026-11-20T23:30:00Z","isPrerelease":false,"isDraft":false,"description":"<b>Fixed</b> it"},
                                {"tagName":"v1","name":"v1","publishedAt":"2026-10-01T10:00:00Z","isPrerelease":false,"isDraft":false,"description":""}]},
             "history":{"nodes":[{"publishedAt":"2026-11-25T10:00:00Z","isPrerelease":true,"isDraft":false},{"publishedAt":"2026-11-20T23:30:00Z","isPrerelease":false,"isDraft":false},{"publishedAt":"2026-10-01T10:00:00Z","isPrerelease":false,"isDraft":false}]}}
            """)!.AsObject();
        var target = new Target("thing", "o/r", ["Thing"], ["forever", "retail"], "Thing/Thing.toc");
        var detail = Details.Build(target, repo, new Second("README.md", "# Thing\n\n![shot](shots/a.png)", "CHANGELOG.md", "## 2\nFixed", ["Screenshots/b.jpg", "Screenshots/notes.txt"]), 4);
        Check("detail: about", (string?)detail["about"] == "# Thing\n\n![shot](https://raw.githubusercontent.com/o/r/main/shots/a.png)" && detail["aboutTruncated"] is null);
        Check("detail: stable releases only, with notes when there are any", detail["releases"]!.AsArray().Count == 2 && (string?)detail["releases"]![0]!["notes"] == "**Fixed** it"
            && (string?)detail["releases"]![0]!["name"] == "Version 2" && detail["releases"]![1]!["name"] is null && detail["releases"]![1]!["notes"] is null
            && (string?)detail["releases"]![0]!["published"] == "2026-11-20");
        Check("detail: no changelog when releases have notes", detail["changelog"] is null);
        Check("detail: screenshots from the README and the folder", detail["screenshots"]!.AsArray().Select(s => (string)s!).SequenceEqual(
            ["https://raw.githubusercontent.com/o/r/main/shots/a.png", "https://raw.githubusercontent.com/o/r/main/Screenshots/b.jpg"]));
        Check("detail: stats", (int)detail["stats"]!["stars"]! == 12 && (int)detail["stats"]!["openIssues"]! == 3 && (int)detail["stats"]!["contributors"]! == 4
            && (string?)detail["stats"]!["lastRelease"] == "2026-11-20" && detail["stats"]!["releaseDates"]!.AsArray().Count == 2);
        Check("detail: languages and technical", detail["languages"]!.AsArray().Select(s => (string)s!).SequenceEqual(["deDE"])
            && (string?)detail["technical"]!["savedVariables"]![0] == "ThingDB" && detail["technical"]!["editions"]!.AsArray().Count == 2);
        repo["recent"] = JsonNode.Parse("""{"nodes":[]}""");
        var noNotes = Details.Build(target, repo, new Second(null, null, "CHANGELOG.md", "## 2\nFixed", []), null);
        Check("detail: the changelog when no release has notes", (string?)noNotes["changelog"] == "## 2\nFixed" && noNotes["about"] is null && noNotes["stats"]!["contributors"] is null);
        var shared = target with { AddonName = "Thing", Shared = true };
        Check("readme: an add-on with its repo to itself shows the root README", Readmes.Fits(target, "README.md", "# Toolkit\nAll of it."));
        Check("readme: a shared repo's add-on shows the README in its own folder", Readmes.Fits(shared, "Thing/README.md", "# Anything"));
        Check("readme: a shared repo's add-on never shows the repo's root README", !Readmes.Fits(shared, "README.md", "# Toolkit\nAll of it.")
            && !Readmes.Fits(shared, "README.md", "No title here.") && !Readmes.Fits(shared, "Other/README.md", "# Thing"));
        Check("readme: unless its title is the add-on's name", Readmes.Fits(shared, "README.md", "\n# Thing #\nIt does things.")
            && Readmes.Fits(shared, "README.md", "THING\n=====\n") && Readmes.Fits(shared, "readme.md", "## thing"));
        var sharedNone = Details.Build(shared, repo, new Second("README.md", "# Toolkit\nEverything.", null, null, []), null);
        Check("detail: a shared repo with no README of the add-on's own has no about", sharedNone["about"] is null);
        var sharedOwn = Details.Build(shared, repo, new Second("Thing/README.md", "# Thing\nMine.", null, null, []), null);
        Check("detail: a shared repo's add-on with its own README shows it", (string?)sharedOwn["about"] == "# Thing\nMine.");
        Check("serialized the same way each time", Details.Serialize(detail) == Details.Serialize(detail) && Details.Serialize(detail).EndsWith("}\n", StringComparison.Ordinal));

        Console.WriteLine(failures == 0 ? "details.cs self-test passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
