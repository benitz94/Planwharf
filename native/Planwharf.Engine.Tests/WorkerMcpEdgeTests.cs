using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class WorkerMcpEdgeTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw mcp edge ").FullName;
    public void Dispose() => Directory.Delete(root, true);

    static WorkerMcp.Bind Sample(string attempt = "E1", string model = "claude-sonnet") => new(
        "proj", Path.Combine("work", "tree"), "R-" + attempt, attempt, "S", "M", "claude", model);

    WorkerMcp.Call Connect(string token, long now = 1_000)
    {
        var attached = WorkerMcp.Attach(root, token, now);
        Assert.True(attached.Ok);
        return new WorkerMcp.Call(token, attached.Body!["connectionId"]!.GetValue<string>());
    }

    [Fact]
    public void Open_and_Attach_validate_all_inputs_and_bounds()
    {
        // Parameter validation on Open
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerMcp.Open(root, Sample(), askTimeoutMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerMcp.Open(root, Sample(), askTimeoutMs: -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerMcp.Open(root, Sample(), askTimeoutMs: WorkerMcp.MaxAskTimeoutMs + 1));

        Assert.Throws<ArgumentException>(() => WorkerMcp.Open(root, Sample() with { ProjectId = "" }));
        Assert.Throws<ArgumentException>(() => WorkerMcp.Open(root, Sample() with { ProjectId = "   " }));
        Assert.Throws<ArgumentException>(() => WorkerMcp.Open(root, Sample() with { ProjectId = "proj\nid" }));
        Assert.Throws<ArgumentException>(() => WorkerMcp.Open(root, Sample() with { ProjectId = new string('p', 201) }));
        Assert.Throws<ArgumentException>(() => WorkerMcp.Open(root, Sample() with { Provider = new string('x', 81) }));

        // Attach validation
        Assert.Equal("unknown-session", WorkerMcp.Attach(root, "", 1000).Code);
        Assert.Equal("unknown-session", WorkerMcp.Attach(root, "invalid-token", 1000).Code);
        Assert.Equal("unknown-session", WorkerMcp.Attach(root, new string('a', 63), 1000).Code);
        Assert.Equal("unknown-session", WorkerMcp.Attach(root, new string('g', 64), 1000).Code); // not hex
        Assert.Equal("unknown-session", WorkerMcp.Attach(root, new string('0', 64), 1000).Code); // nonexistent
    }

    [Fact]
    public void Ask_and_decision_edge_cases_and_cross_state_pausing()
    {
        var token = WorkerMcp.Open(root, Sample("AD1"));
        var call = Connect(token);

        // Malformed / bounds on Ask
        Assert.Equal("malformed", WorkerMcp.Ask(root, call, null, false, 100).Code);
        Assert.Equal("empty", WorkerMcp.Ask(root, call, "   ", false, 100).Code);
        Assert.Equal("unsupported-characters", WorkerMcp.Ask(root, call, "has\0null", false, 100).Code);
        Assert.Equal("too-long", WorkerMcp.Ask(root, call, new string('q', WorkerMcp.AskQuestionMax + 1), false, 100).Code);

        // Malformed / bounds on AnswerAsk
        Assert.Equal("malformed", WorkerMcp.AnswerAsk(root, token, null, false).Code);
        Assert.Equal("empty", WorkerMcp.AnswerAsk(root, token, "   ", false).Code);
        Assert.Equal("too-long", WorkerMcp.AnswerAsk(root, token, new string('a', WorkerMcp.AnswerMax + 1), false).Code);
        Assert.Equal("not-pending", WorkerMcp.AnswerAsk(root, token, "no question yet", false).Code);
        Assert.Equal("not-pending", WorkerMcp.CancelAsk(root, token).Code);
        Assert.Equal("not-pending", WorkerMcp.PollAsk(root, call, 100).Code);

        // Start an ask
        Assert.Equal("pending", WorkerMcp.Ask(root, call, "question one", false, 1000).Code);
        // Idempotent retry with identical question returns pending
        Assert.Equal("pending", WorkerMcp.Ask(root, call, "question one", false, 1010).Code);
        // Different question while ask pending is rejected
        Assert.Equal("ask-pending", WorkerMcp.Ask(root, call, "question two", false, 1020).Code);
        // Decision while ask pending is rejected with paused
        Assert.Equal("paused", WorkerMcp.Decision(root, call, "decision?", 1030).Code);
        // Answer ask
        Assert.True(WorkerMcp.AnswerAsk(root, token, "answered", false).Ok);

        // Malformed / bounds on Decision
        Assert.Equal("malformed", WorkerMcp.Decision(root, call, null, 1100).Code);
        Assert.Equal("empty", WorkerMcp.Decision(root, call, "   ", 1100).Code);
        Assert.Equal("too-long", WorkerMcp.Decision(root, call, new string('d', WorkerMcp.DecisionMax + 1), 1100).Code);
        Assert.Equal("not-pending", WorkerMcp.AnswerDecision(root, token, "no decision yet").Code);
        Assert.Equal("not-pending", WorkerMcp.CancelDecision(root, token).Code);
        Assert.Equal("not-pending", WorkerMcp.PollDecision(root, call, 1100).Code);

        // Start a decision
        Assert.Equal("pending", WorkerMcp.Decision(root, call, "decision one", 1200).Code);
        Assert.True(WorkerMcp.DependentsPaused(root, "S"));
        // Idempotent retry with identical question returns pending
        Assert.Equal("pending", WorkerMcp.Decision(root, call, "decision one", 1210).Code);
        // Different question while decision pending is rejected
        Assert.Equal("decision-pending", WorkerMcp.Decision(root, call, "decision two", 1220).Code);
        // Ask while decision pending is rejected with paused
        Assert.Equal("paused", WorkerMcp.Ask(root, call, "ask during decision?", false, 1230).Code);
        // AnswerDecision malformed / bounds
        Assert.Equal("too-long", WorkerMcp.AnswerDecision(root, token, new string('x', WorkerMcp.DecisionAnswerMax + 1)).Code);
        Assert.Equal("empty", WorkerMcp.AnswerDecision(root, token, "  ").Code);
        // Answer decision
        Assert.True(WorkerMcp.AnswerDecision(root, token, "answered decision").Ok);
        Assert.False(WorkerMcp.DependentsPaused(root, "S"));
    }

    [Fact]
    public void Handoff_edge_cases_and_multi_attempt_ordering()
    {
        var token1 = WorkerMcp.Open(root, Sample("H1", model: "model-a"));
        var call1 = Connect(token1);

        // Malformed / bounds
        Assert.Equal("malformed", WorkerMcp.Handoff(root, call1, null, 100).Code);
        Assert.Equal("empty", WorkerMcp.Handoff(root, call1, "   ", 100).Code);
        Assert.Equal("too-long", WorkerMcp.Handoff(root, call1, new string('h', WorkerMcp.HandoffMax + 1), 100).Code);

        Assert.True(WorkerMcp.Handoff(root, call1, "first notes", 500).Ok);
        Assert.Equal("duplicate", WorkerMcp.Handoff(root, call1, "second notes", 600).Code);

        // Second attempt on the same subtask from model-b leaves notes later
        var token2 = WorkerMcp.Open(root, Sample("H2", model: "model-b"));
        var call2 = Connect(token2);
        Assert.True(WorkerMcp.Handoff(root, call2, "newer notes", 1500).Ok);

        // Successor model-c on subtask S receives the newest notes ("newer notes")
        var brief = WorkerMcp.SuccessorBrief(root, "S", "model-c", "H3");
        Assert.Equal("newer notes", brief);

        // Caller from model-b does not get model-b's notes (picks model-a's notes)
        Assert.Equal("first notes", WorkerMcp.SuccessorBrief(root, "S", "model-b", "H3"));
    }

    static JsonObject SampleResult(string attempt = "F1") => new()
    {
        ["resultId"] = "R-" + attempt,
        ["attemptId"] = attempt,
        ["subTaskId"] = "S",
        ["megaTaskId"] = "M",
        ["status"] = "COMPLETE",
        ["summary"] = "verified done",
        ["filesChanged"] = new JsonArray(),
        ["tests"] = new JsonArray(),
        ["blockers"] = new JsonArray(),
        ["nextAction"] = "none"
    };

    [Fact]
    public void Finish_research_fields_and_detailed_field_bounds()
    {
        var token = WorkerMcp.Open(root, Sample("F1"));
        var call = Connect(token);

        // Valid finish with research fields and valid createdAt
        var valid = SampleResult("F1");
        valid["reportPath"] = Path.Combine(root, "report.md");
        valid["sources"] = new JsonArray("https://example.com/one", "https://example.com/two");
        valid["recommendedNextAction"] = "review findings";
        valid["createdAt"] = "2026-11-21T00:00:00Z";
        valid["tests"] = new JsonArray(new JsonObject
        {
            ["command"] = "dotnet test",
            ["status"] = "PASSED",
            ["evidence"] = "all green"
        });
        Assert.True(WorkerMcp.Finish(root, call, valid, 100).Ok);

        // Calling Finish on a new attempt with bad research fields
        var token2 = WorkerMcp.Open(root, Sample("F2"));
        var call2 = Connect(token2);

        var badResearch = SampleResult("F2");
        badResearch["reportPath"] = new string('p', WorkerMcp.ReportPathMax + 1);
        badResearch["recommendedNextAction"] = new string('r', WorkerMcp.RecommendedMax + 1);
        badResearch["createdAt"] = "invalid-date";
        var resErrors = WorkerMcp.Finish(root, call2, badResearch, 200);
        Assert.Equal("invalid", resErrors.Code);
        var resFields = resErrors.Body!["fields"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
        Assert.Contains("reportPath", resFields);
        Assert.Contains("recommendedNextAction", resFields);
        Assert.Contains("createdAt", resFields);

        // Bad test entries and authority keys
        var badTests = SampleResult("F2");
        badTests["tests"] = new JsonArray(new JsonObject
        {
            ["command"] = "test",
            ["status"] = "UNKNOWN_STATUS",
            ["evidence"] = "evidence"
        });
        var testErrors = WorkerMcp.Finish(root, call2, badTests, 300);
        Assert.Equal("invalid", testErrors.Code);
        Assert.Contains("tests", testErrors.Body!["fields"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Authority keys in finish are rejected
        foreach (var authKey in new[] { "token", "store", "storeRoot", "connectionId", "provider", "model", "cli" })
        {
            var withAuth = SampleResult("F2");
            withAuth[authKey] = "injected";
            Assert.Equal("identity-mismatch", WorkerMcp.Finish(root, call2, withAuth, 400).Code);
        }
    }

    [Fact]
    public void WorkerMcpServer_handles_json_rpc_edge_cases_and_protocols()
    {
        var token = WorkerMcp.Open(root, Sample("S1"));

        // ping method returns empty result
        var pingOutput = new StringWriter();
        var script = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n"
                   + "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"unknown_rpc_method\"}\n"
                   + "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"nonexistent_tool\"}}\n"
                   + "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"ask\",\"arguments\":{\"question\":\"hello\",\"forOwner\":\"not-bool\"}}}\n"
                   + "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"progress\",\"arguments\":12345}}\n";

        Assert.Equal(0, WorkerMcpServer.Serve(new StringReader(script), pingOutput, new StringWriter(), root, token));

        var lines = pingOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();

        // 1: ping
        Assert.NotNull(lines[0]["result"]);
        // 2: unknown method
        Assert.Equal(-32601, lines[1]!["error"]!["code"]!.GetValue<int>());
        // 3: unknown tool
        Assert.Contains("unknown-tool", lines[2].ToJsonString());
        // 4: ask with non-bool forOwner
        Assert.Contains("malformed", lines[3].ToJsonString());
        // 5: progress with non-object arguments
        Assert.Contains("malformed", lines[4].ToJsonString());

        // WorkerMcpServer with invalid token returns exit code 2
        var errOutput = new StringWriter();
        Assert.Equal(2, WorkerMcpServer.Serve(new StringReader(""), new StringWriter(), errOutput, root, "bad-token"));
        Assert.Contains("planwharf-mcp\tattach\tunknown-session", errOutput.ToString());
    }

    [Fact]
    public void WorkerMcpLaunch_validates_server_spec_and_formats_args()
    {
        // Missing / malformed server spec
        Assert.Equal("json-file", WorkerMcpLaunch.Register("claude", true, null).Transport);
        Assert.Equal("json-file", WorkerMcpLaunch.Register("claude", true, new WorkerMcpLaunch.ServerSpec("", [])).Transport);
        Assert.Equal("json-file", WorkerMcpLaunch.Register("claude", true, new WorkerMcpLaunch.ServerSpec("cmd\nwith\nnewlines", [])).Transport);
        Assert.Equal("json-file", WorkerMcpLaunch.Register("claude", true, new WorkerMcpLaunch.ServerSpec("valid-cmd", ["arg\nwith\nnewline"])).Transport);

        // Codex args formatting with ownerMcpEnabled true vs false
        var server = new WorkerMcpLaunch.ServerSpec("planwharf.exe", ["--serve", "root"]);
        var codexOwnerOff = WorkerMcpLaunch.Register("codex", false, server);
        Assert.True(codexOwnerOff.PlanwharfTools);
        Assert.Contains("mcp_servers={}", codexOwnerOff.ExtraArgs);

        var codexOwnerOn = WorkerMcpLaunch.Register("codex", true, server);
        Assert.True(codexOwnerOn.PlanwharfTools);
        Assert.DoesNotContain("mcp_servers={}", codexOwnerOn.ExtraArgs);
    }

    [Fact]
    public async Task Concurrent_progress_calls_across_sessions_and_audit_log_remain_consistent()
    {
        var count = 10;
        var tokens = new string[count];
        var calls = new WorkerMcp.Call[count];
        for (var i = 0; i < count; i++)
        {
            tokens[i] = WorkerMcp.Open(root, Sample("CP" + i));
            calls[i] = Connect(tokens[i]);
        }

        var gate = new Barrier(count);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var step = 0; step < 5; step++)
            {
                var rep = WorkerMcp.Progress(root, calls[i], "step-" + step, 1000 + step);
                Assert.True(rep.Ok);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        for (var i = 0; i < count; i++)
        {
            var progress = WorkerMcp.ProgressOf(root, tokens[i]);
            Assert.Equal(5, progress.Count);
        }

        var auditLines = File.ReadAllLines(Path.Combine(root, "audit.log"));
        Assert.NotEmpty(auditLines);
    }

    [Fact]
    public async Task Concurrent_operations_on_the_same_session_serialize_cleanly()
    {
        var token = WorkerMcp.Open(root, Sample("CS1"));
        var call = Connect(token);

        var count = 8;
        var gate = new Barrier(count);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            var rep = WorkerMcp.Progress(root, call, "item-" + i, 2000 + i);
            Assert.True(rep.Ok);
        })).ToArray();

        await Task.WhenAll(tasks);

        var progress = WorkerMcp.ProgressOf(root, token);
        Assert.Equal(count, progress.Count);
    }
}
