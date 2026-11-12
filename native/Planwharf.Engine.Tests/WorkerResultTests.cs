using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class WorkerResultTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("pw claims é ").FullName;
    static JsonObject Identity() => JsonNode.Parse("""{"resultId":"R","attemptId":"A","subTaskId":"S","megaTaskId":"M","provider":"codex","cli":"gpt-6-astra"}""")!.AsObject();
    static JsonObject Result() => JsonNode.Parse("""{"resultId":"R","attemptId":"A","subTaskId":"S","megaTaskId":"M","status":"COMPLETE","summary":"done","nextAction":"","filesChanged":[],"tests":[],"blockers":[],"unknown":{"keep":true}}""")!.AsObject();
    public void Dispose()
    {
        // Git marks objects read-only. Junctions and symlinks must be removed
        // without walking through them, or Windows refuses the delete.
        DeleteTree(folder);
    }

    static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var child in new DirectoryInfo(path).GetFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                child.Attributes = FileAttributes.Normal;
                if (child is DirectoryInfo) Directory.Delete(child.FullName);
                else File.Delete(child.FullName);
            }
            else if (child is DirectoryInfo) DeleteTree(child.FullName);
            else
            {
                child.Attributes = FileAttributes.Normal;
                child.Delete();
            }
        }
        try { Directory.Delete(path, true); }
        catch (UnauthorizedAccessException)
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                entry.Attributes = FileAttributes.Normal;
            Directory.Delete(path, true);
        }
    }
    string Write(string name, string contents)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void A_COMPLETE_result_naming_a_missing_file_fails_deletions_and_other_statuses_do_not()
    {
        var real = Write("real.txt", "x");
        var claim = Result();
        claim["filesChanged"] = new JsonArray("real.txt", real);
        Assert.Empty(WorkerResult.MissingClaims(claim, folder));
        claim["filesChanged"] = new JsonArray("ghost.txt");
        Assert.Equal(new[] { "ghost.txt" }, WorkerResult.MissingClaims(claim, folder));
        claim["status"] = "BLOCKED";
        Assert.Empty(WorkerResult.MissingClaims(claim, folder));
        claim["status"] = "COMPLETE";
        var rejected = WorkerResult.RejectMissingClaims(claim, folder);
        Assert.Equal("FAILED", rejected["status"]!.GetValue<string>());
        Assert.Equal(["Planwharf found no such file: ghost.txt"], rejected["blockers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("COMPLETE", claim["status"]!.GetValue<string>());
        Assert.Empty(claim["blockers"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(claim["unknown"], rejected["unknown"]));
        claim["blockers"] = new JsonArray("kept");
        claim["filesChanged"] = new JsonArray("ghost.txt", "other.txt");
        rejected = WorkerResult.RejectMissingClaims(claim, folder);
        Assert.Equal(["kept", "Planwharf found no such file: ghost.txt, other.txt"], rejected["blockers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["kept"], claim["blockers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Git("init", "-q");
        var gone = Write("gone é.txt", "x");
        Git("add", "--", "gone é.txt");
        File.Delete(gone);
        claim["filesChanged"] = new JsonArray("gone é.txt");
        Assert.Empty(WorkerResult.MissingClaims(claim, folder));
        Assert.Same(claim, WorkerResult.RejectMissingClaims(claim, folder));
    }

    void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void ReadJSON_accepts_a_BOM_and_rejects_missing_malformed_and_oversized_files()
    {
        var file = Write("result.json", "\uFEFF{\"value\":1}");
        Assert.Equal(1, WorkerResult.ReadJSON(file)!["value"]!.GetValue<int>());
        var bytes = File.ReadAllBytes(file);
        WorkerResult.ReadJSON(file);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Null(WorkerResult.ReadJSON(Path.Combine(folder, "missing")));
        Assert.Null(WorkerResult.ReadJSON(Write("bad", "{")));
        Assert.NotNull(WorkerResult.ReadJSON(Write("limit", "{}" + new string(' ', 1024 * 1024 - 2))));
        Assert.Null(WorkerResult.ReadJSON(Write("large", "{}" + new string(' ', 1024 * 1024 - 1))));
    }

    [Fact]
    public void Validation_checks_identity_status_arrays_tests_dates_and_optional_sources()
    {
        var valid = Result();
        Assert.True(WorkerResult.ValidateResult(valid, Identity()));
        valid["summary"] = "\uFEFF";
        Assert.False(WorkerResult.ValidateResult(valid, Identity()));
        valid["summary"] = "\u0085";
        Assert.True(WorkerResult.ValidateResult(valid, Identity()));
        valid["summary"] = "done";
        foreach (var key in new[] { "resultId", "attemptId", "subTaskId", "megaTaskId", "summary", "status", "nextAction", "filesChanged", "tests", "blockers" })
        {
            var bad = valid.DeepClone().AsObject();
            bad.Remove(key);
            Assert.False(WorkerResult.ValidateResult(bad, Identity()));
        }
        foreach (var patch in new[] { "{\"attemptId\":\"other\"}", "{\"summary\":\"  \"}", "{\"createdAt\":null}", "{\"createdAt\":\"invalid\"}", "{\"filesChanged\":[1]}", "{\"blockers\":[null]}", "{\"tests\":[{}]}", "{\"sources\":null}" })
        {
            var bad = valid.DeepClone().AsObject();
            foreach (var pair in JsonNode.Parse(patch)!.AsObject()) bad[pair.Key] = pair.Value?.DeepClone();
            Assert.False(WorkerResult.ValidateResult(bad, Identity()));
        }
        valid["createdAt"] = "2026-11-12T19:00:00Z";
        valid["tests"] = JsonNode.Parse("""[{"command":"test","status":"NOT_RUN","evidence":""}]""");
        valid["sources"] = new JsonArray("https://example.com");
        Assert.True(WorkerResult.ValidateResult(valid, Identity()));
    }

    [Fact]
    public void Validated_results_require_matching_runner_evidence_and_preserve_unknown_fields()
    {
        var result = Result();
        var file = Write("result.json", result.ToJsonString());
        var exit = JsonNode.Parse("""{"attemptId":"A","provider":"codex","model":"gpt-6-astra","exitCode":0,"createdAt":"2026-11-12T19:00:00Z"}""")!.AsObject();
        string SaveExit() => Write("exit.json", exit.ToJsonString());
        var read = WorkerResult.ReadValidatedResult(file, Identity(), SaveExit());
        Assert.NotNull(read);
        Assert.True(JsonNode.DeepEquals(result["unknown"], read["unknown"]));
        Assert.Equal(exit["createdAt"]!.GetValue<string>(), read["createdAt"]!.GetValue<string>());
        Assert.Equal(file, read["evidencePath"]!.GetValue<string>());
        foreach (var key in new[] { "attemptId", "provider", "model", "exitCode" })
        {
            var saved = exit[key]!.DeepClone();
            exit[key] = "wrong";
            Assert.Null(WorkerResult.ReadValidatedResult(file, Identity(), SaveExit()));
            exit[key] = saved;
        }
        exit["exitCode"] = 1;
        Assert.Null(WorkerResult.ReadValidatedResult(file, Identity(), SaveExit()));
        result["status"] = "FAILED";
        Write("result.json", result.ToJsonString());
        Assert.NotNull(WorkerResult.ReadValidatedResult(file, Identity(), SaveExit()));
        exit["exitCode"] = 0.5;
        Assert.Null(WorkerResult.ReadValidatedResult(file, Identity(), SaveExit()));
    }

    [Fact]
    public void Report_must_be_nonempty_regular_report_md_in_the_exact_folder()
    {
        var result = Result();
        Assert.False(WorkerResult.ReportExists(result, folder));
        result["reportPath"] = Write("report.md", "");
        Assert.False(WorkerResult.ReportExists(result, folder));
        Write("report.md", "findings");
        Assert.True(WorkerResult.ReportExists(result, folder));
        result["reportPath"] = Write("other.md", "findings");
        Assert.False(WorkerResult.ReportExists(result, folder));
    }

    [Fact]
    public void Contract_selects_structured_output_only_for_verified_adapters()
    {
        var opts = Identity();
        Assert.Equal("", WorkerResult.ResultContract(opts));
        opts["attempt"] = "A";
        opts["structuredResultPath"] = "C:\\résultats\\result.json";
        var contract = WorkerResult.ResultContract(opts);
        Assert.StartsWith("Planwharf structured result contract:\nWrite one UTF-8 JSON object to C:\\résultats\\result.json.", contract);
        Assert.Contains("\n  \"attemptId\": \"A\",\n", contract);
        Assert.EndsWith(" Keep the text execution evidence separate.", contract);
        opts["model"] = new JsonObject { ["adapter"] = "claude" };
        contract = WorkerResult.ResultContract(opts);
        Assert.Contains("Do not write a result file; Planwharf saves it and records the time:", contract);
        Assert.DoesNotContain("Keep the text execution evidence separate.", contract);
        opts["structuredResultPath"] = "";
        Assert.Equal("", WorkerResult.ResultContract(opts));
        opts["structuredResultPath"] = 0;
        Assert.Equal("", WorkerResult.ResultContract(opts));
        opts["structuredResultPath"] = "out.json";
        opts.Remove("attempt");
        Assert.DoesNotContain("\"attemptId\"", WorkerResult.ResultContract(opts));
    }

    [Fact]
    public void ReadJSON_error_paths_and_concurrent_reads()
    {
        Assert.Null(WorkerResult.ReadJSON(null));
        Assert.Null(WorkerResult.ReadJSON(folder));
        Assert.Null(WorkerResult.ReadJSON(Write("empty.json", "")));
        Assert.Null(WorkerResult.ReadJSON(Write("null.json", "null")));
        var file = Write("ok.json", "{\"n\":1}");
        using (var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(WorkerResult.ReadJSON(file));
        Write("ok.json", "{\"n\":1}");
        var hits = 0;
        Parallel.For(0, 32, _ =>
        {
            var node = WorkerResult.ReadJSON(file);
            if (node?["n"]?.GetValue<int>() == 1) Interlocked.Increment(ref hits);
        });
        Assert.Equal(32, hits);
    }

    [Fact]
    public void Validated_result_keeps_createdAt_and_allows_nonzero_exit_unless_COMPLETE()
    {
        var result = Result();
        result["createdAt"] = "2026-01-01T00:00:00Z";
        var file = Write("result.json", result.ToJsonString());
        var exit = JsonNode.Parse("""{"attemptId":"A","provider":"codex","model":"gpt-6-astra","exitCode":0,"createdAt":"2026-11-12T19:00:00Z"}""")!.AsObject();
        var read = WorkerResult.ReadValidatedResult(file, Identity(), Write("exit.json", exit.ToJsonString()));
        Assert.Equal("2026-01-01T00:00:00Z", read!["createdAt"]!.GetValue<string>());
        result.Remove("createdAt");
        Write("result.json", result.ToJsonString());
        var identity = Identity();
        identity.Remove("provider");
        identity.Remove("cli");
        exit["provider"] = "other";
        exit["model"] = "other";
        Assert.NotNull(WorkerResult.ReadValidatedResult(file, identity, Write("exit.json", exit.ToJsonString())));
        identity["provider"] = "codex";
        Assert.Null(WorkerResult.ReadValidatedResult(file, identity, Write("exit.json", exit.ToJsonString())));
        result["status"] = "BLOCKED";
        Write("result.json", result.ToJsonString());
        exit["provider"] = "codex";
        exit["model"] = "gpt-6-astra";
        exit["exitCode"] = 7;
        Assert.NotNull(WorkerResult.ReadValidatedResult(file, Identity(), Write("exit.json", exit.ToJsonString())));
        Assert.False(WorkerResult.ValidateResult(new JsonArray(), Identity()));
        Assert.False(WorkerResult.ValidateResult(null, Identity()));
    }

    [Fact]
    public void Missing_claims_ignore_blank_names_and_FAILED_status()
    {
        var claim = Result();
        claim["filesChanged"] = new JsonArray("", "  ", "ghost.txt");
        Assert.Equal(new[] { "ghost.txt" }, WorkerResult.MissingClaims(claim, folder));
        claim["status"] = "FAILED";
        Assert.Empty(WorkerResult.MissingClaims(claim, folder));
        claim["status"] = "COMPLETE";
        claim["filesChanged"] = "ghost.txt";
        Assert.Empty(WorkerResult.MissingClaims(claim, folder));
        claim["filesChanged"] = new JsonArray("ghost.txt");
        var copies = new JsonObject[8];
        Parallel.For(0, copies.Length, i => copies[i] = WorkerResult.RejectMissingClaims(claim, folder));
        Assert.All(copies, copy => Assert.Equal("FAILED", copy["status"]!.GetValue<string>()));
        Assert.Equal("COMPLETE", claim["status"]!.GetValue<string>());
        Directory.CreateDirectory(Path.Combine(folder, "report.md"));
        claim["reportPath"] = Path.Combine(folder, "report.md");
        Assert.False(WorkerResult.ReportExists(claim, folder));
        claim["reportPath"] = "report.md";
        Assert.False(WorkerResult.ReportExists(claim, folder));
        var real = Write("real-report.md", "findings");
        var reportLink = Path.Combine(folder, "report.md");
        if (Directory.Exists(reportLink)) Directory.Delete(reportLink);
        var start = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "/c mklink \"" + reportLink + "\" \"" + real + "\""
        };
        using (var process = Process.Start(start)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                claim["reportPath"] = reportLink;
                Assert.False(WorkerResult.ReportExists(claim, folder));
            }
        }
    }
}
