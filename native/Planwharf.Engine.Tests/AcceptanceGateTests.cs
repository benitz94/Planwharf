using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Job 26. Isolated acceptance gate. Fake programs only; nothing is discovered inside the candidate.
public sealed class AcceptanceGateTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw gate é ").FullName;
    const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Project = "proj";
    const string Job = "job-1";
    static readonly UTF8Encoding Utf8 = new(false);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    string Attempts() => Path.Combine(root, "attempts");
    string JobDir(string job = Job) => Path.Combine(Attempts(), "pipeline", Project, "jobs", job);

    string WriteManifest(JsonArray checks, int revision = 3, string? approvedBy = "owner", string[]? shellChecks = null, string job = Job)
    {
        var manifest = new JsonObject
        {
            ["summary"] = "Accepted checks",
            ["schema"] = 1,
            ["jobId"] = job,
            ["specRevision"] = revision,
            ["baseCommit"] = Base,
            ["checks"] = checks
        };
        var bytes = Utf8.GetBytes(manifest.ToJsonString());
        var hash = AcceptanceGate.Sha256(bytes);
        var dir = JobDir(job);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "acceptance.json"), bytes);
        var approval = new JsonObject
        {
            ["summary"] = "Owner approval",
            ["jobId"] = job,
            ["specRevision"] = revision,
            ["manifestSha256"] = hash,
            ["baseCommit"] = Base,
            ["approvedBy"] = approvedBy ?? "",
            ["approvedAt"] = "2026-01-01T00:00:00Z",
            ["shellChecks"] = new JsonArray()
        };
        if (shellChecks is not null)
        {
            var list = (JsonArray)approval["shellChecks"]!;
            foreach (var id in shellChecks) list.Add(id);
        }
        File.WriteAllText(Path.Combine(dir, "approval.json"), approval.ToJsonString());
        var pointer = new JsonObject
        {
            ["summary"] = "Job",
            ["jobId"] = job,
            ["projectId"] = Project,
            ["status"] = "approved",
            ["snapshotId"] = "rev-1",
            ["manifestSha256"] = hash,
            ["manifestPath"] = "acceptance.json",
            ["specRevision"] = revision,
            ["baseCommit"] = Base
        };
        File.WriteAllText(Path.Combine(dir, "job.json"), pointer.ToJsonString());
        return hash;
    }

    static JsonObject EngineCheck(string id, string sha, string expected = "ok") => new()
    {
        ["id"] = id,
        ["purpose"] = "Pinned check " + id,
        ["executable"] = "check.exe",
        ["arguments"] = new JsonArray { "--flag", "hello world", "テスト" },
        ["workingDirectory"] = ".",
        ["timeoutSeconds"] = 5,
        ["expectedExitCode"] = 0,
        ["expectedResult"] = expected,
        ["shell"] = false,
        ["trust"] = "engine-owned",
        ["programSha256"] = sha
    };

    static string Hex(byte b) => Convert.ToHexString(new[] { b }).ToLowerInvariant().PadLeft(64, 'a')[..64];

    [Fact]
    public void Load_keeps_the_owner_manifest_when_the_candidate_copy_changes()
    {
        var sha = Hex(1);
        var hash = WriteManifest(new JsonArray { EngineCheck("build", sha), EngineCheck("test", Hex(2), "passed") });
        var work = Path.Combine(root, "work tree");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "acceptance.json"), """{"summary":"forged","schema":1,"checks":[]}""");
        File.WriteAllText(Path.Combine(work, "package.json"), """{"scripts":{"test":"exit 0"}}""");
        File.WriteAllText(Path.Combine(work, "result.json"), """{"status":"COMPLETE","tests":[{"status":"PASSED"}]}""");

        var loaded = AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", hash, Base);

        Assert.Equal("ok", loaded.Status);
        Assert.Equal(new[] { "build", "test" }, loaded.CheckIds);
        Assert.Equal(hash, loaded.ManifestSha256);
        Assert.Equal("build", loaded.Checks[0].Id);
        Assert.Equal(new[] { "--flag", "hello world", "テスト" }, loaded.Checks[0].Arguments);
    }

    [Fact]
    public void Missing_or_corrupt_pinned_data_blocks_the_gate()
    {
        var missing = AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", Hex(3), Base);
        Assert.Equal("waiting", missing.Status);
        Assert.Equal("missing-manifest", missing.Reason);

        var sha = Hex(1);
        var hash = WriteManifest(new JsonArray { EngineCheck("build", sha) });
        File.WriteAllText(Path.Combine(JobDir(), "acceptance.json"), "{");
        var corrupt = AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", hash, Base);
        Assert.Equal("blocked", corrupt.Status);
        Assert.Equal("hash-mismatch", corrupt.Reason);

        hash = WriteManifest(new JsonArray { EngineCheck("build", sha) });
        var forged = AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", Hex(9), Base);
        Assert.Equal("hash-mismatch", forged.Reason);

        File.WriteAllText(Path.Combine(JobDir(), "approval.json"), """{"summary":"no","approvedBy":"builder","manifestSha256":"x","specRevision":3,"baseCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","jobId":"job-1"}""");
        var unauth = AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", hash, Base);
        Assert.Equal("unauthorized", unauth.Reason);
    }

    [Fact]
    public void A_shell_string_an_absolute_path_or_a_candidate_check_without_an_assertion_is_corrupt()
    {
        var sha = Hex(1);
        var shellString = EngineCheck("build", sha);
        shellString["arguments"] = "npm test";
        WriteManifest(new JsonArray { shellString });
        var bytes = File.ReadAllBytes(Path.Combine(JobDir(), "acceptance.json"));
        var hash = AcceptanceGate.Sha256(bytes);
        RewritePointer(hash);
        Assert.Equal("corrupt-manifest", AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", hash, Base).Reason);

        var absolute = EngineCheck("build", sha);
        absolute["workingDirectory"] = "/tmp/elsewhere";
        WriteManifest(new JsonArray { absolute });
        bytes = File.ReadAllBytes(Path.Combine(JobDir(), "acceptance.json"));
        hash = AcceptanceGate.Sha256(bytes);
        RewritePointer(hash);
        Assert.Equal("corrupt-manifest", AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", hash, Base).Reason);

        var candidate = EngineCheck("build", sha);
        candidate["trust"] = "candidate-code";
        candidate.Remove("programSha256");
        WriteManifest(new JsonArray { candidate });
        bytes = File.ReadAllBytes(Path.Combine(JobDir(), "acceptance.json"));
        hash = AcceptanceGate.Sha256(bytes);
        RewritePointer(hash);
        Assert.Equal("corrupt-manifest", AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", hash, Base).Reason);
    }

    [Fact]
    public void A_missing_tracked_list_does_not_invent_a_pin()
    {
        var work = Work("src/a.txt", "one");
        File.WriteAllText(Path.Combine(work, ".env"), "TOKEN=secret");
        var pin = AcceptanceGate.Pin(work, null, null);
        Assert.Equal("unsafe", pin.Status);
        Assert.Equal("repository-port-missing", pin.Reason);
        Assert.Equal("", pin.TreeDigest);
    }

    [Fact]
    public void Pin_hashes_tracked_and_working_files_and_skips_private_and_ignored()
    {
        var work = Work("src/a.txt", "one");
        File.WriteAllText(Path.Combine(work, ".env"), "TOKEN=secret");
        Directory.CreateDirectory(Path.Combine(work, ".planwharf"));
        File.WriteAllText(Path.Combine(work, ".planwharf", "notebook.md"), "personal");
        File.WriteAllText(Path.Combine(work, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(work, "noise.log"), "ignore me");
        File.WriteAllText(Path.Combine(work, "extra.txt"), "build input");

        var pin = AcceptanceGate.Pin(work, ["src/a.txt"], ["extra.txt"]);
        Assert.Equal("pinned", pin.Status);
        Assert.Contains("src/a.txt", pin.Included);
        Assert.Contains("extra.txt", pin.Included);
        Assert.DoesNotContain(".env", pin.Included);
        Assert.DoesNotContain("noise.log", pin.Included);

        File.WriteAllText(Path.Combine(work, "src", "a.txt"), "two");
        var changed = AcceptanceGate.Pin(work, ["src/a.txt"], ["extra.txt"]);
        Assert.NotEqual(pin.TreeDigest, changed.TreeDigest);
        Assert.Equal(pin.WorkingInputDigest, changed.WorkingInputDigest);

        var undeclared = AcceptanceGate.Pin(work, ["src/a.txt"], null);
        Assert.Equal("undeclared-input", undeclared.Reason);

        var secret = AcceptanceGate.Pin(work, [".env"], null);
        Assert.Equal("private-input", secret.Reason);

        File.WriteAllText(Path.Combine(work, ".gitignore"), "foo/**\n");
        var unparsed = AcceptanceGate.Pin(work, ["src/a.txt"], null);
        Assert.Equal("ignore-unparsed", unparsed.Reason);
    }

    [Fact]
    public void A_symlink_cannot_be_pinned()
    {
        var work = Work("src/a.txt", "one");
        var link = Path.Combine(work, "link.txt");
        try { File.CreateSymbolicLink(link, Path.Combine(work, "src", "a.txt")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var pin = AcceptanceGate.Pin(work, ["src/a.txt"], null);
        Assert.Equal("symlink", pin.Reason);
    }

    string Work(string relative, string text, string name = "work tree")
    {
        var work = Path.Combine(root, name);
        var file = Path.Combine(work, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return work;
    }

    string Store(string id, string body = "pinned", string job = Job)
    {
        var dir = Path.Combine(JobDir(job), "check-store", id);
        Directory.CreateDirectory(dir);
        var bytes = Utf8.GetBytes(body);
        File.WriteAllBytes(Path.Combine(dir, "check.exe"), bytes);
        return AcceptanceGate.Sha256(bytes);
    }

    AcceptanceGate.Request Gate(string hash, string work, AcceptanceGate.Starter start, string stage = "pre-review", string job = Job, IReadOnlyList<string>? tracked = null, string attempt = "attempt-1", string initiatedBy = "engine", CancellationToken cancellation = default, Action? beforeSeal = null, string worktreeId = "wt-1", string snapshot = "rev-1", IReadOnlyList<string>? working = null) => new()
    {
        AttemptsRoot = Attempts(),
        ProjectId = Project,
        JobId = job,
        SnapshotId = snapshot,
        ManifestSha256 = hash,
        BaseCommit = Base,
        Stage = stage,
        AttemptId = attempt,
        InitiatedBy = initiatedBy,
        WorktreePath = work,
        WorktreeId = worktreeId,
        TrackedRelativePaths = tracked ?? ["src/a.txt"],
        WorkingInputs = working,
        CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        Now = () => 1_700_000_000_000,
        Start = start,
        Cancellation = cancellation,
        BeforeSeal = beforeSeal
    };

    static AcceptanceGate.Starter Fake(Action<AcceptanceGate.ProcessRequest>? then = null, string output = "ok", int code = 0) => (request, events) =>
    {
        then?.Invoke(request);
        if (output.Length > 0) events.Output(output);
        events.Exit(code);
    };

    AcceptanceGate.PassClaim Claim(AcceptanceGate.Outcome outcome, string stage = "pre-review", string attempt = "attempt-1", string snapshot = "rev-1", string worktree = "wt-1", IReadOnlyList<string>? checks = null) => new()
    {
        Stage = stage,
        AttemptId = attempt,
        SnapshotId = snapshot,
        ManifestSha256 = outcome.ManifestSha256,
        WorktreeId = worktree,
        TreeDigest = outcome.TreeDigest,
        WorkingInputDigest = outcome.WorkingInputDigest,
        ArtifactDigest = outcome.ArtifactDigest,
        CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        CheckIds = checks ?? outcome.CheckIds
    };

    static JsonObject CandidateCheck(string id, string assertion) => new()
    {
        ["id"] = id,
        ["purpose"] = "Candidate " + id,
        ["executable"] = "candidate.exe",
        ["arguments"] = new JsonArray { "--flag", "hello world", "テスト" },
        ["workingDirectory"] = ".",
        ["timeoutSeconds"] = 5,
        ["expectedExitCode"] = 0,
        ["expectedResult"] = "printed by the candidate",
        ["shell"] = false,
        ["trust"] = "candidate-code",
        ["engineOwnedAssertionId"] = assertion
    };

    static void Reseal(string json)
    {
        File.WriteAllText(json[..^".json".Length] + ".seal", AcceptanceGate.Sha256(File.ReadAllBytes(json)) + "\n");
    }

    string Evidence(AcceptanceGate.Outcome outcome, string job = Job) =>
        Path.Combine(JobDir(job), "gate-runs", outcome.RunId + ".json");

    [Fact]
    public async Task Pre_review_pass_records_both_checks_and_does_not_call_a_model()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "ghp_secretvalue");
        try
        {
            var first = Store("build");
            var second = Store("test");
            var hash = WriteManifest(new JsonArray { EngineCheck("build", first), EngineCheck("test", second, "passed") });
            var work = Work("src/a.txt", "one");
            var seen = new List<(string id, string[] args, bool shell, bool secret)>();
            var outcome = await AcceptanceGate.Run(Gate(hash, work, (request, events) =>
            {
                seen.Add((request.CheckId, request.Arguments, request.Shell, request.Environment.ContainsKey("GITHUB_TOKEN")));
                events.Output(request.CheckId == "test" ? "passed" : "ok");
                events.Exit(0);
            }));
            Assert.Equal("pass", outcome.Status);
            Assert.False(outcome.PullRequestReady);
            Assert.False(outcome.ReturnToBuilder);
            Assert.Equal(new[] { "build", "test" }, outcome.CheckIds);
            Assert.Equal(new[] { "build", "test" }, seen.Select(item => item.id));
            Assert.All(seen, item => Assert.False(item.secret));
            Assert.Equal(new[] { "--flag", "hello world", "テスト" }, seen[0].args);
            Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, outcome.RunId!, Claim(outcome)));
        }
        finally { Environment.SetEnvironmentVariable("GITHUB_TOKEN", token); }
    }

    [Fact]
    public async Task Post_sync_reruns_on_the_integrated_tree_and_keeps_the_earlier_pass()
    {
        var sha = Store("build");
        var hash = WriteManifest(new JsonArray { EngineCheck("build", sha) });
        var work = Work("src/a.txt", "one");
        var calls = 0;
        AcceptanceGate.Starter start = (_, events) => { calls++; events.Output("ok"); events.Exit(0); };
        var pre = await AcceptanceGate.Run(Gate(hash, work, start));
        var post = await AcceptanceGate.Run(Gate(hash, work, start, stage: "post-sync"));
        Assert.Equal(2, calls);
        Assert.Equal("pass", pre.Status);
        Assert.False(pre.PullRequestReady);
        Assert.True(post.PullRequestReady);
        Assert.False(post.ReturnToBuilder);
        var historical = File.ReadAllBytes(Evidence(pre));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, pre.RunId!, Claim(post, "post-sync")));
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, pre.RunId!, Claim(pre)));
        File.WriteAllText(Path.Combine(work, "src", "a.txt"), "synced from main");
        var again = await AcceptanceGate.Run(Gate(hash, work, start, stage: "post-sync"));
        Assert.Equal(3, calls);
        Assert.True(again.PullRequestReady);
        Assert.NotEqual(post.TreeDigest, again.TreeDigest);
        Assert.Equal(historical, File.ReadAllBytes(Evidence(pre)));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, post.RunId!, Claim(again, "post-sync")));
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, again.RunId!, Claim(again, "post-sync")));
    }

    [Fact]
    public async Task A_failed_or_skipped_check_blocks_and_a_green_print_is_not_proof()
    {
        var build = Store("build");
        var test = Store("test");
        var hash = WriteManifest(new JsonArray { EngineCheck("build", build), EngineCheck("test", test, "passed") });
        var work = Work("src/a.txt", "one");
        var nonzero = await AcceptanceGate.Run(Gate(hash, work, (request, events) =>
        {
            events.Output(request.CheckId == "build" ? "nope ghp_secretvalueextra" : "passed");
            events.Exit(request.CheckId == "build" ? 2 : 0);
        }));
        Assert.Equal("blocked", nonzero.Status);
        Assert.Equal("nonzero-exit", nonzero.Reason);
        Assert.Contains("build", nonzero.FailingChecks);
        Assert.True(nonzero.ReturnToBuilder);
        Assert.False(nonzero.PullRequestReady);
        Assert.DoesNotContain("ghp_", nonzero.Log);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, nonzero.RunId!, Claim(nonzero)));

        var missed = await AcceptanceGate.Run(Gate(hash, work, (_, events) => { events.Output("ok"); events.Exit(0); }, attempt: "attempt-2"));
        Assert.Equal("assertion-failed", missed.Reason);
        Assert.Contains("test", missed.FailingChecks);
        Assert.DoesNotContain("build", missed.FailingChecks);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, missed.RunId!, Claim(missed, attempt: "attempt-2", checks: ["build"])));

        var synced = await AcceptanceGate.Run(Gate(hash, work, (_, events) => { events.Output("nope"); events.Exit(1); }, stage: "post-sync"));
        Assert.True(synced.BlocksPullRequest);
        Assert.False(synced.PullRequestReady);
        Assert.False(synced.ReturnToBuilder);

        var assertion = Store("assert", "trusted", "green");
        var greenHash = WriteManifest(new JsonArray { CandidateCheck("build", "assert"), EngineCheck("assert", assertion, "ARTIFACT-OK") }, job: "green");
        var green = Work("src/a.txt", "one", "green");
        File.WriteAllText(Path.Combine(green, "candidate.exe"), "candidate");
        File.WriteAllText(Path.Combine(green, "package.json"), """{"scripts":{"test":"echo COMPLETE"}}""");
        File.WriteAllText(Path.Combine(green, "result.json"), """{"status":"COMPLETE","tests":[{"status":"PASSED"}]}""");
        var calls = 0;
        var hidden = await AcceptanceGate.Run(Gate(greenHash, green, (_, _) => calls++, job: "green", tracked: ["src/a.txt", "candidate.exe"]));
        Assert.Equal("undeclared-input", hidden.Reason);
        Assert.Equal(0, calls);
        var judged = await AcceptanceGate.Run(Gate(greenHash, green, (request, events) =>
        {
            calls++;
            events.Output(request.CheckId == "build" ? "ALL TESTS PASSED" : "no");
            events.Exit(0);
        }, job: "green", tracked: ["src/a.txt", "candidate.exe", "package.json", "result.json"]));
        Assert.Equal("blocked", judged.Status);
        Assert.Equal("assertion-failed", judged.Reason);
        Assert.Contains("assert", judged.FailingChecks);
        Assert.False(judged.PullRequestReady);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Execution_failures_stay_distinct_and_a_retry_appends_another_record()
    {
        var sha = Store("build");
        var hash = WriteManifest(new JsonArray { EngineCheck("build", sha) });
        var work = Work("src/a.txt", "one");
        File.Delete(Path.Combine(JobDir(), "check-store", "build", "check.exe"));
        var missing = await AcceptanceGate.Run(Gate(hash, work, (_, _) => throw new InvalidOperationException("should not start")));
        Assert.Equal("error", missing.Status);
        Assert.Equal("spawn-error", missing.Reason);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, missing.RunId!, Claim(missing)));
        Store("build");

        var denied = await AcceptanceGate.Run(Gate(hash, work, (_, events) => events.Error("access denied")));
        Assert.Equal("spawn-error", denied.Reason);
        var crashed = await AcceptanceGate.Run(Gate(hash, work, (_, events) => events.Exit(null)));
        Assert.Equal("crashed", crashed.Reason);
        var thrown = await AcceptanceGate.Run(Gate(hash, work, (_, _) => throw new IOException("spawn denied")));
        Assert.Equal("spawn-error", thrown.Reason);
        var unsealed = await AcceptanceGate.Run(Gate(hash, work, (_, events) => { events.Output("ok"); events.Exit(0); }, beforeSeal: () => throw new IOException("disk full")));
        Assert.Equal("evidence-failure", unsealed.Reason);
        Assert.False(File.Exists(Evidence(unsealed)[..^".json".Length] + ".seal"));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, unsealed.RunId!, Claim(unsealed)));

        var failureBytes = File.ReadAllBytes(Evidence(denied));
        var retry = await AcceptanceGate.Run(Gate(hash, work, (_, events) => { events.Output("ok"); events.Exit(0); }));
        Assert.Equal("pass", retry.Status);
        Assert.NotEqual(denied.RunId, retry.RunId);
        Assert.Equal(failureBytes, File.ReadAllBytes(Evidence(denied)));
        Assert.False(File.Exists(Evidence(retry) + ".partial"));
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, retry.RunId!, Claim(retry)));

        var saved = File.ReadAllBytes(Evidence(retry));
        var edited = JsonNode.Parse(File.ReadAllText(Evidence(retry)))!.AsObject();
        ((JsonArray)edited["commands"]!).Clear();
        File.WriteAllBytes(Evidence(retry), Utf8.GetBytes(edited.ToJsonString()));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, retry.RunId!, Claim(retry)));
        File.WriteAllBytes(Evidence(retry), saved);
        Reseal(Evidence(retry));
        ((JsonArray)edited["commands"]!).Clear();
        File.WriteAllBytes(Evidence(retry), Utf8.GetBytes(edited.ToJsonString()));
        Reseal(Evidence(retry));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, retry.RunId!, Claim(retry)));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, retry.RunId!, Claim(retry, snapshot: "rev-2")));
        File.WriteAllBytes(Path.Combine(JobDir(), "check-store", "build", "check.exe"), "tampered"u8.ToArray());
        var tampered = await AcceptanceGate.Run(Gate(hash, work, (_, _) => throw new InvalidOperationException("tampered program")));
        Assert.Equal("blocked", tampered.Status);
        Assert.Equal("tamper", tampered.Reason);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, tampered.RunId!, Claim(tampered)));
    }

    [Fact]
    public async Task Timeout_and_cancellation_stop_the_tree_even_if_the_output_says_pass()
    {
        var slow = EngineCheck("build", Store("build"));
        slow["timeoutSeconds"] = 1;
        var hash = WriteManifest(new JsonArray { slow });
        var work = Work("src/a.txt", "one");
        var killed = 0;
        var timed = await AcceptanceGate.Run(Gate(hash, work, (_, events) =>
        {
            events.Output("ok");
            events.KillTree = () => killed++;
        }));
        Assert.Equal("timeout", timed.Reason);
        Assert.Equal("blocked", timed.Status);
        Assert.Equal(1, killed);
        Assert.True(timed.ReturnToBuilder);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, timed.RunId!, Claim(timed)));

        var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = AcceptanceGate.Run(Gate(hash, work, (_, events) =>
        {
            events.Output("ok");
            events.KillTree = () => killed++;
            started.TrySetResult();
        }, cancellation: cts.Token));
        await started.Task;
        cts.Cancel();
        var cancelled = await running;
        Assert.Equal("cancelled", cancelled.Reason);
        Assert.True(killed >= 2);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, cancelled.RunId!, Claim(cancelled)));

        using var already = new CancellationTokenSource();
        already.Cancel();
        var calls = 0;
        var early = await AcceptanceGate.Run(Gate(hash, work, (_, _) => calls++, cancellation: already.Token));
        Assert.Equal("cancelled", early.Reason);
        Assert.Equal(0, calls);

        var huge = new string('x', 9_000);
        var limited = await AcceptanceGate.Run(Gate(hash, work, (_, events) => { events.Output("ok" + huge); events.Exit(0); }));
        Assert.Equal("output-limit", limited.Reason);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, limited.RunId!, Claim(limited)));
    }

    [Fact]
    public async Task Restart_resumes_only_a_sealed_check_bound_to_the_same_snapshot()
    {
        var hash = WriteManifest(new JsonArray { EngineCheck("build", Store("build")), EngineCheck("test", Store("test"), "passed") });
        var work = Work("src/a.txt", "one");
        var seen = new List<string>();
        AcceptanceGate.Starter start = (request, events) =>
        {
            seen.Add(request.CheckId);
            events.Output(request.CheckId == "test" ? "passed" : "ok");
            events.Exit(0);
        };
        var first = await AcceptanceGate.Run(Gate(hash, work, start));
        Assert.Equal("pass", first.Status);
        seen.Clear();
        var second = await AcceptanceGate.Run(Gate(hash, work, start));
        Assert.Empty(seen);
        var resumed = JsonNode.Parse(File.ReadAllText(Evidence(second)))!["commands"]!.AsArray();
        Assert.All(resumed, node => Assert.False(string.IsNullOrEmpty(node!["resumedFrom"]!.GetValue<string>())));
        var junk = Path.Combine(JobDir(), "gate-runs", "r-junk.json");
        File.WriteAllBytes(junk, Utf8.GetBytes("{"));
        Reseal(junk);
        seen.Clear();
        Assert.Equal("pass", (await AcceptanceGate.Run(Gate(hash, work, start))).Status);
        Assert.Empty(seen);
        foreach (var seal in Directory.EnumerateFiles(Path.Combine(JobDir(), "gate-runs"), "*.seal")) File.Delete(seal);
        seen.Clear();
        var rebuilt = await AcceptanceGate.Run(Gate(hash, work, start));
        Assert.Equal(new[] { "build", "test" }, seen);
        seen.Clear();
        await AcceptanceGate.Run(Gate(hash, work, start, attempt: "attempt-9"));
        Assert.Equal(2, seen.Count);
        File.WriteAllText(Path.Combine(JobDir(), "gate-runs", "r-partial.json.partial"), "{}");
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, "r-partial", Claim(rebuilt)));
        var changed = await AcceptanceGate.Run(Gate(hash, work, (request, events) =>
        {
            if (request.CheckId == "build") File.WriteAllText(Path.Combine(work, "src", "a.txt"), "mutated");
            events.Output(request.CheckId == "test" ? "passed" : "ok");
            events.Exit(0);
        }, attempt: "attempt-change"));
        Assert.Equal("changed-during-run", changed.Reason);
        Assert.Equal(new[] { "build", "test" }, changed.FailingChecks);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, changed.RunId!, Claim(changed, attempt: "attempt-change")));
        var pin = AcceptanceGate.Pin(work, ["src/a.txt"], null);
        Assert.NotEqual(rebuilt.TreeDigest, pin.TreeDigest);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, rebuilt.RunId!, new AcceptanceGate.PassClaim
        {
            Stage = "pre-review",
            AttemptId = "attempt-1",
            SnapshotId = "rev-1",
            ManifestSha256 = rebuilt.ManifestSha256,
            WorktreeId = "wt-1",
            TreeDigest = pin.TreeDigest,
            WorkingInputDigest = pin.WorkingInputDigest,
            ArtifactDigest = pin.ArtifactDigest,
            CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            CheckIds = rebuilt.CheckIds
        }));
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, rebuilt.RunId!, Claim(rebuilt)));
    }

    [Fact]
    public async Task Concurrent_jobs_cannot_read_or_reuse_one_anothers_evidence()
    {
        var hashA = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "job-a"), "alpha") }, job: "job-a");
        var hashB = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "job-b"), "beta") }, job: "job-b");
        var workA = Work("src/a.txt", "A", "work-a");
        var workB = Work("src/a.txt", "B", "work-b");
        using var gate = new Barrier(2);
        var both = await Task.WhenAll(
            AcceptanceGate.Run(Gate(hashA, workA, (_, events) => { gate.SignalAndWait(TimeSpan.FromSeconds(5)); events.Output("alpha"); events.Exit(0); }, job: "job-a")),
            AcceptanceGate.Run(Gate(hashB, workB, (_, events) => { gate.SignalAndWait(TimeSpan.FromSeconds(5)); events.Output("beta"); events.Exit(0); }, job: "job-b")));
        Assert.All(both, item => Assert.Equal("pass", item.Status));
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "job-a", both[0].RunId!, Claim(both[0])));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "job-a", both[1].RunId!, Claim(both[0])));
        Assert.Equal("hash-mismatch", AcceptanceGate.Load(Attempts(), Project, "job-a", "rev-1", hashB, Base).Reason);
        var hashC = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "job-c")) }, job: "job-c");
        var workC = Work("src/a.txt", "C", "work-c");
        var together = await Task.WhenAll(
            AcceptanceGate.Run(Gate(hashC, workC, (_, events) => { Thread.Sleep(30); events.Output("ok"); events.Exit(0); }, job: "job-c", attempt: "left")),
            AcceptanceGate.Run(Gate(hashC, workC, (_, events) => { Thread.Sleep(30); events.Output("ok"); events.Exit(0); }, job: "job-c", attempt: "right")));
        Assert.All(together, item => Assert.Equal("pass", item.Status));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(JobDir("job-c"), "gate-runs"), "r-*.json").Count());
        Assert.NotEqual(together[0].RunId, together[1].RunId);
    }

    [Fact]
    public async Task Shell_paths_and_a_foreign_start_cannot_weaken_an_approved_manifest()
    {
        var pausedCheck = EngineCheck("build", Store("build"));
        pausedCheck["shell"] = true;
        var pausedHash = WriteManifest(new JsonArray { pausedCheck });
        var work = Work("src/a.txt", "one");
        var calls = 0;
        var paused = await AcceptanceGate.Run(Gate(pausedHash, work, (_, _) => calls++));
        Assert.Equal("needs-owner-review", paused.Reason);
        Assert.False(paused.ReturnToBuilder);
        Assert.Equal(0, calls);
        Assert.Contains("build", paused.FailingChecks);

        var shellSha = Store("build", job: "shell");
        var shellCheck = EngineCheck("build", shellSha);
        shellCheck["shell"] = true;
        var shellHash = WriteManifest(new JsonArray { shellCheck }, shellChecks: ["build"], job: "shell");
        var shellWork = Work("src/a.txt", "one", "shell");
        var shell = false;
        var allowed = await AcceptanceGate.Run(Gate(shellHash, shellWork, (request, events) =>
        {
            shell = request.Shell;
            events.Output("ok");
            events.Exit(0);
        }, job: "shell"));
        Assert.True(shell);
        Assert.Equal("pass", allowed.Status);

        var foreign = await AcceptanceGate.Run(Gate(pausedHash, work, (_, _) => calls++, initiatedBy: "webhook"));
        Assert.Equal("not-engine", foreign.Reason);
        Assert.Null(foreign.RunId);
        Assert.Equal("bad-stage", (await AcceptanceGate.Run(Gate(pausedHash, work, (_, _) => calls++, stage: "review"))).Reason);
        Assert.Equal("stale", (await AcceptanceGate.Run(Gate(pausedHash, work, (_, _) => calls++, snapshot: "rev-9"))).Reason);
        Assert.Equal(0, calls);

        var pointer = JsonNode.Parse(File.ReadAllText(Path.Combine(JobDir(), "job.json")))!.AsObject();
        pointer["manifestPath"] = "../acceptance.json";
        File.WriteAllText(Path.Combine(JobDir(), "job.json"), pointer.ToJsonString());
        Assert.Equal("path-escape", AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", pausedHash, Base).Reason);
        pointer["manifestPath"] = "acceptance.json";
        pointer["specRevision"] = 9;
        File.WriteAllText(Path.Combine(JobDir(), "job.json"), pointer.ToJsonString());
        Assert.Equal("stale", AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", pausedHash, Base).Reason);
        pointer["specRevision"] = 3;
        pointer["status"] = "specifying";
        File.WriteAllText(Path.Combine(JobDir(), "job.json"), pointer.ToJsonString());
        var waiting = AcceptanceGate.Load(Attempts(), Project, Job, "rev-1", pausedHash, Base);
        Assert.Equal("waiting", waiting.Status);
        Assert.Equal("missing-manifest", waiting.Reason);

        var rooted = EngineCheck("build", shellSha);
        rooted["arguments"] = new JsonArray { Attempts() };
        var rootedHash = WriteManifest(new JsonArray { rooted }, job: "rooted");
        Assert.Equal("corrupt-manifest", AcceptanceGate.Load(Attempts(), Project, "rooted", "rev-1", rootedHash, Base).Reason);
        var climbed = EngineCheck("build", Store("build", job: "climbed"));
        climbed["workingDirectory"] = "out/../secret";
        Assert.Equal("corrupt-manifest", AcceptanceGate.Load(Attempts(), Project, "climbed", "rev-1", WriteManifest(new JsonArray { climbed }, job: "climbed"), Base).Reason);

        var noteSha = Store("build", job: "note");
        var note = EngineCheck("build", noteSha);
        note["arguments"] = new JsonArray { "note " + Attempts() };
        var noteHash = WriteManifest(new JsonArray { note }, job: "note");
        var noted = await AcceptanceGate.Run(Gate(noteHash, work, (_, events) => { events.Output("ok"); events.Exit(0); }, job: "note"));
        Assert.Equal("error", noted.Status);
        Assert.Equal("path-escape", noted.Reason);
        var inside = Path.Combine(Attempts(), "nested");
        Directory.CreateDirectory(Path.Combine(inside, "src"));
        File.WriteAllText(Path.Combine(inside, "src", "a.txt"), "one");
        var escaped = await AcceptanceGate.Run(Gate(noteHash, inside, (_, _) => calls++, job: "note"));
        Assert.Equal("path-escape", escaped.Reason);
        Assert.Equal(0, calls);

        var oldHash = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "revise")) }, revision: 3, job: "revise");
        var reviseWork = Work("src/a.txt", "one", "revise");
        var oldPass = await AcceptanceGate.Run(Gate(oldHash, reviseWork, (_, events) => { events.Output("ok"); events.Exit(0); }, job: "revise"));
        var newHash = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "revise")) }, revision: 4, job: "revise");
        Assert.NotEqual(oldHash, newHash);
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "revise", oldPass.RunId!, Claim(oldPass)));
        Assert.Contains(oldHash, File.ReadAllText(Evidence(oldPass, "revise")));
        var revisedCalls = 0;
        var revised = await AcceptanceGate.Run(Gate(newHash, reviseWork, (_, events) => { revisedCalls++; events.Output("ok"); events.Exit(0); }, job: "revise"));
        Assert.Equal(1, revisedCalls);
        Assert.Equal("pass", revised.Status);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "revise", oldPass.RunId!, Claim(revised)));
    }

    [Fact]
    public async Task Artifact_bytes_unicode_paths_and_private_values_stay_bound()
    {
        var body = "結果";
        var artifact = AcceptanceGate.Sha256(Utf8.GetBytes(body));
        var check = EngineCheck("build", Store("build"), "artifact-sha256:out/結果.txt:" + artifact);
        var hash = WriteManifest(new JsonArray { check });
        var work = Work("src/テスト file.txt", "一");
        var outcome = await AcceptanceGate.Run(Gate(hash, work, (_, events) =>
        {
            Directory.CreateDirectory(Path.Combine(work, "out"));
            File.WriteAllText(Path.Combine(work, "out", "結果.txt"), body);
            events.Output("not the assertion");
            events.Exit(0);
        }, tracked: ["src/テスト file.txt"]));
        Assert.Equal("pass", outcome.Status);
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, outcome.RunId!, Claim(outcome)));
        var pin = AcceptanceGate.Pin(work, ["src/テスト file.txt"], null, ["out/結果.txt"]);
        Assert.Equal(outcome.ArtifactDigest, pin.ArtifactDigest);
        Assert.Contains("src/テスト file.txt", pin.Included);
        Assert.DoesNotContain("out/結果.txt", pin.Included);
        File.WriteAllText(Path.Combine(work, "out", "結果.txt"), "changed");
        var drifted = AcceptanceGate.Pin(work, ["src/テスト file.txt"], null, ["out/結果.txt"]);
        Assert.Equal(pin.TreeDigest, drifted.TreeDigest);
        Assert.NotEqual(pin.ArtifactDigest, drifted.ArtifactDigest);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, Job, outcome.RunId!, new AcceptanceGate.PassClaim
        {
            Stage = "pre-review",
            AttemptId = "attempt-1",
            SnapshotId = "rev-1",
            ManifestSha256 = outcome.ManifestSha256,
            WorktreeId = "wt-1",
            TreeDigest = drifted.TreeDigest,
            WorkingInputDigest = drifted.WorkingInputDigest,
            ArtifactDigest = drifted.ArtifactDigest,
            CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            CheckIds = outcome.CheckIds
        }));
        var wrong = await AcceptanceGate.Run(Gate(hash, work, (_, events) => { events.Output("ok"); events.Exit(0); }, tracked: ["src/テスト file.txt"], attempt: "attempt-2"));
        Assert.Equal("assertion-failed", wrong.Reason);

        var secret = EngineCheck("build", Store("build", job: "secret"), "artifact-sha256:.env:" + artifact);
        Assert.Equal("corrupt-manifest", AcceptanceGate.Load(Attempts(), Project, "secret", "rev-1", WriteManifest(new JsonArray { secret }, job: "secret"), Base).Reason);
        File.WriteAllText(Path.Combine(work, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(work, "noise.log"), "x");
        Assert.Equal("ignored-input", AcceptanceGate.Pin(work, ["src/テスト file.txt"], ["noise.log"], ["out/結果.txt"]).Reason);

        var loud = EngineCheck("build", Store("build", job: "loud"));
        var loudHash = WriteManifest(new JsonArray { loud }, job: "loud");
        var loudWork = Work("src/a.txt", "one", "loud");
        var tmp = Environment.GetEnvironmentVariable("TMP");
        var plan = Environment.GetEnvironmentVariable("PLANWHARF_TOKEN");
        var ask = Environment.GetEnvironmentVariable("GIT_ASKPASS");
        try
        {
            Environment.SetEnvironmentVariable("TMP", (tmp ?? "") + " ghp_secretvalueextra");
            Environment.SetEnvironmentVariable("PLANWHARF_TOKEN", "secret");
            Environment.SetEnvironmentVariable("GIT_ASKPASS", "ask");
            IReadOnlyDictionary<string, string>? seen = null;
            var blocked = await AcceptanceGate.Run(Gate(loudHash, loudWork, (request, events) =>
            {
                seen = request.Environment;
                events.Output(new string('z', 5_000) + " ghp_secretvalueextra");
                events.Exit(1);
            }, job: "loud"));
            Assert.Equal("nonzero-exit", blocked.Reason);
            Assert.DoesNotContain("ghp_", blocked.Log);
            Assert.True(blocked.Log.Length <= AcceptanceGate.MaxLogChars + 3);
            Assert.False(seen!.ContainsKey("PLANWHARF_TOKEN"));
            Assert.False(seen.ContainsKey("GIT_ASKPASS"));
            Assert.False(seen.ContainsKey("TMP"));
            var tail = JsonNode.Parse(File.ReadAllText(Evidence(blocked, "loud")))!["commands"]![0]!["outputTail"]!.GetValue<string>();
            Assert.DoesNotContain("ghp_", tail);
            Assert.True(tail.Length <= AcceptanceGate.MaxLogChars + 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", tmp);
            Environment.SetEnvironmentVariable("PLANWHARF_TOKEN", plan);
            Environment.SetEnvironmentVariable("GIT_ASKPASS", ask);
        }
    }

    [Fact]
    public async Task Default_start_kills_the_child_process()
    {
        var pidFile = Path.Combine(root, "child.pid");
        string executable;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(executable)) return;
            var script = Path.Combine(root, "parent.ps1");
            File.WriteAllText(script, "$p = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 40' -PassThru -WindowStyle Hidden\nSet-Content -LiteralPath $args[0] -Value $p.Id -NoNewline\n[Console]::Out.WriteLine('ready')\n[Console]::Out.Flush()\nStart-Sleep -Seconds 40\n", Utf8);
            arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, pidFile];
        }
        else
        {
            executable = "/bin/sh";
            if (!File.Exists(executable)) return;
            var script = Path.Combine(root, "parent.sh");
            File.WriteAllText(script, "sleep 40 &\necho $! > \"$1\"\necho ready\nwait\n", Utf8);
            arguments = [script, pidFile];
        }
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) env[name] = value;
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new AcceptanceGate.ProcessEvents
        {
            Output = text => { if (text.Contains("ready", StringComparison.Ordinal)) ready.TrySetResult(); },
            Exit = code => exited.TrySetResult(code)
        };
        AcceptanceGate.DefaultStart(new AcceptanceGate.ProcessRequest
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = root,
            Environment = env,
            TimeoutMs = 5_000,
            Shell = false,
            CheckId = "sleep"
        }, events);
        Assert.Same(ready.Task, await Task.WhenAny(ready.Task, Task.Delay(8_000)));
        var child = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        events.KillTree();
        Assert.Same(exited.Task, await Task.WhenAny(exited.Task, Task.Delay(8_000)));
        var alive = true;
        for (var i = 0; i < 20 && alive; i++)
        {
            await Task.Delay(100);
            try { alive = !System.Diagnostics.Process.GetProcessById(child).HasExited; }
            catch (ArgumentException) { alive = false; }
        }
        Assert.False(alive);
    }

    [Fact]
    public async Task Pinned_external_checks_are_verified_and_run_from_check_store()
    {
        var extSha = Store("ext-tool", "tool content", job: "ext");
        var check = new JsonObject
        {
            ["id"] = "ext-tool",
            ["purpose"] = "Pinned external tool",
            ["executable"] = "check.exe",
            ["arguments"] = new JsonArray { "--json" },
            ["workingDirectory"] = ".",
            ["timeoutSeconds"] = 5,
            ["expectedExitCode"] = 0,
            ["expectedResult"] = "verified output",
            ["shell"] = false,
            ["trust"] = "pinned-external",
            ["programSha256"] = extSha
        };
        var hash = WriteManifest(new JsonArray { check }, job: "ext");
        var work = Work("src/a.txt", "content", "ext-work");

        var calls = 0;
        var outcome = await AcceptanceGate.Run(Gate(hash, work, (request, events) =>
        {
            calls++;
            events.Output("verified output");
            events.Exit(0);
        }, job: "ext"));

        Assert.Equal("pass", outcome.Status);
        Assert.Equal(1, calls);
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "ext", outcome.RunId!, Claim(outcome)));

        // Tampered program in check-store fails with tamper
        File.WriteAllBytes(Path.Combine(JobDir("ext"), "check-store", "ext-tool", "check.exe"), "tampered"u8.ToArray());
        var tampered = await AcceptanceGate.Run(Gate(hash, work, (_, _) => calls++, job: "ext", attempt: "attempt-2"));
        Assert.Equal("blocked", tampered.Status);
        Assert.Equal("tamper", tampered.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Candidate_code_is_never_resumed_across_subsequent_runs()
    {
        var assertion = Store("assert", "trusted", "cand");
        var hash = WriteManifest(new JsonArray
        {
            CandidateCheck("build", "assert"),
            EngineCheck("assert", assertion, "ARTIFACT-OK")
        }, job: "cand");
        var work = Work("src/a.txt", "one", "cand-work");
        File.WriteAllText(Path.Combine(work, "candidate.exe"), "cand");

        var candidateRuns = 0;
        var assertionRuns = 0;
        AcceptanceGate.Starter start = (request, events) =>
        {
            if (request.CheckId == "build")
            {
                candidateRuns++;
                events.Output("candidate executed");
                events.Exit(0);
            }
            else if (request.CheckId == "assert")
            {
                assertionRuns++;
                events.Output("ARTIFACT-OK");
                events.Exit(0);
            }
        };

        var first = await AcceptanceGate.Run(Gate(hash, work, start, job: "cand", tracked: ["src/a.txt", "candidate.exe"]));
        Assert.Equal("pass", first.Status);
        Assert.Equal(1, candidateRuns);
        Assert.Equal(1, assertionRuns);

        // On a second run with identical snapshot, trusted engine assertion can be resumed,
        // but candidate-code MUST run again.
        var second = await AcceptanceGate.Run(Gate(hash, work, start, job: "cand", tracked: ["src/a.txt", "candidate.exe"]));
        Assert.Equal("pass", second.Status);
        Assert.Equal(2, candidateRuns); // candidate executed again!
        Assert.Equal(1, assertionRuns); // engine assertion was resumed from prior run
    }

    [Fact]
    public async Task Checks_with_valid_and_invalid_subdirectories_behave_appropriately()
    {
        var sha = Store("subcheck", job: "subdir");
        var validCheck = EngineCheck("subcheck", sha);
        validCheck["workingDirectory"] = "sub/dir";
        var hash = WriteManifest(new JsonArray { validCheck }, job: "subdir");
        var work = Work("src/a.txt", "one", "subdir-work");
        Directory.CreateDirectory(Path.Combine(work, "sub", "dir"));

        string? actualCwd = null;
        var ok = await AcceptanceGate.Run(Gate(hash, work, (request, events) =>
        {
            actualCwd = request.WorkingDirectory;
            events.Output("ok");
            events.Exit(0);
        }, job: "subdir"));

        Assert.Equal("pass", ok.Status);
        Assert.Equal(Path.Combine(work, "sub", "dir"), actualCwd);

        // When workingDirectory does not exist, fail with path-escape
        Directory.Delete(Path.Combine(work, "sub", "dir"));
        var escaped = await AcceptanceGate.Run(Gate(hash, work, (_, _) => { }, job: "subdir", attempt: "attempt-2"));
        Assert.Equal("error", escaped.Status);
        Assert.Equal("path-escape", escaped.Reason);
    }

    [Fact]
    public async Task PassClaim_rejects_omitted_checks_tampered_commands_and_mismatched_worktree()
    {
        var hash = WriteManifest(new JsonArray
        {
            EngineCheck("build", Store("build", job: "claim-test")),
            EngineCheck("test", Store("test", job: "claim-test"), "passed")
        }, job: "claim-test");
        var work = Work("src/a.txt", "one", "claim-work");

        var outcome = await AcceptanceGate.Run(Gate(hash, work, (request, events) =>
        {
            events.Output(request.CheckId == "test" ? "passed" : "ok");
            events.Exit(0);
        }, job: "claim-test"));
        Assert.Equal("pass", outcome.Status);

        // Mismatched worktreeId
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "claim-test", outcome.RunId!,
            Claim(outcome, worktree: "wt-different")));

        // Mismatched attemptId
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "claim-test", outcome.RunId!,
            Claim(outcome, attempt: "attempt-99")));

        // Claim checkIds includes a check that never ran
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "claim-test", outcome.RunId!,
            Claim(outcome, checks: ["build", "test", "deploy"])));

        // Tamper run.json: command 'test' marked as failed but top-level outcome forged to 'pass'
        var runPath = Evidence(outcome, "claim-test");
        var doc = JsonNode.Parse(File.ReadAllText(runPath))!.AsObject();
        var cmds = doc["commands"]!.AsArray();
        cmds[1]!["outcome"] = "assertion-failed";
        File.WriteAllBytes(runPath, Utf8.GetBytes(doc.ToJsonString()));
        Reseal(runPath);

        // Even though top-level outcome says pass and seal matches, test didn't pass so claim is rejected
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "claim-test", outcome.RunId!, Claim(outcome)));

        // Tamper run.json: command 'test' removed entirely from commands array
        cmds.RemoveAt(1);
        File.WriteAllBytes(runPath, Utf8.GetBytes(doc.ToJsonString()));
        Reseal(runPath);

        // Required check 'test' is missing from run evidence
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "claim-test", outcome.RunId!, Claim(outcome)));
    }

    [Fact]
    public async Task Git_exclude_ignores_files_and_modifications_invalidate_earlier_pass()
    {
        var hash = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "excl")) }, job: "excl");
        var work = Work("src/a.txt", "one", "excl-work");
        var gitInfo = Path.Combine(work, ".git", "info");
        Directory.CreateDirectory(gitInfo);
        File.WriteAllText(Path.Combine(gitInfo, "exclude"), "ignored.tmp\n");
        File.WriteAllText(Path.Combine(work, "ignored.tmp"), "should be ignored");

        // Pin should succeed because ignored.tmp is in exclude
        var pin = AcceptanceGate.Pin(work, ["src/a.txt"], null);
        Assert.Equal("pinned", pin.Status);
        Assert.DoesNotContain("ignored.tmp", pin.Included);

        var outcome = await AcceptanceGate.Run(Gate(hash, work, (_, events) =>
        {
            events.Output("ok");
            events.Exit(0);
        }, job: "excl"));
        Assert.Equal("pass", outcome.Status);
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "excl", outcome.RunId!, Claim(outcome)));

        // If .git/info/exclude changes, TreeDigest changes and earlier pass cannot be reused
        File.WriteAllText(Path.Combine(gitInfo, "exclude"), "ignored.tmp\nother.tmp\n");
        var modifiedPin = AcceptanceGate.Pin(work, ["src/a.txt"], null);
        Assert.NotEqual(outcome.TreeDigest, modifiedPin.TreeDigest);
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "excl", outcome.RunId!, new AcceptanceGate.PassClaim
        {
            Stage = "pre-review",
            AttemptId = "attempt-1",
            SnapshotId = "rev-1",
            ManifestSha256 = outcome.ManifestSha256,
            WorktreeId = "wt-1",
            TreeDigest = modifiedPin.TreeDigest,
            WorkingInputDigest = modifiedPin.WorkingInputDigest,
            ArtifactDigest = modifiedPin.ArtifactDigest,
            CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            CheckIds = outcome.CheckIds
        }));
    }

    [Fact]
    public async Task Request_validation_rejects_invalid_commit_tree_sha_and_worktree_boundaries()
    {
        var hash = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "val")) }, job: "val");
        var work = Work("src/a.txt", "one", "val-work");

        var calls = 0;
        AcceptanceGate.Starter start = (_, _) => calls++;

        // Non-hex or invalid length CommitSha
        var badCommit = await AcceptanceGate.Run(new AcceptanceGate.Request
        {
            AttemptsRoot = Attempts(),
            ProjectId = Project,
            JobId = "val",
            SnapshotId = "rev-1",
            ManifestSha256 = hash,
            BaseCommit = Base,
            Stage = "pre-review",
            AttemptId = "attempt-1",
            WorktreePath = work,
            WorktreeId = "wt-1",
            CommitSha = "invalid-not-40-hex",
            TrackedRelativePaths = ["src/a.txt"],
            Start = start
        });
        Assert.Equal("blocked", badCommit.Status);
        Assert.Equal("stale", badCommit.Reason);
        Assert.Equal(0, calls);

        // Non-hex or invalid length TreeSha
        var badTree = await AcceptanceGate.Run(new AcceptanceGate.Request
        {
            AttemptsRoot = Attempts(),
            ProjectId = Project,
            JobId = "val",
            SnapshotId = "rev-1",
            ManifestSha256 = hash,
            BaseCommit = Base,
            Stage = "pre-review",
            AttemptId = "attempt-1",
            WorktreePath = work,
            WorktreeId = "wt-1",
            TreeSha = "xyz",
            TrackedRelativePaths = ["src/a.txt"],
            Start = start
        });
        Assert.Equal("blocked", badTree.Status);
        Assert.Equal("stale", badTree.Reason);
        Assert.Equal(0, calls);

        // AttemptsRoot equals WorktreePath
        var sameDir = await AcceptanceGate.Run(new AcceptanceGate.Request
        {
            AttemptsRoot = work,
            ProjectId = Project,
            JobId = "val",
            SnapshotId = "rev-1",
            ManifestSha256 = hash,
            BaseCommit = Base,
            Stage = "pre-review",
            AttemptId = "attempt-1",
            WorktreePath = work,
            WorktreeId = "wt-1",
            TrackedRelativePaths = ["src/a.txt"],
            Start = start
        });
        Assert.Equal("error", sameDir.Status);
        Assert.Equal("path-escape", sameDir.Reason);
        Assert.Equal(0, calls);

        // Invalid WorktreeId with path traversal chars
        var badWorktreeId = await AcceptanceGate.Run(new AcceptanceGate.Request
        {
            AttemptsRoot = Attempts(),
            ProjectId = Project,
            JobId = "val",
            SnapshotId = "rev-1",
            ManifestSha256 = hash,
            BaseCommit = Base,
            Stage = "pre-review",
            AttemptId = "attempt-1",
            WorktreePath = work,
            WorktreeId = "../escape",
            TrackedRelativePaths = ["src/a.txt"],
            Start = start
        });
        Assert.Equal("error", badWorktreeId.Status);
        Assert.Equal("path-escape", badWorktreeId.Reason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Default_start_runs_shell_command_with_spaces_and_unicode()
    {
        string scriptFile;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            scriptFile = Path.Combine(root, "shell script é.cmd");
            File.WriteAllText(scriptFile, "@echo off\r\nchcp 65001 >nul\r\necho shell:%~1 %~2\r\n", Utf8);
            arguments = ["hello world", "テスト"];
        }
        else
        {
            scriptFile = Path.Combine(root, "shell script é.sh");
            File.WriteAllText(scriptFile, "#!/bin/sh\necho \"shell:$1 $2\"\n", Utf8);
            arguments = ["hello world", "テスト"];
            // A script written by the test is not executable on Linux and macOS until it is marked so.
            File.SetUnixFileMode(scriptFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) env[name] = value;
        }
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new AcceptanceGate.ProcessEvents
        {
            Output = text => output.Append(text),
            Exit = code => exited.TrySetResult(code)
        };
        AcceptanceGate.DefaultStart(new AcceptanceGate.ProcessRequest
        {
            Executable = scriptFile,
            Arguments = arguments,
            WorkingDirectory = root,
            Environment = env,
            TimeoutMs = 5_000,
            Shell = true,
            CheckId = "shell-test"
        }, events);
        Assert.Same(exited.Task, await Task.WhenAny(exited.Task, Task.Delay(5_000)));
        Assert.True(await exited.Task == 0, "Exit: " + await exited.Task + ", Output: " + output.ToString());
        Assert.Contains("shell:hello world テスト", output.ToString());
    }

    [Fact]
    public async Task Hex_case_in_commit_and_tree_sha_matches_in_claims()
    {
        var hash = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "case-job")) }, job: "case-job");
        var work = Work("src/a.txt", "one", "case-work");

        var outcome = await AcceptanceGate.Run(new AcceptanceGate.Request
        {
            AttemptsRoot = Attempts(),
            ProjectId = Project,
            JobId = "case-job",
            SnapshotId = "rev-1",
            ManifestSha256 = hash,
            BaseCommit = Base,
            Stage = "pre-review",
            AttemptId = "attempt-1",
            WorktreePath = work,
            WorktreeId = "wt-1",
            TrackedRelativePaths = ["src/a.txt"],
            CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            TreeSha = "cccccccccccccccccccccccccccccccccccccccc",
            Start = (_, events) => { events.Output("ok"); events.Exit(0); }
        });
        Assert.Equal("pass", outcome.Status);

        // Claim with uppercase commit and tree SHA must still match
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "case-job", outcome.RunId!, new AcceptanceGate.PassClaim
        {
            Stage = "pre-review",
            AttemptId = "attempt-1",
            SnapshotId = "rev-1",
            ManifestSha256 = hash,
            WorktreeId = "wt-1",
            TreeDigest = outcome.TreeDigest,
            WorkingInputDigest = outcome.WorkingInputDigest,
            ArtifactDigest = outcome.ArtifactDigest,
            CommitSha = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            TreeSha = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            CheckIds = outcome.CheckIds
        }));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_job_lock_records_cancelled_and_leaves_running_job()
    {
        var hash = WriteManifest(new JsonArray { EngineCheck("build", Store("build", job: "lock-job")) }, job: "lock-job");
        var work = Work("src/a.txt", "one", "lock-work");

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRun = AcceptanceGate.Run(Gate(hash, work, (_, events) =>
        {
            firstStarted.TrySetResult();
            firstCanFinish.Task.Wait(TimeSpan.FromSeconds(5));
            events.Output("ok");
            events.Exit(0);
        }, job: "lock-job", attempt: "attempt-1"));

        await firstStarted.Task;

        // Second run attempts to acquire the lock for the same job directory, but is cancelled
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        var secondRun = await AcceptanceGate.Run(Gate(hash, work, (_, _) => { }, job: "lock-job", attempt: "attempt-2", cancellation: cts.Token));

        Assert.Equal("blocked", secondRun.Status);
        Assert.Equal("cancelled", secondRun.Reason);

        firstCanFinish.TrySetResult();
        var firstOutcome = await firstRun;
        Assert.Equal("pass", firstOutcome.Status);

        // First run passed and is accepted; second run cancelled and is not accepted
        Assert.True(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "lock-job", firstOutcome.RunId!, Claim(firstOutcome)));
        Assert.False(AcceptanceGate.IsAcceptedPass(Attempts(), Project, "lock-job", secondRun.RunId!, Claim(secondRun, attempt: "attempt-2")));
    }

    void RewritePointer(string hash)
    {
        var job = JsonNode.Parse(File.ReadAllText(Path.Combine(JobDir(), "job.json")))!.AsObject();
        job["manifestSha256"] = hash;
        File.WriteAllText(Path.Combine(JobDir(), "job.json"), job.ToJsonString());
        var approval = JsonNode.Parse(File.ReadAllText(Path.Combine(JobDir(), "approval.json")))!.AsObject();
        approval["manifestSha256"] = hash;
        File.WriteAllText(Path.Combine(JobDir(), "approval.json"), approval.ToJsonString());
    }
}
