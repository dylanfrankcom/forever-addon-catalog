#:package JsonSchema.Net@9.7.0
#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Validates catalog.json: the schema, plus the rules a schema can't express.
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

var path = Path.GetFullPath(catalogArg ?? "catalog.json");
var problems = CatalogValidator.Validate(path);
if (problems.Count == 0 && baseArg is not null)
{
    problems.AddRange(VersionBump.Check(Path.GetFullPath(baseArg), path));
}
foreach (var p in problems)
{
    Console.Error.WriteLine($"{p.Code}: {p.Message}");
}
Console.WriteLine(problems.Count == 0 ? $"{Path.GetFileName(path)} is valid." : $"{problems.Count} problem(s) in {Path.GetFileName(path)}.");
return problems.Count == 0 ? 0 : 1;

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

        var schemaPath = FindSchema(Path.GetDirectoryName(catalogPath)!);
        if (schemaPath is null)
        {
            problems.Add(new("missing-schema", $"No schema/catalog.schema.json above {catalogPath}."));
            return problems;
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
        var addons = root!["addons"]!.AsArray().Select(a => a!.AsObject()).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addon in addons)
        {
            var id = (string)addon["id"]!;
            if (!ids.Add(id))
            {
                problems.Add(new("duplicate-id", $"'{id}' is used by more than one entry."));
            }
        }

        var folderOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in addons)
        {
            var id = (string)addon["id"]!;

            // One name: the add-on's main author, not a list of contributors or a group.
            var author = (string)addon["author"]!;
            if (NotOneName(author))
            {
                problems.Add(new("author-not-one-name", $"'{id}' author '{author}' should be one name: the add-on's main author."));
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
        return Dedupe(problems);
    }

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

    static string? FindSchema(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schema", "catalog.schema.json");
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
    static readonly string[] Codes = ["schema", "json", "duplicate-id", "unknown-reference", "self-reference", "folder-claimed-twice", "author-not-one-name", "added-date"];

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
/// On a PR, against the base branch's catalog: when the entries or the forever block changed, the version must go up,
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
            || !JsonNode.DeepEquals(before?["forever"], after["forever"]);
        if (!changed)
        {
            return [];
        }
        var problems = new List<Problem>();
        var oldVersion = Semver((string?)before?["version"]);
        var newVersion = Semver((string?)after["version"]);
        if (oldVersion is not null && newVersion is not null && newVersion.CompareTo(oldVersion) <= 0)
        {
            problems.Add(new("version-not-bumped", $"Entries changed but version stayed at {after["version"]} (was {before?["version"]}). Bump it."));
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
