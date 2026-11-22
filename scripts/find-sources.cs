#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// Looks for an add-on's other sources on GitHub and writes a report for a person to confirm. It never edits catalog.json.
//
// Run it through the app repo's dev.ps1 (see scripts/README.md), so NuGet stays in the dev tools folder:
//   ... dotnet run scripts/find-sources.cs                          # every entry in ./catalog.json
//   ... dotnet run scripts/find-sources.cs -- --id questie          # one entry
//   ... dotnet run scripts/find-sources.cs -- --folder GatherLite   # a suggested add-on, by the folder it installs
//   ... dotnet run scripts/find-sources.cs -- --self-test           # offline tests of the matching rules
// Options: --catalog <file> (default ./catalog.json), --summary <file> (also append the report there, as the workflow does
// with $GITHUB_STEP_SUMMARY). GITHUB_TOKEN is used when set; without it GitHub's code search is skipped.
//
// Only GitHub is contacted: the latest release of each candidate repository is downloaded and its .toc read (the zip is
// never extracted or run). CurseForge and Wago IDs come from the .toc's X-Curse-Project-ID, X-Wago-ID and X-WoWI-ID fields.
// Exit code 0 when the report was written, 1 when the catalog can't be read, 2 for bad arguments.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    return SelfTest.Run();
}

string? id = null, folder = null, catalogArg = null, summaryArg = null;
for (var i = 0; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--id" when value is not null: id = value.Trim(); i++; break;
        case "--folder" when value is not null: folder = value.Trim(); i++; break;
        case "--catalog" when value is not null: catalogArg = value; i++; break;
        case "--summary" when value is not null: summaryArg = value; i++; break;
        default:
            Console.Error.WriteLine("Usage: dotnet run scripts/find-sources.cs [--id <id> | --folder <Folder>] [--catalog <file>] [--summary <file>] | --self-test");
            return 2;
    }
}
if (id == "") id = null;
if (folder == "") folder = null;
if (id is not null && folder is not null)
{
    Console.Error.WriteLine("Give --id or --folder, not both.");
    return 2;
}
if (folder is not null && !Rules.IsFolderName(folder))
{
    Console.Error.WriteLine($"Not a folder name: {folder}");
    return 2;
}

List<Target> all;
string foreverInterface;
try
{
    var catalog = JsonNode.Parse(File.ReadAllText(Path.GetFullPath(catalogArg ?? "catalog.json")))!;
    foreverInterface = catalog["forever"]?["interface"]?.GetValue<string>() ?? "";
    all = catalog["addons"]!.AsArray().Select(a => Target.FromEntry(a!)).ToList();
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
{
    Console.Error.WriteLine($"Can't read the catalog: {e.Message}");
    return 1;
}

var report = new StringBuilder();
List<Target> targets;
if (folder is not null)
{
    var listed = all.FirstOrDefault(t => t.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase));
    if (listed is not null)
    {
        report.AppendLine($"{Md.Code(folder)} is already in the catalog, as {Md.Code(listed.Id!)}.").AppendLine();
    }
    targets = [listed ?? Target.Suggested(folder)];
}
else
{
    targets = id is null ? all : all.Where(t => t.Id == id).ToList();
    if (targets.Count == 0)
    {
        Console.Error.WriteLine($"No catalog entry with the ID {id}.");
        return 2;
    }
}

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
using var github = new GitHub(string.IsNullOrWhiteSpace(token) ? null : token);
report.Insert(0, "## Other sources\n\nFound on GitHub for a person to confirm; nothing here changes the catalog. " +
    "CurseForge and Wago aren't contacted: their IDs come from the `.toc` of each release read.\n\n");
if (!github.HasToken)
{
    report.AppendLine("No `GITHUB_TOKEN`, so GitHub's code search was skipped.").AppendLine();
}
foreach (var target in targets)
{
    Console.Error.WriteLine($"Looking for {target.Label} ...");
    var candidates = await Finder.FindAsync(github, target, foreverInterface);
    Report.Write(report, target, candidates, foreverInterface);
}

Console.Write(report);
if (summaryArg is not null)
{
    File.AppendAllText(summaryArg, report.ToString());
}
return 0;

/// <summary>An add-on to look for: a catalog entry, or a suggested add-on known only by its folder.</summary>
record Target(
    string? Id,
    string Name,
    string MainFolder,
    IReadOnlyList<string> Folders,
    string? TocPath,
    IReadOnlyList<string> GitHubRepos,
    IReadOnlyList<long> CurseForgeIds,
    IReadOnlyList<string> WagoSlugs,
    string? RepositoryRepo)
{
    public string Label => Id is null ? $"suggested add-on {MainFolder}" : $"{Id} ({Name})";

    public static Target Suggested(string folder) => new(null, folder, folder, [folder], null, [], [], [], null);

    public static Target FromEntry(JsonNode entry)
    {
        var folders = entry["folders"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        var tocPath = entry["tocPath"]?.GetValue<string>();
        var sources = entry["sources"]!.AsArray().Select(s => s!).ToList();
        string[] Of(string type, string field) =>
            sources.Where(s => s["type"]?.GetValue<string>() == type).Select(s => s[field]!.ToString()).ToArray();
        return new(
            entry["id"]!.GetValue<string>(),
            entry["name"]!.GetValue<string>(),
            Rules.MainFolder(tocPath, folders),
            folders,
            tocPath,
            Of("github", "repo"),
            Of("curseforge", "projectId").Select(long.Parse).ToArray(),
            Of("wago", "slug"),
            Rules.RepoFromUrl(entry["repository"]?.GetValue<string>()));
    }
}

/// <summary>What the latest release of a repository holds, or why it couldn't be read.</summary>
record ReleaseRead(
    string? Tag,
    string? AssetName,
    IReadOnlyList<string> TopFolders,
    string? TocName,
    IReadOnlyDictionary<string, string> Fields,
    bool ListsForever,
    string? Problem)
{
    public static ReleaseRead Failed(string problem) =>
        new(null, null, [], null, new Dictionary<string, string>(), false, problem);
}

/// <summary>A repository that might publish the add-on, with how it was found.</summary>
record Candidate(string Repo, string FoundBy, ReleaseRead Release, Verdict Verdict);

enum Verdict
{
    /// <summary>Already a GitHub source of the entry.</summary>
    Listed,
    /// <summary>Ships the add-on's folder and something ties it to the entry (its repository link or CurseForge ID).</summary>
    SameAddon,
    /// <summary>Ships the add-on's folder, nothing else ties it to the entry.</summary>
    Possible,
    /// <summary>No release, or the release doesn't ship the folder.</summary>
    NotIt,
}

/// <summary>The matching rules, kept free of I/O so the self-test can check them.</summary>
static class Rules
{
    static readonly Regex FolderName = new("^[A-Za-z0-9_][A-Za-z0-9_.-]*$");
    static readonly Regex RepoUrl = new(@"^https://github\.com/([A-Za-z0-9-]+/[A-Za-z0-9._-]+?)(?:\.git)?/?$");

    public static bool IsFolderName(string s) => s.Length <= 100 && FolderName.IsMatch(s);

    /// <summary>The folder whose .toc matters: tocPath's first part, else the first folder.</summary>
    public static string MainFolder(string? tocPath, IReadOnlyList<string> folders) =>
        tocPath is not null && tocPath.Contains('/') ? tocPath[..tocPath.IndexOf('/')] : folders[0];

    public static string? RepoFromUrl(string? url) => url is null ? null : RepoUrl.Match(url) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The <c>## Key: Value</c> lines of a .toc; the first of a key wins, keys ignore case.</summary>
    public static Dictionary<string, string> ParseToc(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
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
            if (key.Length > 0)
            {
                fields.TryAdd(key, line[(colon + 1)..].Trim());
            }
        }
        return fields;
    }

    public static bool ListsInterface(IReadOnlyDictionary<string, string> fields, string iface) =>
        iface.Length > 0 && fields.TryGetValue("Interface", out var v) && v.Split(',').Select(s => s.Trim()).Contains(iface);

    static string Norm(string entryName) => entryName.Replace('\\', '/');

    /// <summary>The top-level folders of a zip, from its entry names.</summary>
    public static List<string> TopFolders(IEnumerable<string> entryNames) =>
        entryNames.Select(Norm).Where(n => n.Contains('/')).Select(n => n[..n.IndexOf('/')])
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The .toc to read, as the catalog does: the entry's tocPath, else <c>Folder/Folder_Forever.toc</c>, <c>Folder-Forever.toc</c>,
    /// <c>Folder.toc</c>, else any .toc directly in the folder. Returns the zip's own spelling of the name.
    /// </summary>
    public static string? PickToc(IReadOnlyList<string> entryNames, string folder, string? tocPath)
    {
        string? Find(string wanted) => entryNames.FirstOrDefault(n => Norm(n).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (tocPath is not null && Find(tocPath) is { } exact)
        {
            return exact;
        }
        foreach (var suffix in new[] { "_Forever.toc", "-Forever.toc", ".toc" })
        {
            if (Find($"{folder}/{folder}{suffix}") is { } found)
            {
                return found;
            }
        }
        return entryNames.FirstOrDefault(n =>
        {
            var parts = Norm(n).Split('/');
            return parts.Length == 2 && parts[0].Equals(folder, StringComparison.OrdinalIgnoreCase)
                && parts[1].EndsWith(".toc", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The release asset to read: a .zip, preferring one named after the folder and not a "nolib" build.</summary>
    public static string? PickAsset(IEnumerable<string> assetNames, string folder) =>
        assetNames.Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Contains(folder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n.Contains("nolib", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    /// <summary>An assetPattern to start from: the name up to its version, then anything, then .zip.</summary>
    public static string SuggestAssetPattern(string assetName)
    {
        var stem = Path.GetFileNameWithoutExtension(assetName);
        var m = Regex.Match(stem, @"^(.+?)[-_. ]?[vV]?\d");
        var prefix = m.Success ? m.Groups[1].Value : stem;
        return "^" + Regex.Escape(prefix) + @".*\.zip$";
    }

    public static Verdict Judge(Target target, string repo, ReleaseRead release)
    {
        if (target.GitHubRepos.Contains(repo, StringComparer.OrdinalIgnoreCase))
        {
            return Verdict.Listed;
        }
        if (release.Problem is not null || !release.TopFolders.Contains(target.MainFolder, StringComparer.OrdinalIgnoreCase))
        {
            return Verdict.NotIt;
        }
        // Only the entry's own repository link ties a repository to the author: mirrors and forks declare the same CurseForge
        // project too, so a matching ID alone stays "possible".
        return string.Equals(repo, target.RepositoryRepo, StringComparison.OrdinalIgnoreCase) ? Verdict.SameAddon : Verdict.Possible;
    }

    public static string Owner(string repo) => repo[..repo.IndexOf('/')];

    public static long? CurseForgeId(IReadOnlyDictionary<string, string> fields) =>
        fields.TryGetValue("X-Curse-Project-ID", out var v) && long.TryParse(v, out var id) && id > 0 ? id : null;

    /// <summary>What a release's .toc says about the add-on's other sources, compared with the entry.</summary>
    public static List<string> DeclaredNotes(Target target, IReadOnlyDictionary<string, string> fields)
    {
        var notes = new List<string>();
        if (CurseForgeId(fields) is { } cf)
        {
            notes.Add(target.CurseForgeIds.Contains(cf)
                ? $"CurseForge project {cf} matches the entry."
                : $"Declares CurseForge project {cf}, which the entry doesn't list.");
        }
        if (fields.TryGetValue("X-Wago-ID", out var wago) && wago.Length > 0)
        {
            notes.Add(target.WagoSlugs.Count == 0
                ? $"Declares Wago project {Md.Code(wago)}; the entry has no Wago source."
                : $"Declares Wago project {Md.Code(wago)} (the entry lists the slug {Md.Code(target.WagoSlugs[0])}: check they're the same add-on).");
        }
        if (fields.TryGetValue("X-WoWI-ID", out var wowi) && wowi.Length > 0)
        {
            notes.Add($"Declares WoWInterface ID {Md.Code(wowi)} (the catalog has no WoWInterface sources).");
        }
        return notes;
    }
}

/// <summary>Markdown for text from outside (repositories, assets, .toc fields): always inside a code span, so it can't add links or images.</summary>
static class Md
{
    public static string Code(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray()).Replace('`', '\'').Trim();
        if (clean.Length > 100)
        {
            clean = clean[..100] + "…";
        }
        return clean.Length == 0 ? "`(empty)`" : $"`{clean}`";
    }
}

static class Finder
{
    const int MaxSearched = 6;

    public static async Task<List<Candidate>> FindAsync(GitHub github, Target target, string foreverInterface)
    {
        // Repository -> how it was found, in the order to check them.
        var repos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in target.GitHubRepos)
        {
            repos.TryAdd(r, "listed source");
        }
        if (target.RepositoryRepo is { } linked)
        {
            repos.TryAdd(linked, "the entry's repository link");
        }
        var searched = 0;
        foreach (var r in await github.SearchCodeAsync(target.MainFolder))
        {
            if (searched < MaxSearched && repos.TryAdd(r, $"has {target.MainFolder}.toc"))
            {
                searched++;
            }
        }
        foreach (var r in await github.SearchRepositoriesAsync(target.MainFolder))
        {
            if (searched < MaxSearched && repos.TryAdd(r, "name matches"))
            {
                searched++;
            }
        }

        var candidates = new List<Candidate>();
        foreach (var (repo, foundBy) in repos)
        {
            var release = await github.ReadLatestAsync(repo, target, foreverInterface);
            candidates.Add(new(repo, foundBy, release, Rules.Judge(target, repo, release)));
        }
        return candidates;
    }
}

static class Report
{
    public static void Write(StringBuilder md, Target target, List<Candidate> candidates, string foreverInterface)
    {
        md.AppendLine(target.Id is null ? $"### Suggested add-on {Md.Code(target.MainFolder)}" : $"### {Md.Code(target.Id)}: {Md.Code(target.Name)}");
        var listed = new List<string>();
        listed.AddRange(target.GitHubRepos.Select(r => $"GitHub {Md.Code(r)}"));
        listed.AddRange(target.CurseForgeIds.Select(c => $"CurseForge {c}"));
        listed.AddRange(target.WagoSlugs.Select(w => $"Wago {Md.Code(w)}"));
        if (listed.Count > 0)
        {
            md.AppendLine($"Listed: {string.Join(", ", listed)}.").AppendLine();
        }
        else
        {
            md.AppendLine();
        }

        var shown = candidates.Where(c => c.Verdict != Verdict.NotIt || c.FoundBy != "name matches" && !c.FoundBy.StartsWith("has ")).ToList();
        foreach (var c in shown.OrderBy(c => c.Verdict))
        {
            var r = c.Release;
            var verdict = c.Verdict switch
            {
                Verdict.Listed => "already listed",
                Verdict.SameAddon => "**the same add-on**",
                Verdict.Possible => "**possibly the same add-on**",
                _ => "not usable",
            };
            md.Append($"- {Md.Code(c.Repo)} ({c.FoundBy}): {verdict}. ");
            if (r.Problem is not null)
            {
                md.AppendLine(r.Problem);
                continue;
            }
            md.Append($"Latest release {Md.Code(r.Tag ?? "")}, asset {Md.Code(r.AssetName ?? "")}");
            md.Append(r.TocName is null ? ", no .toc found" : $", {Md.Code(r.TocName)} ");
            if (r.TocName is not null)
            {
                md.Append(r.ListsForever ? $"lists the Forever Interface ({foreverInterface})" : $"doesn't list the Forever Interface ({foreverInterface})");
            }
            md.AppendLine(".");
            if (c.Verdict == Verdict.NotIt)
            {
                md.AppendLine($"  - Its zip doesn't hold {Md.Code(target.MainFolder)}.");
                continue;
            }
            foreach (var note in Rules.DeclaredNotes(target, r.Fields))
            {
                md.AppendLine($"  - {note}");
            }
            if (c.Verdict == Verdict.Possible)
            {
                md.AppendLine(target.RepositoryRepo is { } link && !Rules.Owner(link).Equals(Rules.Owner(c.Repo), StringComparison.OrdinalIgnoreCase)
                    ? $"  - Not the entry's repository ({Md.Code(link)}): check it's the author's own and not a mirror or fork."
                    : "  - Check it's published by the add-on's author, not a mirror or fork.");
            }
            if (c.Verdict is Verdict.SameAddon or Verdict.Possible && target.GitHubRepos.Count == 0 && r.AssetName is not null)
            {
                if (!r.ListsForever)
                {
                    md.AppendLine("  - The entry has no GitHub source, but this release doesn't list the Forever Interface, so it can't be one yet.");
                    continue;
                }
                md.AppendLine($"  - **Candidate for one-click installs:** the entry has no GitHub source. A source to start from (check the asset pattern against older releases):");
                md.AppendLine("    ```json");
                md.AppendLine($"    {{ \"type\": \"github\", \"repo\": \"{c.Repo}\", \"assetPattern\": \"{Rules.SuggestAssetPattern(r.AssetName).Replace("\\", "\\\\")}\" }}");
                md.AppendLine("    ```");
            }
        }

        var skipped = candidates.Except(shown).ToList();
        if (skipped.Count > 0)
        {
            md.AppendLine($"- Also looked at, without a release holding {Md.Code(target.MainFolder)}: {string.Join(", ", skipped.Select(c => Md.Code(c.Repo)))}.");
        }
        if (candidates.Count == 0)
        {
            md.AppendLine("- Nothing found on GitHub.");
        }
        md.AppendLine();
    }
}

sealed class GitHub : IDisposable
{
    const long MaxZipBytes = 200 * 1024 * 1024;
    const int MaxTocBytes = 64 * 1024;
    static readonly TimeSpan CodeSearchGap = TimeSpan.FromSeconds(7); // code search allows 10 requests a minute

    readonly HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
    DateTime lastCodeSearch = DateTime.MinValue;

    public bool HasToken { get; }

    public GitHub(string? token)
    {
        HasToken = token is not null;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("forever-addon-catalog-find-sources");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    async Task<(HttpStatusCode Status, JsonNode? Json)> GetAsync(string path)
    {
        try
        {
            using var response = await http.GetAsync(path);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, response.IsSuccessStatusCode ? JsonNode.Parse(text) : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"  {path}: {e.Message}");
            return (0, null);
        }
    }

    /// <summary>Repositories (not forks) that hold a file named <c>Folder.toc</c>.</summary>
    public async Task<List<string>> SearchCodeAsync(string folder)
    {
        if (!HasToken)
        {
            return [];
        }
        var wait = lastCodeSearch + CodeSearchGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        lastCodeSearch = DateTime.UtcNow;
        var (status, json) = await GetAsync($"search/code?per_page=30&q={Uri.EscapeDataString($"filename:{folder}.toc")}");
        if (json is null)
        {
            Console.Error.WriteLine($"  Code search for {folder}.toc failed ({(int)status}).");
            return [];
        }
        return json["items"]!.AsArray()
            .Select(i => i!["repository"]!)
            .Where(r => r["fork"]?.GetValue<bool>() != true)
            .Select(r => r["full_name"]!.GetValue<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Repositories (not forks or archived) whose name contains the folder's name, most starred first.</summary>
    public async Task<List<string>> SearchRepositoriesAsync(string folder)
    {
        var (status, json) = await GetAsync($"search/repositories?per_page=5&sort=stars&q={Uri.EscapeDataString($"{folder} in:name fork:false archived:false")}");
        if (json is null)
        {
            Console.Error.WriteLine($"  Repository search for {folder} failed ({(int)status}).");
            return [];
        }
        return json["items"]!.AsArray().Select(r => r!["full_name"]!.GetValue<string>()).ToList();
    }

    /// <summary>Downloads the latest release's zip (GitHub never flags a pre-release as Latest) and reads its .toc.</summary>
    public async Task<ReleaseRead> ReadLatestAsync(string repo, Target target, string foreverInterface)
    {
        var (status, json) = await GetAsync($"repos/{repo}/releases/latest");
        if (status == HttpStatusCode.NotFound)
        {
            return ReleaseRead.Failed("No published release.");
        }
        if (json is null)
        {
            return ReleaseRead.Failed($"Couldn't read its releases ({(int)status}).");
        }
        var tag = json["tag_name"]?.GetValue<string>() ?? "";
        var assets = json["assets"]!.AsArray().Select(a => a!).ToList();
        var name = Rules.PickAsset(assets.Select(a => a["name"]!.GetValue<string>()), target.MainFolder);
        if (name is null)
        {
            return ReleaseRead.Failed($"Latest release {Md.Code(tag)} has no .zip asset.");
        }
        var asset = assets.First(a => a["name"]!.GetValue<string>() == name);
        if (asset["size"]!.GetValue<long>() > MaxZipBytes)
        {
            return ReleaseRead.Failed($"Latest release {Md.Code(tag)}: {Md.Code(name)} is over {MaxZipBytes / 1024 / 1024} MB, not read.");
        }

        try
        {
            await using var zipStream = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            using (var download = await http.GetStreamAsync(asset["browser_download_url"]!.GetValue<string>()))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await download.ReadAsync(buffer)) > 0)
                {
                    if (zipStream.Length + read > MaxZipBytes)
                    {
                        return ReleaseRead.Failed($"{Md.Code(name)} is over {MaxZipBytes / 1024 / 1024} MB, not read.");
                    }
                    await zipStream.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            zipStream.Position = 0;
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            var tocName = Rules.PickToc(names, target.MainFolder, target.TocPath);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var listsForever = false;
            if (tocName is not null)
            {
                var chosen = Rules.ParseToc(ReadText(zip.GetEntry(tocName)!));
                listsForever = Rules.ListsInterface(chosen, foreverInterface);
                // The IDs may sit in another flavour's .toc of the same folder; the chosen one wins.
                foreach (var other in names.Where(n => n != tocName && Rules.PickToc([n], target.MainFolder, null) == n))
                {
                    foreach (var (k, v) in Rules.ParseToc(ReadText(zip.GetEntry(other)!)))
                    {
                        chosen.TryAdd(k, v);
                    }
                }
                fields = chosen;
            }
            return new(tag, name, Rules.TopFolders(names), tocName, fields, listsForever, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            return ReleaseRead.Failed($"Couldn't read {Md.Code(name)}: {e.GetType().Name}.");
        }
    }

    static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new byte[MaxTocBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    public void Dispose() => http.Dispose();
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

    static Target Entry(string folder, string[]? github = null, long[]? curseforge = null, string[]? wago = null, string? repository = null) =>
        new("x", "X", folder, [folder], null, github ?? [], curseforge ?? [], wago ?? [], repository);

    static ReleaseRead Release(string[] topFolders, Dictionary<string, string>? fields = null) =>
        new("v1", "X-v1.zip", topFolders, "X/X.toc", fields ?? new(StringComparer.OrdinalIgnoreCase), true, null);

    public static int Run()
    {
        var toc = Rules.ParseToc("## Interface: 11507, 16001\r\n# a comment\n## Title: Questie\n## x-curse-project-id: 334372\n## Title: Second\n##NoColon\n## X-Wago-ID: 6b6Wd0K3\n");
        Check("toc: fields", toc["Title"] == "Questie" && toc["X-Curse-Project-ID"] == "334372" && toc["X-Wago-ID"] == "6b6Wd0K3");
        Check("toc: no colon skipped", !toc.ContainsKey("NoColon"));
        Check("toc: lists interface", Rules.ListsInterface(toc, "16001"));
        Check("toc: doesn't list interface", !Rules.ListsInterface(toc, "16002"));
        Check("toc: empty interface", !Rules.ListsInterface(toc, ""));

        string[] zip = ["Questie/", "Questie/Questie.toc", "Questie/Questie_Forever.toc", "Questie/Questie_Vanilla.toc", "QuestieDB/QuestieDB.toc", "README.md"];
        Check("toc: tocPath wins", Rules.PickToc(zip, "Questie", "Questie/Questie_Vanilla.toc") == "Questie/Questie_Vanilla.toc");
        Check("toc: _Forever before plain", Rules.PickToc(zip, "Questie", null) == "Questie/Questie_Forever.toc");
        Check("toc: missing tocPath falls back", Rules.PickToc(zip, "Questie", "Questie/Gone.toc") == "Questie/Questie_Forever.toc");
        Check("toc: folder ignores case", Rules.PickToc(["questie/questie.toc"], "Questie", null) == "questie/questie.toc");
        Check("toc: any toc in the folder", Rules.PickToc(["Foo/Bar.toc", "Foo/Sub/Foo.toc"], "Foo", null) == "Foo/Bar.toc");
        Check("toc: none", Rules.PickToc(["Other/Other.toc"], "Foo", null) is null);
        Check("toc: backslashes", Rules.PickToc(["Foo\\Foo.toc"], "Foo", null) == "Foo\\Foo.toc");

        var top = Rules.TopFolders(zip);
        Check("zip: top folders", top.SequenceEqual(["Questie", "QuestieDB"]));

        Check("asset: named after the folder", Rules.PickAsset(["Other.zip", "notes.txt", "GatherLite-v2.zip"], "GatherLite") == "GatherLite-v2.zip");
        Check("asset: not nolib", Rules.PickAsset(["Foo-1.0-nolib.zip", "Foo-1.0.zip"], "Foo") == "Foo-1.0.zip");
        Check("asset: only zips", Rules.PickAsset(["Foo.tar.gz", "Foo.rar"], "Foo") is null);

        foreach (var (asset, expected) in new[]
        {
            ("GatherLite-v2.1.3.zip", @"^GatherLite.*\.zip$"),
            ("Questie-v11.2.3.zip", @"^Questie.*\.zip$"),
            ("OpenMap.zip", @"^OpenMap.*\.zip$"),
            ("Blizz Move 4.zip", @"^Blizz\ Move.*\.zip$"),
            ("2Foo.zip", @"^2Foo.*\.zip$"),
        })
        {
            var pattern = Rules.SuggestAssetPattern(asset);
            Check($"pattern: {asset} -> {pattern}", pattern == expected && Regex.IsMatch(asset, pattern));
        }

        Check("repo url", Rules.RepoFromUrl("https://github.com/Wuild/GatherLite") == "Wuild/GatherLite");
        Check("repo url: .git and slash", Rules.RepoFromUrl("https://github.com/a/b.git") == "a/b" && Rules.RepoFromUrl("https://github.com/a/b/") == "a/b");
        Check("repo url: not GitHub", Rules.RepoFromUrl("https://gitlab.com/a/b") is null && Rules.RepoFromUrl("https://github.com/a/b/issues") is null);
        Check("main folder", Rules.MainFolder("Questie/Questie_Forever.toc", ["QuestieDB", "Questie"]) == "Questie" && Rules.MainFolder(null, ["A", "B"]) == "A");
        Check("folder names", Rules.IsFolderName("GatherLite") && Rules.IsFolderName("Foo_Bar.Baz") && !Rules.IsFolderName("a/b") && !Rules.IsFolderName("..") && !Rules.IsFolderName("x y"));

        var cf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Curse-Project-ID"] = "334918" };
        Check("judge: listed", Rules.Judge(Entry("Foo", github: ["a/Foo"]), "A/foo", Release(["Other"])) == Verdict.Listed);
        Check("judge: repository link", Rules.Judge(Entry("GatherLite", repository: "Wuild/GatherLite"), "wuild/gatherlite", Release(["GatherLite"])) == Verdict.SameAddon);
        Check("judge: same CurseForge ID is only possible (mirrors declare it too)", Rules.Judge(Entry("GatherLite", curseforge: [334918]), "b/c", Release(["GatherLite"], cf)) == Verdict.Possible);
        Check("judge: other CurseForge ID", Rules.Judge(Entry("GatherLite", curseforge: [1]), "b/c", Release(["gatherlite"], cf)) == Verdict.Possible);
        Check("owner", Rules.Owner("Wuild/GatherLite") == "Wuild");
        Check("judge: folder missing", Rules.Judge(Entry("GatherLite", repository: "b/c"), "b/c", Release(["Other"])) == Verdict.NotIt);
        Check("judge: no release", Rules.Judge(Entry("GatherLite", repository: "b/c"), "b/c", ReleaseRead.Failed("No published release.")) == Verdict.NotIt);

        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Curse-Project-ID"] = "5", ["X-Wago-ID"] = "abc", ["X-WoWI-ID"] = "9" };
        var notes = Rules.DeclaredNotes(Entry("Foo", curseforge: [7]), declared);
        Check("declared: unlisted CurseForge", notes.Count == 3 && notes[0].Contains("doesn't list") && notes[1].Contains("no Wago source") && notes[2].Contains("WoWInterface"));
        Check("declared: matching CurseForge", Rules.DeclaredNotes(Entry("Foo", curseforge: [5], wago: ["foo"]), declared)[0].Contains("matches")
            && Rules.DeclaredNotes(Entry("Foo", curseforge: [5], wago: ["foo"]), declared)[1].Contains("check they're the same"));
        Check("declared: none", Rules.DeclaredNotes(Entry("Foo"), new Dictionary<string, string> { ["X-Curse-Project-ID"] = "abc" }).Count == 0);

        Check("md: code span", Md.Code("a`b](http://x)\n") == "`a'b](http://x)`" && Md.Code("") == "`(empty)`" && Md.Code(new string('x', 150)).Length == 103);

        Console.WriteLine(failures == 0 ? "Self-test passed." : $"{failures} self-test check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
