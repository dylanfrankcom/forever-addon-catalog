#:package JsonSchema.Net@9.7.0
#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Validates catalog.json: the schema, plus the rules a schema can't express. With no file named, also versions.json,
// optout.json, overrides/ and the detail files in addons/ (summed up in one line).
//
// Run it through the app repo's dev.ps1, so NuGet stays in Documents\Developer\.tools:
//   powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs                 # ./catalog.json
//   powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs -- path/to/file  # another file
//   powershell -ExecutionPolicy Bypass -File ..\ForeverAddonManager\dev.ps1 dotnet run scripts/validate.cs -- --self-test   # scripts/test-data
//   ... dotnet run scripts/validate.cs -- catalog.json --base base.json   # also: entries changed => version bumped, new entries => "added"
//
// Exit code 0 when valid, 1 when not (or when a self-test case doesn't give the expected result), 2 for bad arguments.
// The schema is found by walking up from the catalog file to a folder holding schema/catalog.schema.json.

using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

if (args is ["--self-test"])
{
    return SelfTest.Run();
}

string? catalogArg = null;
string? baseArg = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--base" && i + 1 < args.Length)
    {
        baseArg = args[++i];
    }
    else if (!args[i].StartsWith('-') && catalogArg is null)
    {
        catalogArg = args[i];
    }
    else
    {
        Console.Error.WriteLine("Usage: dotnet run scripts/validate.cs [catalog.json] [--base <previous catalog.json>] | --self-test");
        return 2;
    }
}

// With no file named: catalog.json, and versions.json too once the release check writes one.
var paths = catalogArg is not null
    ? new List<string> { Path.GetFullPath(catalogArg) }
    : new[] { "catalog.json", "versions.json", "optout.json", "confirmed.json", "tags.json" }.Select(Path.GetFullPath).Where((p, i) => i == 0 || File.Exists(p))
        .Concat(Directory.Exists("overrides") ? Directory.GetFiles("overrides", "*.json").Order(StringComparer.Ordinal).Select(Path.GetFullPath) : []).ToList();
var exit = 0;
if (catalogArg is null && Directory.Exists("addons"))
{
    var detailFiles = Directory.GetFiles("addons", "*.json").Order(StringComparer.Ordinal).ToList();
    var bad = 0;
    foreach (var file in detailFiles)
    {
        var problems = CatalogValidator.Validate(Path.GetFullPath(file));
        foreach (var p in problems)
        {
            Console.Error.WriteLine($"addons/{Path.GetFileName(file)}: {p.Code}: {p.Message}");
        }
        bad += problems.Count > 0 ? 1 : 0;
    }
    Console.WriteLine(bad == 0 ? $"addons/: {detailFiles.Count} detail files are valid." : $"addons/: {bad} of {detailFiles.Count} detail files have problems.");
    exit = bad == 0 ? 0 : 1;
}
foreach (var path in paths)
{
    var problems = CatalogValidator.Validate(path);
    if (problems.Count == 0 && baseArg is not null && CatalogValidator.KindOf(path) == "catalog")
    {
        problems.AddRange(VersionBump.Check(Path.GetFullPath(baseArg), path));
    }
    foreach (var p in problems)
    {
        Console.Error.WriteLine($"{p.Code}: {p.Message}");
    }
    Console.WriteLine(problems.Count == 0 ? $"{Path.GetFileName(path)} is valid." : $"{problems.Count} problem(s) in {Path.GetFileName(path)}.");
    if (problems.Count > 0)
    {
        exit = 1;
    }
}
return exit;

/// <summary>
/// A detail file (addons/&lt;id&gt;.json): its Markdown has no raw HTML outside code, its links are http(s), mailto or
/// #anchors and its images https; in addons/, it's named after its ID and that ID is listed in catalog.json beside it.
/// </summary>
static class DetailsRules
{
    static readonly System.Text.RegularExpressions.Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})");
    static readonly System.Text.RegularExpressions.Regex InlineCode = new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)");
    static readonly System.Text.RegularExpressions.Regex Tag = new(@"</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>");
    static readonly System.Text.RegularExpressions.Regex Link = new(@"(!?)\[((?:[^\[\]]|\[[^\[\]]*\])*)\]\(\s*<?([^\s()<>]*(?:\([^\s()<>]*\)[^\s()<>]*)*)>?(\s+(?:""[^""]*""|'[^']*'))?\s*\)");
    static readonly System.Text.RegularExpressions.Regex Definition = new(@"^ {0,3}\[[^\]]+\]:[ \t]*<?(\S+?)>?([ \t]+.*)?$", System.Text.RegularExpressions.RegexOptions.Multiline);

    public static List<Problem> Check(JsonNode root, string path)
    {
        var problems = new List<Problem>();
        var id = (string)root["id"]!;
        var dir = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(dir) == "addons")
        {
            if (Path.GetFileNameWithoutExtension(path) != id)
            {
                problems.Add(new("details-id", $"addons/{Path.GetFileName(path)} holds '{id}': the file name must be its ID."));
            }
            var catalog = Path.Combine(Path.GetDirectoryName(dir)!, "catalog.json");
            if (File.Exists(catalog) && !Listed(catalog).Contains(id))
            {
                problems.Add(new("details-unlisted", $"'{id}' isn't listed in catalog.json, so its detail file should be gone."));
            }
        }
        var fields = new List<(string Name, string Text)>();
        foreach (var name in new[] { "about", "changelog" })
        {
            if (root[name] is JsonValue v)
            {
                fields.Add((name, (string)v!));
            }
        }
        foreach (var (release, i) in (root["releases"]?.AsArray() ?? []).Select((r, i) => (r, i)))
        {
            if (release?["notes"] is JsonValue v)
            {
                fields.Add(($"releases[{i}].notes", (string)v!));
            }
        }
        foreach (var (name, text) in fields)
        {
            var prose = Prose(text);
            if (Tag.Match(prose) is { Success: true } tag)
            {
                problems.Add(new("details-html", $"'{id}' {name} has raw HTML outside code: {tag.Value[..Math.Min(60, tag.Value.Length)]}"));
            }
            var targets = Link.Matches(prose).Select(m => (Image: m.Groups[1].Value == "!", Url: m.Groups[3].Value))
                .Concat(Definition.Matches(prose).Select(m => (Image: false, Url: m.Groups[1].Value)));
            foreach (var (image, url) in targets)
            {
                var ok = image ? url.StartsWith("https://", StringComparison.Ordinal)
                    : url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal) || url.StartsWith('#');
                if (!ok)
                {
                    problems.Add(new("details-link", $"'{id}' {name} has {(image ? "an image" : "a link")} that isn't absolute and safe: {url[..Math.Min(80, url.Length)]}"));
                }
            }
        }
        return problems;
    }

    static readonly Dictionary<string, HashSet<string>> listed = new(StringComparer.OrdinalIgnoreCase);

    static HashSet<string> Listed(string catalog)
    {
        if (!listed.TryGetValue(catalog, out var ids))
        {
            ids = (JsonNode.Parse(File.ReadAllText(catalog))?["addons"]?.AsArray() ?? []).Select(a => (string?)a?["id"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
            listed[catalog] = ids;
        }
        return ids;
    }

    /// <summary>The text outside code fences and inline code, where HTML would be rendered.</summary>
    static string Prose(string text)
    {
        var output = new System.Text.StringBuilder();
        string? fence = null;
        foreach (var line in text.Split('\n'))
        {
            var m = Fence.Match(line);
            if (fence is null && m.Success)
            {
                fence = m.Groups[1].Value;
            }
            else if (fence is not null)
            {
                if (m.Success && m.Groups[1].Value[0] == fence[0] && m.Groups[1].Value.Length >= fence.Length && line.Trim().Trim(fence[0]).Length == 0)
                {
                    fence = null;
                }
            }
            else
            {
                output.Append(InlineCode.Replace(line, "")).Append('\n');
            }
        }
        return output.ToString();
    }
}

/// <summary>One thing wrong with a catalog. Codes are stable, so tests can expect them.</summary>
record Problem(string Code, string Message);

static class CatalogValidator
{
    public static List<Problem> Validate(string catalogPath)
    {
        var problems = new List<Problem>();
        if (!File.Exists(catalogPath))
        {
            problems.Add(new("missing-file", $"{catalogPath} doesn't exist."));
            return problems;
        }

        var kind = KindOf(catalogPath);
        var versions = kind == "versions";
        var schemaName = kind + ".schema.json";
        var schemaPath = FindSchema(Path.GetDirectoryName(catalogPath)!, schemaName);
        if (schemaPath is null)
        {
            problems.Add(new("missing-schema", $"No schema/{schemaName} above {catalogPath}."));
            return problems;
        }
        if (kind != "catalog")
        {
            // The other schemas refer to catalog.schema.json's definitions, so that one is loaded first.
            LoadSchema(Path.Combine(Path.GetDirectoryName(schemaPath)!, "catalog.schema.json"));
        }

        JsonNode? root;
        JsonElement element;
        try
        {
            var text = File.ReadAllText(catalogPath);
            root = JsonNode.Parse(text);
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            problems.Add(new("json", $"Not valid JSON: {e.Message}"));
            return problems;
        }

        // 1. The schema.
        var schema = LoadSchema(schemaPath);
        var result = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (!result.IsValid)
        {
            foreach (var (location, message) in SchemaErrors(result))
            {
                problems.Add(new("schema", $"{Where(root, location)}: {message}"));
            }
            if (problems.Count == 0)
            {
                problems.Add(new("schema", "The catalog doesn't match the schema."));
            }
            // The rules below assume the schema's shape, so stop here.
            return Dedupe(problems);
        }

        // 2. Rules the schema can't express.
        if (versions)
        {
            return Dedupe(VersionsRules(root!));
        }
        if (kind == "details")
        {
            return Dedupe(DetailsRules.Check(root!, catalogPath));
        }
        if (kind != "catalog")
        {
            // An override's file name is the ID of the entry it corrects.
            var name = Path.GetFileNameWithoutExtension(catalogPath);
            if (kind == "override" && Path.GetFileName(Path.GetDirectoryName(catalogPath)) == "overrides" && !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                problems.Add(new("override-name", $"overrides/{name}.json: the file name must be the entry's ID."));
            }
            return problems;
        }
        var addons = root!["addons"]!.AsArray().Select(a => a!.AsObject()).ToList();

        // Editions: each Interface is its game version's, and every edition an
        // entry lists is defined, with upToDate saying whether it lists that edition's current Interface.
        var editions = root["editions"]?.AsObject();
        foreach (var (editionId, edition) in editions ?? [])
        {
            var gameVersion = (string)edition!["gameVersion"]!;
            var iface = (string)edition["interface"]!;
            if (InterfaceOf(gameVersion) != iface)
            {
                problems.Add(new("edition-interface", $"Edition '{editionId}' interface {iface} isn't game version {gameVersion}'s ({InterfaceOf(gameVersion)})."));
            }
            var since = (string)edition["interfaceSince"]!;
            if (!DateOnly.TryParseExact(since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var sinceDay)
                || sinceDay > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
            {
                problems.Add(new("edition-interface", $"Edition '{editionId}' interfaceSince '{since}' isn't a real day, or is in the future."));
            }

            // A PTR belongs to the live edition Battle.net files it under (whatever version it runs).
            if (edition["of"] is JsonValue ofValue)
            {
                var of = (string)ofValue!;
                if (editions?[of] is not JsonObject target || target["of"] is not null)
                {
                    problems.Add(new("test-client-edition", $"PTR '{editionId}' is of '{of}', which isn't a live edition in \"editions\"."));
                }
            }

            // How it reads an add-on: .toc names ignore case, so two suffixes that differ only in case are one.
            var suffixes = edition["tocSuffixes"]?.AsArray().Select(s => (string)s!).ToList() ?? [];
            foreach (var twice in suffixes.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                problems.Add(new("edition-facts", $"Edition '{editionId}' lists the .toc suffix '{twice.Key}' more than once (.toc names ignore case)."));
            }
        }
        foreach (var addon in addons)
        {
            // A confirmed version is for an edition the entry lists.
            foreach (var source in addon["sources"]?.AsArray() ?? [])
            {
                foreach (var (editionId, _) in source?["confirmed"]?.AsObject() ?? [])
                {
                    if (addon["editions"]?[editionId] is null)
                    {
                        problems.Add(new("confirmed-edition", $"'{addon["id"]}' {source!["type"]} has a confirmed version for '{editionId}', which the entry doesn't list."));
                    }
                }
            }
            foreach (var (editionId, listed) in addon["editions"]?.AsObject() ?? [])
            {
                var id = (string)addon["id"]!;
                if (editions?[editionId] is not JsonObject edition)
                {
                    problems.Add(new("unknown-edition", $"'{id}' lists edition '{editionId}', which isn't in \"editions\"."));
                    continue;
                }
                if (listed!["released"] is JsonValue releasedValue)
                {
                    var released = (string)releasedValue!;
                    if (!DateOnly.TryParseExact(released, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var releasedDay))
                    {
                        problems.Add(new("released-date", $"'{id}' edition '{editionId}' released '{released}' isn't a real date."));
                    }
                    else if (releasedDay > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
                    {
                        problems.Add(new("released-date", $"'{id}' edition '{editionId}' released {released} is in the future."));
                    }
                }
                var entryFolders = Strings(addon["folders"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in Strings(listed!["folders"]).Where(f => !entryFolders.Contains(f)))
                {
                    problems.Add(new("edition-folders", $"'{id}' edition '{editionId}' installs '{folder}', which isn't one of the entry's folders."));
                }
                var current = (string)listed!["interface"]! == (string)edition["interface"]!;
                if ((bool)listed["upToDate"]! != current)
                {
                    problems.Add(new("edition-up-to-date", $"'{id}' edition '{editionId}' says upToDate {(bool)listed["upToDate"]!}, but it lists {listed["interface"]} and the edition is at {edition["interface"]}."));
                }
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addon in addons)
        {
            var id = (string)addon["id"]!;
            if (!ids.Add(id))
            {
                problems.Add(new("duplicate-id", $"'{id}' is used by more than one entry."));
            }
        }

        // Authors: one name each, profiles on GitHub, Wago or CurseForge (one per site), and every author used.
        var authors = root["authors"]!.AsObject();
        foreach (var (authorId, record) in authors)
        {
            var name = (string)record!["name"]!;
            if (NotOneName(name))
            {
                problems.Add(new("author-not-one-name", $"Author '{authorId}' name '{name}' should be one name: the add-on's main author."));
            }
            var sites = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in Strings(record["profiles"]))
            {
                var site = ProfileSite(profile);
                if (site is null)
                {
                    problems.Add(new("author-profile", $"Author '{authorId}' profile {profile} isn't on GitHub, Wago or CurseForge."));
                }
                else if (!sites.Add(site))
                {
                    problems.Add(new("author-profile", $"Author '{authorId}' has more than one {site} profile."));
                }
            }
        }
        var usedAuthors = new HashSet<string>(StringComparer.Ordinal);

        var folderOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in addons)
        {
            var id = (string)addon["id"]!;

            var author = (string)addon["author"]!;
            usedAuthors.Add(author);
            if (!authors.ContainsKey(author))
            {
                problems.Add(new("unknown-author", $"'{id}' author '{author}', which isn't in \"authors\"."));
            }

            // A real day, and not after today (a day's grace for time zones).
            if (addon["added"] is JsonValue addedValue)
            {
                var added = (string)addedValue!;
                if (!DateOnly.TryParseExact(added, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                {
                    problems.Add(new("added-date", $"'{id}' added '{added}' isn't a real date."));
                }
                else if (day > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
                {
                    problems.Add(new("added-date", $"'{id}' added {added} is in the future."));
                }
            }

            foreach (var field in new[] { "requires", "optional", "conflicts" })
            {
                foreach (var target in Strings(addon[field]))
                {
                    if (target == id)
                    {
                        problems.Add(new("self-reference", $"'{id}' lists itself in {field}."));
                    }
                    else if (!ids.Contains(target))
                    {
                        problems.Add(new("unknown-reference", $"'{id}' {field} '{target}', which isn't in the catalog."));
                    }
                }
            }

            var allFolders = Strings(addon["folders"]).Concat((addon["overrides"]?.AsArray() ?? []).SelectMany(o => Strings(o!["folders"])));
            foreach (var folder in allFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (folderOwners.TryGetValue(folder, out var owner) && owner != id)
                {
                    problems.Add(new("folder-claimed-twice", $"'{folder}' is claimed by both '{owner}' and '{id}'."));
                }
                else
                {
                    folderOwners[folder] = id;
                }
            }
        }
        // An own app's folder patterns cover only its own entry's folders and folders no entry has.
        foreach (var addon in addons)
        {
            var id = (string)addon["id"]!;
            foreach (var folder in Strings(addon["requiresFolders"]))
            {
                if (folderOwners.TryGetValue(folder, out var owner))
                {
                    problems.Add(new("requires-folder-listed", $"'{id}' requiresFolders '{folder}', which '{owner}' ships: it goes in requires."));
                }
            }
            foreach (var pattern in Strings(addon["ownApp"]?["folders"]))
            {
                var prefix = pattern.EndsWith('*') ? pattern[..^1] : null;
                foreach (var (folder, owner) in folderOwners)
                {
                    if (owner != id && (prefix is null ? string.Equals(folder, pattern, StringComparison.OrdinalIgnoreCase) : folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add(new("own-app-folder", $"'{id}' ownApp folder '{pattern}' covers '{folder}', which is '{owner}'s."));
                    }
                }
            }
        }
        foreach (var (authorId, _) in authors)
        {
            if (!usedAuthors.Contains(authorId))
            {
                problems.Add(new("unused-author", $"Author '{authorId}' isn't the author of any entry."));
            }
        }
        return Dedupe(problems);
    }

    /// <summary>versions.json (and test cases with "versions" in their name) rather than a catalog.</summary>
    public static bool IsVersionsFile(string path) => KindOf(path) == "versions";

    /// <summary>
    /// Which file this is, by its name: versions.json, optout.json, overrides/_authors.json, an override (in overrides/,
    /// or a test case with "override" in its name) or catalog.json.
    /// </summary>
    public static string KindOf(string path)
    {
        var name = Path.GetFileName(path);
        var inOverrides = Path.GetFileName(Path.GetDirectoryName(path)) == "overrides";
        return Path.GetFileName(Path.GetDirectoryName(path)) == "addons" || name.Contains("details-file", StringComparison.OrdinalIgnoreCase) ? "details"
            : name.Contains("versions", StringComparison.OrdinalIgnoreCase) ? "versions"
            : name.Contains("optout", StringComparison.OrdinalIgnoreCase) ? "optout"
            : name == "confirmed.json" || name.Contains("confirmed-file", StringComparison.OrdinalIgnoreCase) ? "confirmed"
            : name == "tags.json" || name.Contains("tags-file", StringComparison.OrdinalIgnoreCase) ? "tags"
            : name.Contains("_authors", StringComparison.OrdinalIgnoreCase) || name.Contains("curated-authors", StringComparison.OrdinalIgnoreCase) ? "authors"
            : inOverrides || name.Contains("override-file", StringComparison.OrdinalIgnoreCase) ? "override"
            : "catalog";
    }

    /// <summary>1.60.1 gives 16001, 12.1.5 gives 120105: major * 10000 + minor * 100 + patch.</summary>
    static string InterfaceOf(string gameVersion)
    {
        var parts = gameVersion.Split('.').Select(int.Parse).ToArray();
        return (parts[0] * 10000 + parts[1] * 100 + parts[2]).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// versions.json: one file listed under several editions (the same name in the same release) is the same file, so
    /// it has one hash and one link.
    /// </summary>
    static List<Problem> VersionsRules(JsonNode root)
    {
        var problems = new List<Problem>();
        foreach (var (id, byEdition) in root["addons"]!.AsObject())
        {
            var seen = new Dictionary<(string Release, string File), (string Sha, string Url, string Edition)>();
            foreach (var (editionId, release) in byEdition!.AsObject())
            {
                var key = ((string)release!["release"]!, (string)release["file"]!);
                var value = ((string)release["sha256"]!, (string)release["url"]!, editionId);
                if (seen.TryGetValue(key, out var first) && (first.Sha != value.Item1 || first.Url != value.Item2))
                {
                    problems.Add(new("versions-same-file", $"'{id}' lists {key.Item2} of {key.Item1} under '{first.Edition}' and '{editionId}' with a different hash or link."));
                }
                seen.TryAdd(key, value);
            }
        }
        return problems;
    }

    /// <summary>"GitHub", "Wago" or "CurseForge" for a profile on one of those sites; null for anywhere else.</summary>
    static string? ProfileSite(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.AbsolutePath.Trim('/').Length > 0
            ? uri.Host.ToLowerInvariant() switch
            {
                "github.com" => "GitHub",
                "addons.wago.io" => "Wago",
                "www.curseforge.com" or "curseforge.com" => "CurseForge",
                _ => null,
            }
            : null;

    /// <summary>A list or a group rather than one person: a comma, a semicolon, "&amp;", "and others" or "contributors".</summary>
    static bool NotOneName(string author) =>
        author.IndexOfAny([',', ';', '&']) >= 0
        || author.Contains(" and others", StringComparison.OrdinalIgnoreCase)
        || author.Contains("contributors", StringComparison.OrdinalIgnoreCase);

    // Keywords that only say "something below failed": the failure below is reported instead.
    static readonly HashSet<string> ContainerKeywords = ["properties", "items", "$ref", "allOf", "anyOf", "oneOf", "$defs"];

    /// <summary>
    /// The errors that explain a failure, from the hierarchical result: passing subtrees are skipped, container keywords
    /// are dropped, and for a failed "oneOf" (the source types) only the branch whose "type" matched is reported, or a
    /// short line when no branch's type matched.
    /// </summary>
    static IEnumerable<(string Location, string Message)> SchemaErrors(EvaluationResults node)
    {
        if (node.IsValid)
        {
            yield break;
        }
        foreach (var (keyword, message) in node.Errors ?? new Dictionary<string, string>())
        {
            if (!ContainerKeywords.Contains(keyword))
            {
                // The only enum is the tag list.
                yield return (node.InstanceLocation.ToString(), keyword == "enum" ? "isn't one of the catalog's tags (see schema/README.md)" : message);
            }
        }

        var children = node.Details ?? [];
        var branches = children.Where(c => LastSegments(c.EvaluationPath.ToString(), 2) is [ "oneOf", _ ]).ToList();
        if (branches.Count > 0)
        {
            var sameType = branches.Where(b => !b.IsValid && !FailsOn(b, "type")).ToList();
            if (sameType.Count == 0)
            {
                yield return (node.InstanceLocation.ToString(), "doesn't match any allowed shape (for a source: \"type\" must be github, curseforge or wago)");
            }
            foreach (var branch in sameType)
            {
                foreach (var error in SchemaErrors(branch))
                {
                    yield return error;
                }
            }
        }
        foreach (var child in children.Except(branches))
        {
            foreach (var error in SchemaErrors(child))
            {
                yield return error;
            }
        }
    }

    /// <summary>Whether a failing descendant of <paramref name="node"/> is the given property's own check.</summary>
    static bool FailsOn(EvaluationResults node, string property) =>
        !node.IsValid && ((LastSegments(node.EvaluationPath.ToString(), 2) is ["properties", var name] && name == property)
            || (node.Details ?? []).Any(d => FailsOn(d, property)));

    static string[] LastSegments(string pointer, int count)
    {
        var parts = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < count ? [] : parts[^count..];
    }

    // The schema library registers a schema by its $id and refuses to load it twice, so load each file once.
    static readonly Dictionary<string, JsonSchema> Schemas = new(StringComparer.OrdinalIgnoreCase);

    static JsonSchema LoadSchema(string path)
    {
        if (!Schemas.TryGetValue(path, out var schema))
        {
            schema = JsonSchema.FromFile(path);
            Schemas[path] = schema;
        }
        return schema;
    }

    static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => (string)n!) : [];

    /// <summary>"/addons/3/sources/0" becomes "addons[3] (questie) sources[0]".</summary>
    static string Where(JsonNode? root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/" || pointer == "#")
        {
            return "catalog";
        }
        var parts = pointer.Trim('#').Trim('/').Split('/');
        var text = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out var index) && text.Count > 0)
            {
                text[^1] += $"[{index}]";
                if (i == 1 && parts[0] == "addons" && root?["addons"]?[index]?["id"] is JsonValue id)
                {
                    text[^1] += $" ({id})";
                }
            }
            else
            {
                text.Add(parts[i]);
            }
        }
        return string.Join(' ', text);
    }

    static List<Problem> Dedupe(List<Problem> problems) => problems.Distinct().ToList();

    static string? FindSchema(string start, string name)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}

/// <summary>
/// Each file in scripts/test-data is a case. "valid-*.json" must pass. "invalid-&lt;code&gt;[-anything].json" must fail
/// with at least one problem whose code is &lt;code&gt; (for example invalid-duplicate-id.json expects "duplicate-id").
/// </summary>
static class SelfTest
{
    public static int Run()
    {
        var dir = FindTestData();
        if (dir is null)
        {
            Console.Error.WriteLine("No scripts/test-data folder found.");
            return 1;
        }

        var failures = 0;
        var cases = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var file in cases)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var problems = CatalogValidator.Validate(file);
            string verdict;
            if (name.StartsWith("valid-", StringComparison.Ordinal))
            {
                verdict = problems.Count == 0 ? "ok" : "expected valid, got " + string.Join("; ", problems.Select(p => $"{p.Code}: {p.Message}"));
            }
            else if (name.StartsWith("invalid-", StringComparison.Ordinal))
            {
                var expected = ExpectedCode(name["invalid-".Length..], problems);
                verdict = problems.Any(p => p.Code == expected)
                    ? "ok"
                    : $"expected '{expected}', got " + (problems.Count == 0 ? "no problems" : string.Join("; ", problems.Select(p => p.Code)));
            }
            else
            {
                verdict = "name must start with valid- or invalid-";
            }
            if (verdict != "ok")
            {
                failures++;
                Console.WriteLine($"FAIL {name}  {verdict}");
            }
        }
        var bumpDir = Path.Combine(dir, "bump");
        var bumpCases = Directory.Exists(bumpDir)
            ? Directory.GetFiles(bumpDir, "*.head.json").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
        foreach (var head in bumpCases)
        {
            var name = Path.GetFileName(head)[..^".head.json".Length];
            var problems = VersionBump.Check(head[..^".head.json".Length] + ".base.json", head);
            var expected = name.StartsWith("invalid-", StringComparison.Ordinal) ? BumpCode(name["invalid-".Length..]) : null;
            var ok = expected is not null ? problems.Any(p => p.Code == expected) : problems.Count == 0;
            if (!ok)
            {
                failures++;
                Console.WriteLine($"FAIL bump/{name}  got " + (problems.Count == 0 ? "no problems" : string.Join("; ", problems.Select(p => p.Code))));
            }
        }
        var formProblem = IssueFormTags.Compare(Path.GetFullPath(Path.Combine(dir, "..", "..")));
        if (formProblem is not null)
        {
            failures++;
            Console.WriteLine($"FAIL issue-form-tags  {formProblem}");
        }
        var total = cases.Count + bumpCases.Count + 1;
        Console.WriteLine($"{total - failures}/{total} cases passed.");
        return failures == 0 && cases.Count > 0 ? 0 : 1;
    }

    // "duplicate-id" and "duplicate-id-in-overrides" both expect "duplicate-id": the longest known code that prefixes the name.
    static readonly string[] Codes = ["schema", "json", "details-html", "details-link", "details-id", "details-unlisted", "duplicate-id", "unknown-reference", "self-reference", "folder-claimed-twice", "author-not-one-name", "added-date", "released-date",
        "unknown-author", "unused-author", "author-profile", "edition-interface", "unknown-edition", "edition-up-to-date",
        "versions-same-file", "test-client-edition", "edition-facts", "override-name", "own-app-folder", "requires-folder-listed", "edition-folders", "confirmed-edition"];

    // bump/invalid-added-missing-*.head.json expects "added-missing"; any other invalid bump case "version-not-bumped".
    static string BumpCode(string rest) =>
        rest.StartsWith("added-missing", StringComparison.Ordinal) ? "added-missing" : "version-not-bumped";

    static string ExpectedCode(string rest, List<Problem> _) =>
        Codes.Where(c => rest == c || rest.StartsWith(c + "-", StringComparison.Ordinal)).OrderByDescending(c => c.Length).FirstOrDefault() ?? rest;

    static string? FindTestData()
    {
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "scripts", "test-data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}

/// <summary>
/// The "Add an add-on" form's Tags dropdown must offer exactly the schema's tags, in the same order. Each option reads
/// "&lt;tag&gt;: &lt;what it's for&gt;".
/// </summary>
static class IssueFormTags
{
    /// <summary>Null when they match, else what's wrong.</summary>
    public static string? Compare(string repoRoot)
    {
        var schemaPath = Path.Combine(repoRoot, "schema", "catalog.schema.json");
        var formPath = Path.Combine(repoRoot, ".github", "ISSUE_TEMPLATE", "addon.yml");
        if (!File.Exists(schemaPath) || !File.Exists(formPath))
        {
            return $"needs {schemaPath} and {formPath}";
        }
        var schemaTags = (JsonNode.Parse(File.ReadAllText(schemaPath))?["$defs"]?["tag"]?["enum"]?.AsArray() ?? [])
            .Select(t => (string)t!).ToList();
        var formTags = FormOptions(File.ReadAllLines(formPath)).Select(o => o.Split(':')[0].Trim()).ToList();
        return schemaTags.Count > 0 && schemaTags.SequenceEqual(formTags)
            ? null
            : $"the schema has [{string.Join(", ", schemaTags)}] but the form offers [{string.Join(", ", formTags)}]";
    }

    /// <summary>The option lines of the dropdown whose id is "tags", without their quotes.</summary>
    static IEnumerable<string> FormOptions(string[] lines)
    {
        var inTags = false;
        var inOptions = false;
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.StartsWith("- type:", StringComparison.Ordinal))
            {
                inTags = false;
                inOptions = false;
            }
            else if (text == "id: tags")
            {
                inTags = true;
            }
            else if (inTags && text == "options:")
            {
                inOptions = true;
            }
            else if (inOptions && text.StartsWith("- ", StringComparison.Ordinal))
            {
                yield return text[2..].Trim().Trim('"');
            }
            else if (inOptions && text.Length > 0)
            {
                inOptions = false;
            }
        }
    }
}

/// <summary>
/// On a PR, against the base branch's catalog: when the entries or the authors changed, the version must go up,
/// and every new entry must have "added". A missing or empty base file (the first catalog) needs neither.
/// </summary>
static class VersionBump
{
    public static List<Problem> Check(string basePath, string headPath)
    {
        if (!File.Exists(basePath) || new FileInfo(basePath).Length == 0)
        {
            return [];
        }
        JsonNode? before;
        try
        {
            before = JsonNode.Parse(File.ReadAllText(basePath));
        }
        catch (JsonException)
        {
            // The base was broken; the head has just passed validation, so there's nothing to compare.
            return [];
        }
        var after = JsonNode.Parse(File.ReadAllText(headPath))!;

        var changed = !JsonNode.DeepEquals(before?["addons"], after["addons"])
            || !JsonNode.DeepEquals(before?["authors"], after["authors"])
            || !JsonNode.DeepEquals(before?["editions"], after["editions"]);
        if (!changed)
        {
            return [];
        }
        var problems = new List<Problem>();
        var oldVersion = Semver((string?)before?["version"]);
        var newVersion = Semver((string?)after["version"]);
        if (oldVersion is not null && newVersion is not null && newVersion.CompareTo(oldVersion) <= 0)
        {
            problems.Add(new("version-not-bumped", $"Entries or authors changed but version stayed at {after["version"]} (was {before?["version"]}). Bump it."));
        }

        // A new entry says when it joined.
        var oldIds = (before?["addons"] as JsonArray ?? []).Select(a => (string?)a?["id"]).ToHashSet(StringComparer.Ordinal);
        foreach (var addon in after["addons"] as JsonArray ?? [])
        {
            var id = (string?)addon?["id"];
            if (id is not null && !oldIds.Contains(id) && addon?["added"] is null)
            {
                problems.Add(new("added-missing", $"'{id}' is new, so it needs \"added\": the day it joined the catalog."));
            }
        }
        return problems;
    }

    static Version? Semver(string? text) => Version.TryParse(text, out var v) ? v : null;
}
