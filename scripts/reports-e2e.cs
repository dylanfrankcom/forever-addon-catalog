#:property PublishAot=false
// SPDX-License-Identifier: GPL-3.0-or-later

// End-to-end test of version reports on made-up data, with no network and no secrets. It runs the real scripts in the
// order the workflows do, in a throwaway folder:
//   1. generate.cs builds a catalog from a made-up hand-listed add-on (an override with a CurseForge source and the
//      Forever edition) and nothing else;
//   2. reports.cs collect reads a recorded relay answer (--answer) with one report for it, and keeps it in the store;
//   3. reports.cs propose proposes the version in confirmed.json and the pull request's text;
//   4. generate.cs again copies the confirmed version onto the add-on's source.
// The pieces have their own self-tests; this catches what sits between them, like an override's editions that never
// reach catalog.json, so nothing is ever proposed.
//
//   ... dotnet run scripts/reports-e2e.cs        # from the repository root
// Exit code 0 when it passed, 1 when a step or a check failed.

using System.Diagnostics;
using System.Text.Json.Nodes;

var root = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(root, "scripts", "generate.cs")) || !File.Exists(Path.Combine(root, "catalog.json")))
{
    Console.Error.WriteLine("Run it from the repository root.");
    return 1;
}
var work = Directory.CreateTempSubdirectory("reports-e2e-").FullName;
var failures = 0;
try
{
    // The made-up inputs. The previous catalog keeps the real editions and nothing else.
    var real = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog.json")))!.AsObject();
    var previous = new JsonObject
    {
        ["$schema"] = real["$schema"]?.DeepClone(), ["schemaVersion"] = real["schemaVersion"]?.DeepClone(), ["version"] = "1.0.0",
        ["editions"] = real["editions"]!.DeepClone(), ["authors"] = new JsonObject(), ["addons"] = new JsonArray(),
    };
    var forever = real["editions"]!["forever"]!["interface"]!.GetValue<string>();
    Write("catalog.json", previous);
    Write("candidates.json", new JsonObject { ["candidates"] = new JsonArray() });
    Write("checks.json", new JsonObject { ["repos"] = new JsonArray() });
    Write("confirmed.json", new JsonObject { ["addons"] = new JsonObject() });
    Directory.CreateDirectory(Path.Combine(work, "overrides"));
    Write("overrides/_authors.json", JsonNode.Parse("""{"authors":{"madeup-author":{"name":"Made-up Author"}}}""")!);
    Write("overrides/madeup-addon.json", JsonNode.Parse("""
        {"name":"Made-up Add-on","summary":"An add-on that exists only in this test.","author":"madeup-author","added":"2026-11-01",
         "sources":[{"type":"curseforge","projectId":999999901}],"folders":["MadeUpAddon"],"editions":{"forever":{"interface":"FOREVER"}}}
        """.Replace("FOREVER", forever))!);
    var sha = new string('c', 64);
    Write("answer.json", JsonNode.Parse($$"""
        {"asOf":"2026-11-28T10:00:00.000Z","reports":[
          {"id":1,"addon":"madeup-addon","source":"curseforge","edition":"forever","version":"1.2.3","sha256":"{{sha}}","count":3,"networks":2,
           "firstSeen":"2026-11-27T10:00:00Z","lastSeen":"2026-11-28T09:00:00Z"}],
         "suggestions":[]}
        """)!);

    string[] generate = ["--catalog", P("catalog.json"), "--candidates", P("candidates.json"), "--checks", P("checks.json"), "--overrides", P("overrides"),
        "--optout", P("optout.json"), "--no-icons", "--icons", P("icons"), "--versions", P("versions.json"), "--confirmed", P("confirmed.json"), "--tags", P("tags.json")];

    // 1. The catalog lists the hand-listed add-on for Forever.
    Run("generate.cs", generate);
    var entry = Entry();
    Expect("generate: the hand-listed add-on is listed for Forever", entry?["editions"]?["forever"] is not null);

    // 2. One report collected from the recorded answer.
    Run("reports.cs", ["collect", "--catalog", P("catalog.json"), "--store", P("relay.json"), "--collected", P("collected.json"), "--answer", P("answer.json")]);
    var store = JsonNode.Parse(File.ReadAllText(P("relay.json")));
    Expect("collect: the report is kept with its networks", store?["reports"]?.AsObject().Count == 1
        && store["reports"]!.AsObject().First().Value?["networks"]?.GetValue<long>() == 2);
    Expect("collect: its ID is marked collected", JsonNode.Parse(File.ReadAllText(P("collected.json")))?["reports"]?.AsArray().Count == 1);

    // 3. Proposed.
    var output = Run("reports.cs", ["propose", "--catalog", P("catalog.json"), "--confirmed", P("confirmed.json"), "--store", P("relay.json"),
        "--body", P("body.md"), "--facts", P("facts.json")]);
    var confirmed = JsonNode.Parse(File.ReadAllText(P("confirmed.json")));
    var proposed = confirmed?["addons"]?["madeup-addon"]?["curseforge"]?["forever"];
    Expect("propose: the reported version and hash go into confirmed.json", proposed?["version"]?.GetValue<string>() == "1.2.3"
        && proposed["sha256"]?.GetValue<string>() == sha && proposed["reports"]?.GetValue<long>() == 3);
    Expect("propose: nothing reported as not proposed", !output.Contains("not proposed", StringComparison.Ordinal));
    var body = File.ReadAllText(P("body.md"));
    Expect("propose: the pull request's text names it with its reports and players", body.Contains("Made-up Add-on", StringComparison.Ordinal)
        && body.Contains("`1.2.3`", StringComparison.Ordinal) && body.Contains("3 reports from about 2 players", StringComparison.Ordinal));

    // 4. The next build copies it onto the source.
    Run("generate.cs", generate);
    var source = Entry()?["sources"]?.AsArray().OfType<JsonObject>().FirstOrDefault(s => s["type"]?.GetValue<string>() == "curseforge");
    Expect("generate: the confirmed version is on the add-on's source", source?["confirmed"]?["forever"]?["version"]?.GetValue<string>() == "1.2.3");
}
catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException)
{
    Console.WriteLine($"FAIL {e.Message}");
    failures++;
}
finally
{
    Directory.Delete(work, true);
}
Console.WriteLine(failures == 0 ? "reports end-to-end test passed." : $"reports end-to-end test: {failures} failed.");
return failures == 0 ? 0 : 1;

string P(string name) => Path.Combine(work, name);

void Write(string name, JsonNode node) => File.WriteAllText(P(name), node.ToJsonString());

JsonObject? Entry() => JsonNode.Parse(File.ReadAllText(P("catalog.json")))?["addons"]?.AsArray().OfType<JsonObject>()
    .FirstOrDefault(a => a["id"]?.GetValue<string>() == "madeup-addon");

void Expect(string name, bool ok)
{
    if (!ok)
    {
        failures++;
        Console.WriteLine($"FAIL {name}");
    }
}

// Runs one of the scripts with dotnet from the repository root; throws, with its output, when it fails.
string Run(string script, string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in new[] { "run", Path.Combine("scripts", script), "--" }.Concat(arguments))
    {
        start.ArgumentList.Add(a);
    }
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{script} {arguments.FirstOrDefault()} exited {process.ExitCode}: {stderr.Result.Trim()} {stdout.Result.Trim()}");
    }
    return stdout.Result;
}
