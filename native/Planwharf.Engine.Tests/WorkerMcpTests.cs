using System.Diagnostics;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class WorkerMcpTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw mcp ").FullName;
    public void Dispose() => Directory.Delete(root, true);

    static WorkerMcp.Bind Sample(string attempt = "A", string model = "claude-sonnet") => new(
        "proj", Path.Combine("work", "tree"), "R-" + attempt, attempt, "S", "M", "claude", model);

    WorkerMcp.Call Connect(string token, long now = 1_000)
    {
        var attached = WorkerMcp.Attach(root, token, now);
        Assert.True(attached.Ok);
        return new WorkerMcp.Call(token, attached.Body!["connectionId"]!.GetValue<string>());
    }

    [Fact]
    public void Progress_is_ordered_sanitized_and_never_marks_work_done()
    {
        var token = WorkerMcp.Open(root, Sample());
        var call = Connect(token);
        Assert.Equal("empty", WorkerMcp.Progress(root, call, "  ", 1100).Code);
        Assert.Equal("too-long", WorkerMcp.Progress(root, call, new string('x', WorkerMcp.ProgressMax + 1), 1200).Code);
        Assert.Equal("unsupported-characters", WorkerMcp.Progress(root, call, "line\nbreak", 1300).Code);
        Assert.Equal("malformed", WorkerMcp.Progress(root, call, null, 1400).Code);
        Assert.True(WorkerMcp.Progress(root, call, " reading ", 1500).Ok);
        Assert.True(WorkerMcp.Progress(root, call, "writing", 1600).Ok);
        var progress = WorkerMcp.ProgressOf(root, token);
        Assert.Equal(new[] { "reading", "writing" }, progress.Select(x => x!["text"]!.GetValue<string>()));
        Assert.Null(WorkerMcp.Read(root, token)!["finish"]);
        Assert.Equal("active", WorkerMcp.Read(root, token)!["state"]!.GetValue<string>());
        var secret = "sk-secret-value";
        Assert.Equal("too-long", WorkerMcp.Progress(root, call, secret + new string('y', WorkerMcp.ProgressMax), 1700).Code);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "audit.log")));
    }

    [Fact]
    public void A_late_call_and_a_different_attempt_do_not_change_progress()
    {
        var token = WorkerMcp.Open(root, Sample());
        var other = WorkerMcp.Open(root, Sample("B"));
        var call = Connect(token);
        var otherCall = Connect(other);
        Assert.True(WorkerMcp.Progress(root, call, "one", 2000).Ok);
        WorkerMcp.End(root, token);
        Assert.Equal("ended", WorkerMcp.Progress(root, call, "late", 2100).Code);
        Assert.Equal(new[] { "one" }, WorkerMcp.ProgressOf(root, token).Select(x => x!["text"]!.GetValue<string>()));
        Assert.Empty(WorkerMcp.ProgressOf(root, other));
        Assert.True(WorkerMcp.Progress(root, otherCall, "other", 2200).Ok);
        Assert.Equal(new[] { "one" }, WorkerMcp.ProgressOf(root, token).Select(x => x!["text"]!.GetValue<string>()));
        Assert.Equal("stale-connection", WorkerMcp.Progress(root, call with { ConnectionId = "nope" }, "x", 2300).Code);
    }

    [Fact]
    public void Ask_stays_on_its_attempt_until_the_routed_party_answers_cancels_or_times_out()
    {
        var token = WorkerMcp.Open(root, Sample(), askTimeoutMs: 50);
        var other = WorkerMcp.Open(root, Sample("B"));
        var call = Connect(token);
        Assert.Equal("pending", WorkerMcp.Ask(root, call, "which file?", false, 5_000).Code);
        Assert.Equal("wrong-route", WorkerMcp.AnswerAsk(root, token, "owner jumped in", true).Code);
        Assert.Equal("pending", WorkerMcp.PollAsk(root, call, 5_010).Code);
        Assert.True(WorkerMcp.AnswerAsk(root, other, "nope", false).Code is "not-pending");
        Assert.Equal("which file?", WorkerMcp.Read(root, token)!["ask"]!["question"]!.GetValue<string>());
        Assert.True(WorkerMcp.AnswerAsk(root, token, "the plan", false).Ok);
        Assert.Equal("the plan", WorkerMcp.PollAsk(root, call, 5_020).Body!["answer"]!.GetValue<string>());
        Assert.Equal("active", WorkerMcp.Read(root, token)!["state"]!.GetValue<string>());

        Assert.Equal("pending", WorkerMcp.Ask(root, call, "ship it?", true, 6_000).Code);
        Assert.Equal("wrong-route", WorkerMcp.AnswerAsk(root, token, "lead cannot", false).Code);
        Assert.True(WorkerMcp.AnswerAsk(root, token, "yes", true).Ok);
        Assert.Equal("yes", WorkerMcp.PollAsk(root, call, 6_010).Body!["answer"]!.GetValue<string>());

        Assert.Equal("pending", WorkerMcp.Ask(root, call, "wait", false, 7_000).Code);
        Assert.True(WorkerMcp.CancelAsk(root, token).Ok);
        Assert.Equal("cancelled", WorkerMcp.PollAsk(root, call, 7_010).Code);

        Assert.Equal("pending", WorkerMcp.Ask(root, call, "slow", false, 8_000).Code);
        Assert.Equal("timed-out", WorkerMcp.PollAsk(root, call, 8_050).Code);
        Assert.Equal("active", WorkerMcp.Read(root, token)!["state"]!.GetValue<string>());
        Assert.DoesNotContain("which file?", File.ReadAllText(Path.Combine(root, "audit.log")));
    }

    [Fact]
    public void A_restart_restores_a_pending_question_and_a_second_connection_cannot_take_it()
    {
        var token = WorkerMcp.Open(root, Sample(), askTimeoutMs: 5_000);
        var call = Connect(token, 1_000);
        Assert.Equal("pending", WorkerMcp.Ask(root, call, "still there?", false, 1_000).Code);
        Assert.Equal("busy-connection", WorkerMcp.Attach(root, token, 1_500).Code);
        var resumed = WorkerMcp.Attach(root, token, 1_000 + WorkerMcp.StaleConnectionMs, 0);
        Assert.True(resumed.Ok);
        var again = new WorkerMcp.Call(token, resumed.Body!["connectionId"]!.GetValue<string>());
        Assert.Equal("pending", WorkerMcp.PollAsk(root, again, 4_000).Code);
        Assert.Equal("still there?", WorkerMcp.Read(root, token)!["ask"]!["question"]!.GetValue<string>());
        Assert.True(WorkerMcp.AnswerAsk(root, token, "yes", false).Ok);
    }

    [Fact]
    public void Decision_is_kept_in_full_for_the_owner_and_pauses_that_subtask()
    {
        var token = WorkerMcp.Open(root, Sample());
        var other = WorkerMcp.Open(root, Sample("B"));
        var call = Connect(token);
        var otherCall = Connect(other);
        var question = "Choose\n" + new string('q', 20_000);
        Assert.Equal("too-long", WorkerMcp.Decision(root, call, new string('q', WorkerMcp.DecisionMax + 1), 100).Code);
        Assert.Null(WorkerMcp.Read(root, token)!["decision"]);
        Assert.Equal("pending", WorkerMcp.Decision(root, call, question, 200).Code);
        Assert.True(WorkerMcp.DependentsPaused(root, "S"));
        Assert.False(WorkerMcp.DependentsPaused(root, "other"));
        Assert.Equal("paused", WorkerMcp.Progress(root, call, "nope", 300).Code);
        var shown = WorkerMcp.PendingDecisions(root);
        Assert.Single(shown);
        Assert.True(shown[0]!["highlighted"]!.GetValue<bool>());
        Assert.Equal(question, shown[0]!["question"]!.GetValue<string>());
        Assert.Equal("not-pending", WorkerMcp.AnswerDecision(root, other, "steal").Code);
        Assert.Equal("pending", WorkerMcp.PollDecision(root, call, 400).Code);
        Assert.True(WorkerMcp.Progress(root, otherCall, "still working", 450).Ok);
        Assert.True(WorkerMcp.CancelDecision(root, token).Ok);
        Assert.False(WorkerMcp.DependentsPaused(root, "S"));
        Assert.Equal("cancelled", WorkerMcp.PollDecision(root, call, 500).Code);

        Assert.Equal("pending", WorkerMcp.Decision(root, call, question, 600).Code);
        var crashed = WorkerMcp.Attach(root, token, 700, staleAfterMs: 0);
        var resumed = new WorkerMcp.Call(token, crashed.Body!["connectionId"]!.GetValue<string>());
        Assert.Equal(question, WorkerMcp.PendingDecisions(root)[0]!["question"]!.GetValue<string>());
        Assert.True(WorkerMcp.AnswerDecision(root, token, "keep both").Ok);
        Assert.Equal("keep both", WorkerMcp.PollDecision(root, resumed, 800).Body!["answer"]!.GetValue<string>());
        Assert.False(WorkerMcp.DependentsPaused(root, "S"));
        Assert.Null(WorkerMcp.Read(root, token)!["finish"]);
    }

    [Fact]
    public void Handoff_reaches_only_the_next_model_on_the_same_subtask()
    {
        var token = WorkerMcp.Open(root, Sample(model: "claude-sonnet"));
        var call = Connect(token);
        const string notes = "port the lock first";
        Assert.True(WorkerMcp.Handoff(root, call, notes, 100).Ok);
        Assert.Equal("duplicate", WorkerMcp.Handoff(root, call, "again", 200).Code);
        Assert.Equal(notes, WorkerMcp.Read(root, token)!["handoff"]!["notes"]!.GetValue<string>());
        Assert.Null(WorkerMcp.SuccessorBrief(root, "S", "claude-sonnet", "next"));
        Assert.Null(WorkerMcp.SuccessorBrief(root, "other", "grok-4", "next"));
        Assert.Equal(notes, WorkerMcp.SuccessorBrief(root, "S", "grok-4", "next"));
        Assert.Null(WorkerMcp.SuccessorBrief(root, "S", "grok-4", "A"));
        var resumed = WorkerMcp.Attach(root, token, 300, staleAfterMs: 0);
        Assert.True(resumed.Ok);
        Assert.Equal(notes, WorkerMcp.SuccessorBrief(root, "S", "grok-4", "later"));
    }

    static JsonObject ResultFor(string attempt) => new()
    {
        ["resultId"] = "R-" + attempt,
        ["attemptId"] = attempt,
        ["subTaskId"] = "S",
        ["megaTaskId"] = "M",
        ["status"] = "COMPLETE",
        ["summary"] = "done",
        ["filesChanged"] = new JsonArray(),
        ["tests"] = new JsonArray(),
        ["blockers"] = new JsonArray(),
        ["nextAction"] = "none",
        ["unknown"] = new JsonObject { ["keep"] = true }
    };

    [Fact]
    public void Finish_rejects_a_bad_claim_then_keeps_one_corrected_result()
    {
        var token = WorkerMcp.Open(root, Sample());
        var call = Connect(token);
        var missing = ResultFor("A");
        missing.Remove("summary");
        var rejected = WorkerMcp.Finish(root, call, missing, 100);
        Assert.Equal("invalid", rejected.Code);
        Assert.Contains("summary", rejected.Body!["fields"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(WorkerMcp.Read(root, token)!["finish"]);
        var wrong = ResultFor("A");
        wrong["attemptId"] = "other-secret-id";
        var mismatch = WorkerMcp.Finish(root, call, wrong, 200);
        Assert.Equal("identity-mismatch", mismatch.Code);
        Assert.DoesNotContain("other-secret-id", mismatch.Body?.ToJsonString() ?? "");
        var badStatus = ResultFor("A");
        badStatus["status"] = "DONE";
        Assert.Contains("status", WorkerMcp.Finish(root, call, badStatus, 300).Body!["fields"]!.AsArray().Select(x => x!.GetValue<string>()));
        var secret = "sk-secret-value";
        var oversized = ResultFor("A");
        oversized["summary"] = secret + new string('s', WorkerMcp.SummaryMax);
        Assert.Equal("invalid", WorkerMcp.Finish(root, call, oversized, 400).Code);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "audit.log")));
        var accepted = ResultFor("A");
        Assert.True(WorkerMcp.Finish(root, call, accepted, 500).Ok);
        Assert.True(WorkerMcp.Finish(root, call, accepted.DeepClone().AsObject(), 600).Ok);
        Assert.Equal(1, WorkerMcp.Read(root, token)!["finishWrites"]!.GetValue<int>());
        var changed = accepted.DeepClone().AsObject();
        changed["summary"] = "different";
        Assert.Equal("already-accepted", WorkerMcp.Finish(root, call, changed, 700).Code);
        Assert.Equal("done", WorkerMcp.Read(root, token)!["finish"]!["summary"]!.GetValue<string>());
        Assert.True(WorkerMcp.Read(root, token)!["finish"]!["unknown"]!["keep"]!.GetValue<bool>());
        WorkerMcp.End(root, token);
        Assert.Equal("ended", WorkerMcp.Finish(root, call, ResultFor("A"), 800).Code);
        Assert.Equal(1, WorkerMcp.Read(root, token)!["finishWrites"]!.GetValue<int>());
    }

    [Fact]
    public void Process_exit_and_false_file_claims_are_not_completion()
    {
        var folder = Directory.CreateTempSubdirectory("pw claim ").FullName;
        try
        {
            var token = WorkerMcp.Open(root, Sample());
            var call = Connect(token);
            var claim = ResultFor("A");
            claim["filesChanged"] = new JsonArray("ghost.txt");
            Assert.True(WorkerMcp.Finish(root, call, claim, 100).Ok);
            var cleanExit = WorkerMcp.Collect(root, token, 0, folder, "2026-11-21T00:00:00Z");
            Assert.False(cleanExit.Completed);
            Assert.Equal("false-claim", cleanExit.Reason);
            Assert.Equal("FAILED", cleanExit.Result!["status"]!.GetValue<string>());
            Assert.Equal("COMPLETE", WorkerMcp.Read(root, token)!["finish"]!["status"]!.GetValue<string>());
            var crashed = WorkerMcp.Collect(root, token, 3, folder, "2026-11-21T00:00:00Z");
            Assert.False(crashed.Completed);
            Assert.Equal("process-exit", crashed.Reason);
            Assert.Null(crashed.Result);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Json_fallback_matches_an_mcp_finish_and_survives_a_broken_setup()
    {
        var folder = Directory.CreateTempSubdirectory("pw json ").FullName;
        try
        {
            var mcpToken = WorkerMcp.Open(root, Sample());
            var mcpCall = Connect(mcpToken);
            var body = ResultFor("A");
            body["tests"] = JsonNode.Parse("""[{"command":"dotnet test","status":"PASSED","evidence":"seen"}]""");
            Assert.True(WorkerMcp.Finish(root, mcpCall, body, 100).Ok);
            var mcp = WorkerMcp.Collect(root, mcpToken, 0, folder, "2026-11-21T00:00:00Z");
            var jsonToken = WorkerMcp.Open(root, Sample("J"));
            WorkerMcp.RecordSetupFailure(root, jsonToken);
            Assert.Equal("mcp-setup-failed", WorkerMcp.Read(root, jsonToken)!["recovery"]!.GetValue<string>());
            Assert.Null(WorkerMcp.Read(root, jsonToken)!["finish"]);
            Assert.False(WorkerMcp.Collect(root, jsonToken, 0, folder, "2026-11-21T00:00:00Z").Completed);
            var jsonBody = ResultFor("J");
            jsonBody["tests"] = body["tests"]!.DeepClone();
            var resultPath = Path.Combine(folder, "result.json");
            var exitPath = Path.Combine(folder, "exit.json");
            File.WriteAllText(resultPath, jsonBody.ToJsonString());
            File.WriteAllText(exitPath, """{"attemptId":"J","provider":"claude","model":"claude-sonnet","exitCode":0,"createdAt":"2026-11-21T00:00:00Z"}""");
            var identity = JsonNode.Parse("""{"resultId":"R-J","attemptId":"J","subTaskId":"S","megaTaskId":"M","provider":"claude","cli":"claude-sonnet"}""")!.AsObject();
            var json = WorkerMcp.CollectAttempt(root, jsonToken, 0, folder, "2026-11-21T00:00:00Z", resultPath, identity, exitPath);
            Assert.True(mcp.Completed);
            Assert.True(json.Completed);
            foreach (var key in new[] { "status", "summary", "nextAction" })
                Assert.Equal(mcp.Result![key]!.ToJsonString(), json.Result![key]!.ToJsonString());
            Assert.Equal(mcp.Result!["tests"]!.ToJsonString(), json.Result!["tests"]!.ToJsonString());
            Assert.True(File.Exists(resultPath));
            Assert.NotNull(WorkerMcp.Read(root, mcpToken)!["finish"]);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Planwharf_tools_stay_available_whether_or_not_owner_mcp_is_on()
    {
        var server = new WorkerMcpLaunch.ServerSpec("planwharf-mcp", ["--planwharf-mcp-serve", root, "token"]);
        foreach (var owner in new[] { false, true })
        {
            var claude = WorkerMcpLaunch.Register("claude", owner, server);
            Assert.Equal("mcp", claude.Transport);
            Assert.True(claude.PlanwharfTools);
            var configArg = claude.ExtraArgs[^1];
            var servers = JsonNode.Parse(configArg)!["mcpServers"]!.AsObject();
            Assert.Equal(["planwharf"], servers.Select(pair => pair.Key));
            Assert.Equal("planwharf-mcp", servers["planwharf"]!["command"]!.GetValue<string>());
            Assert.Equal(owner, !claude.ExtraArgs.Contains("--strict-mcp-config"));
            var codex = WorkerMcpLaunch.Register("codex", owner, server);
            Assert.True(codex.PlanwharfTools);
            Assert.Contains(codex.ExtraArgs, arg => arg.StartsWith("mcp_servers.planwharf.command=", StringComparison.Ordinal));
            Assert.Equal(!owner, codex.ExtraArgs.Contains("mcp_servers={}"));
        }
        foreach (var adapter in new[] { "grok", "antigravity", "api", "configured-cli" })
        {
            var registered = WorkerMcpLaunch.Register(adapter, true, server);
            Assert.Equal("json-file", registered.Transport);
            Assert.False(registered.PlanwharfTools);
            Assert.Empty(registered.ExtraArgs);
        }
    }

    [Fact]
    public void The_stdio_server_rejects_bad_input_before_side_effects()
    {
        var token = WorkerMcp.Open(root, Sample());
        var output = new StringWriter();
        var script = "not-json sk-secret-value\n" + Rpc(1, "initialize") + Rpc(2, "tools/list")
            + Rpc(3, "tools/call", """{"name":"progress","arguments":{"status":1,"attemptId":"other"}}""")
            + Rpc(4, "tools/call", """{"name":"progress","arguments":{"status":"reading"}}""");
        Assert.Equal(0, WorkerMcpServer.Serve(new StringReader(script), output, new StringWriter(), root, token));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal(-32700, lines[0]!["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain("sk-secret-value", output.ToString());
        Assert.Equal("planwharf", lines[1]!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(5, lines[2]!["result"]!["tools"]!.AsArray().Count);
        Assert.Contains("identity-mismatch", lines[3]!.ToJsonString());
        Assert.Contains("\"text\":\"ok\"", lines[4]!.ToJsonString());
        Assert.Equal(["reading"], WorkerMcp.ProgressOf(root, token).Select(item => item!["text"]!.GetValue<string>()));
        WorkerMcp.End(root, token);
        var late = new StringWriter();
        var again = WorkerMcp.Open(root, Sample("L"));
        WorkerMcp.End(root, again);
        Assert.Equal(0, WorkerMcpServer.Serve(new StringReader(Rpc(1, "tools/call", """{"name":"progress","arguments":{"status":"late"}}""")), late, new StringWriter(), root, again));
        Assert.Contains("ended", late.ToString());
        Assert.Empty(WorkerMcp.ProgressOf(root, again));
    }

    [Fact]
    public async Task Ask_and_decision_finish_only_when_the_right_party_answers()
    {
        var token = WorkerMcp.Open(root, Sample(), askTimeoutMs: 5_000);
        var script = Rpc(1, "tools/call", """{"name":"ask","arguments":{"question":"which file?"}}""")
            + Rpc(2, "tools/call", """{"name":"decision","arguments":{"question":"keep the name?"}}""")
            + Rpc(3, "tools/call", """{"name":"progress","arguments":{"status":"after"}}""")
            + Rpc(4, "tools/call", """{"name":"handoff","arguments":{"notes":"next model"}}""");
        var output = new StringWriter();
        var task = Task.Run(() => WorkerMcpServer.Serve(new StringReader(script), output, new StringWriter(), root, token));
        WaitUntil(() => WorkerMcp.Read(root, token)?["ask"]?["status"]?.GetValue<string>() == "pending");
        Assert.Equal(2, WorkerMcpServer.Serve(new StringReader(""), new StringWriter(), new StringWriter(), root, token));
        Assert.True(WorkerMcp.AnswerAsk(root, token, "plan.md", false).Ok);
        WaitUntil(() => WorkerMcp.Read(root, token)?["decision"]?["status"]?.GetValue<string>() == "pending");
        Assert.True(WorkerMcp.DependentsPaused(root, "S"));
        Assert.True(WorkerMcp.AnswerDecision(root, token, "keep it").Ok);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("plan.md", output.ToString());
        Assert.Contains("keep it", output.ToString());
        Assert.Equal(["after"], WorkerMcp.ProgressOf(root, token).Select(item => item!["text"]!.GetValue<string>()));
        Assert.Null(WorkerMcp.Read(root, token)!["finish"]);
        Assert.Equal("next model", WorkerMcp.SuccessorBrief(root, "S", "grok-4", "later"));
    }

    [Fact]
    public async Task A_fake_worker_calls_every_tool_through_the_launched_server()
    {
        var exe = FakeExecutable();
        Assert.True(File.Exists(exe), exe);
        var token = WorkerMcp.Open(root, Sample(), askTimeoutMs: 10_000);
        var server = new WorkerMcpLaunch.ServerSpec(exe, ["--planwharf-mcp-serve", root, token]);
        var registration = WorkerMcpLaunch.Register("claude", false, server);
        var configured = JsonNode.Parse(registration.ExtraArgs[^1])!["mcpServers"]!["planwharf"]!;
        Assert.Equal(exe, configured["command"]!.GetValue<string>());
        Assert.Contains("--strict-mcp-config", registration.ExtraArgs);
        var good = ResultFor("A");
        var bad = good.DeepClone().AsObject();
        bad.Remove("summary");
        var calls = new JsonArray();
        calls.Add((JsonNode)ToolCall("progress", new JsonObject { ["status"] = "reading" }));
        calls.Add((JsonNode)ToolCall("ask", new JsonObject { ["question"] = "which file?" }));
        calls.Add((JsonNode)ToolCall("decision", new JsonObject { ["question"] = "ship it?" }));
        calls.Add((JsonNode)ToolCall("handoff", new JsonObject { ["notes"] = "left notes" }));
        calls.Add((JsonNode)ToolCall("finish", bad));
        calls.Add((JsonNode)ToolCall("finish", good));
        var launchPath = Path.Combine(root, "launch.json");
        var transcriptPath = Path.Combine(root, "transcript.json");
        File.WriteAllText(launchPath, new JsonObject
        {
            ["transport"] = "mcp",
            ["root"] = root,
            ["token"] = token,
            ["server"] = new JsonObject { ["command"] = registration.Server!.Command, ["args"] = configured["args"]!.DeepClone() },
            ["calls"] = calls
        }.ToJsonString());
        using var worker = StartFake(exe, "--planwharf-mcp-worker", launchPath, transcriptPath);
        WaitUntil(() => WorkerMcp.Read(root, token)?["ask"]?["status"]?.GetValue<string>() == "pending", 8_000);
        Assert.True(WorkerMcp.AnswerAsk(root, token, "plan.md", false).Ok);
        WaitUntil(() => WorkerMcp.Read(root, token)?["decision"]?["status"]?.GetValue<string>() == "pending", 8_000);
        Assert.True(WorkerMcp.AnswerDecision(root, token, "ship").Ok);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, worker.ExitCode);
        var transcript = JsonNode.Parse(File.ReadAllText(transcriptPath))!;
        Assert.Equal(["ask", "progress", "finish", "decision", "handoff"], transcript["tools"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(["ok", "plan.md", "ship", "ok", "invalid:summary", "ok"], transcript["results"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(["reading"], WorkerMcp.ProgressOf(root, token).Select(item => item!["text"]!.GetValue<string>()));
        Assert.Equal("done", WorkerMcp.Read(root, token)!["finish"]!["summary"]!.GetValue<string>());
        Assert.Equal(1, WorkerMcp.Read(root, token)!["finishWrites"]!.GetValue<int>());
        Assert.Equal("left notes", WorkerMcp.SuccessorBrief(root, "S", "grok-4", "later"));
        Assert.True(WorkerMcp.Collect(root, token, 0, root, "2026-11-21T00:00:00Z").Completed);
        Assert.False(WorkerMcp.Collect(root, token, 1, root, "2026-11-21T00:00:00Z").Completed);
    }

    [Fact]
    public async Task A_worker_without_mcp_and_a_broken_server_keep_the_same_contract()
    {
        var exe = FakeExecutable();
        var folder = Directory.CreateTempSubdirectory("pw json proc ").FullName;
        try
        {
            var jsonToken = WorkerMcp.Open(root, Sample("J"));
            var resultPath = Path.Combine(folder, "result.json");
            var body = ResultFor("J");
            body["tests"] = JsonNode.Parse("""[{"command":"dotnet test","status":"PASSED","evidence":"seen"}]""");
            var jsonLaunch = Path.Combine(root, "json-launch.json");
            File.WriteAllText(jsonLaunch, new JsonObject { ["transport"] = "json-file", ["resultFile"] = resultPath, ["result"] = body }.ToJsonString());
            using (var jsonWorker = StartFake(exe, "--planwharf-mcp-worker", jsonLaunch, Path.Combine(root, "json-transcript.json")))
            {
                await jsonWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, jsonWorker.ExitCode);
            }
            File.WriteAllText(Path.Combine(folder, "exit.json"), """{"attemptId":"J","provider":"claude","model":"claude-sonnet","exitCode":0,"createdAt":"2026-11-21T00:00:00Z"}""");
            var identity = JsonNode.Parse("""{"resultId":"R-J","attemptId":"J","subTaskId":"S","megaTaskId":"M","provider":"claude","cli":"claude-sonnet"}""")!.AsObject();
            var json = WorkerMcp.CollectJson(resultPath, identity, Path.Combine(folder, "exit.json"), folder);
            var mcpToken = WorkerMcp.Open(root, Sample("K"));
            var mcpCall = Connect(mcpToken);
            var mcpBody = ResultFor("K");
            mcpBody["tests"] = body["tests"]!.DeepClone();
            Assert.True(WorkerMcp.Finish(root, mcpCall, mcpBody, 100).Ok);
            var mcp = WorkerMcp.Collect(root, mcpToken, 0, folder, "2026-11-21T00:00:00Z");
            Assert.True(json.Completed);
            Assert.True(mcp.Completed);
            Assert.Equal(mcp.Result!["tests"]!.ToJsonString(), json.Result!["tests"]!.ToJsonString());
            Assert.Equal(mcp.Result!["summary"]!.ToJsonString(), json.Result!["summary"]!.ToJsonString());

            var broken = WorkerMcp.Open(root, Sample("Z"));
            var brokenLaunch = Path.Combine(root, "broken-launch.json");
            File.WriteAllText(brokenLaunch, new JsonObject
            {
                ["transport"] = "mcp",
                ["root"] = root,
                ["token"] = broken,
                ["server"] = new JsonObject { ["command"] = "planwharf-missing-mcp-host", ["args"] = new JsonArray() }
            }.ToJsonString());
            using var brokenWorker = StartFake(exe, "--planwharf-mcp-worker", brokenLaunch, Path.Combine(root, "broken-transcript.json"));
            await brokenWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, brokenWorker.ExitCode);
            Assert.Equal("mcp-setup-failed", JsonNode.Parse(File.ReadAllText(Path.Combine(root, "broken-transcript.json")))!["recovery"]!.GetValue<string>());
            Assert.Equal("mcp-setup-failed", WorkerMcp.Read(root, broken)!["recovery"]!.GetValue<string>());
            Assert.False(WorkerMcp.Collect(root, broken, 0, folder, "2026-11-21T00:00:00Z").Completed);
            Assert.Null(WorkerMcp.Read(root, broken)!["finish"]);
        }
        finally { Directory.Delete(folder, true); }
    }

    static Process StartFake(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    static string FakeExecutable()
    {
        var testDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(testDir);
        var configuration = Path.GetFileName(Path.GetDirectoryName(testDir)!);
        var native = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
        var name = OperatingSystem.IsWindows() ? "Planwharf.FakeCli.exe" : "Planwharf.FakeCli";
        return Path.Combine(native, "Planwharf.FakeCli", "bin", configuration, tfm, name);
    }

    static JsonObject ToolCall(string name, JsonObject arguments) => new() { ["name"] = name, ["arguments"] = arguments };

    [Fact]
    public async Task Parallel_attempts_keep_their_own_questions_and_a_recipient_is_rejected()
    {
        var first = WorkerMcp.Open(root, Sample("P1"));
        var second = WorkerMcp.Open(root, Sample("P2"));
        var firstCall = Connect(first);
        var secondCall = Connect(second);
        var gate = new Barrier(2);
        var asks = await Task.WhenAll(
            Task.Run(() => { gate.SignalAndWait(); return WorkerMcp.Ask(root, firstCall, "from one", false, 2_000).Code; }),
            Task.Run(() => { gate.SignalAndWait(); return WorkerMcp.Ask(root, secondCall, "from two", false, 2_000).Code; }));
        Assert.Equal(["pending", "pending"], asks);
        Assert.True(WorkerMcp.AnswerAsk(root, second, "two", false).Ok);
        Assert.Equal("pending", WorkerMcp.PollAsk(root, firstCall, 2_100).Code);
        Assert.Equal("two", WorkerMcp.PollAsk(root, secondCall, 2_100).Body!["answer"]!.GetValue<string>());
        Assert.True(WorkerMcp.AnswerAsk(root, first, "one", false).Ok);
        Assert.Equal("one", WorkerMcp.PollAsk(root, firstCall, 2_200).Body!["answer"]!.GetValue<string>());
        Assert.Equal("from two", WorkerMcp.Read(root, second)!["ask"]!["question"]!.GetValue<string>());
        WorkerMcp.Detach(root, firstCall, 3_000);
        var output = new StringWriter();
        var script = Rpc(1, "tools/call", """{"name":"progress","arguments":{"status":"nope","recipient":"owner"}}""")
            + Rpc(2, "tools/call", """{"name":"finish","arguments":{"recipient":"owner","resultId":"R-P1","attemptId":"P1","subTaskId":"S","megaTaskId":"M","status":"COMPLETE","summary":"done","filesChanged":[],"tests":[],"blockers":[],"nextAction":"none"}}""");
        Assert.Equal(0, WorkerMcpServer.Serve(new StringReader(script), output, new StringWriter(), root, first));
        Assert.Contains("identity-mismatch", output.ToString());
        Assert.Empty(WorkerMcp.ProgressOf(root, first));
        Assert.Null(WorkerMcp.Read(root, first)!["finish"]);
        Assert.Equal("from two", WorkerMcp.Read(root, second)!["ask"]!["question"]!.GetValue<string>());
    }

    static string Rpc(int id, string method, string? parameters = null) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString() + ",\"method\":\"" + method + "\"" + (parameters is null ? "" : ",\"params\":" + parameters) + "}\n";

    static void WaitUntil(Func<bool> ready, int timeoutMs = 3_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!ready())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("timed out waiting for the worker session");
            Thread.Sleep(10);
        }
    }
}
