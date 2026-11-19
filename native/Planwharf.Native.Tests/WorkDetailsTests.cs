using System.Diagnostics;
using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class WorkDetailsTests
{
    static readonly string[] Rows =
    [
        """{ "subTasks": [ { "id": "s1", "title": "Build it", "status": "COMPLETE", "assignedModelId": "opus" }, { "id": "s2", "title": "Verify the work independently against the job.", "status": "COMPLETE", "assignedModelId": "astra", "takeover": { "fromModelId": "grok" } } ], "results": [ { "subTaskId": "s1", "status": "COMPLETE", "testsReported": 2 }, { "subTaskId": "s2", "status": "BLOCKED", "summary": "Model changed.", "modelChange": true } ] }""",
        """{ "subTasks": [ { "id": "s1", "status": "COMPLETE", "assignedModelId": "opus" }, { "id": "s2", "type": "AUDIT", "status": "RUNNING", "fallbackFrom": "opus", "assignedModelId": "zeta" } ], "results": [ { "subTaskId": "s2", "status": "FAILED", "summary": "  Tests fail on Windows. " }, { "subTaskId": "x", "status": "BLOCKED" } ] }""",
        """{ "subTasks": [ { "id": "s1", "type": "RESEARCH", "status": "COMPLETE" } ], "results": [ { "subTaskId": "s1", "status": "COMPLETE", "testsReported": 1 } ] }"""
    ];
    static readonly (string? Stop, int? Changed)[] Cases = [("GOAL_REACHED", 3), ("LEAD_UNCONFIRMED: no answer", 0), (null, null), ("TIME_LIMIT", 1)];
    static string Label(string id) => id switch { "opus" => "Opus 5.5", "astra" => "GPT-6 Astra", "grok" => "Grok 4.7", _ => "" };

    // The native summary says exactly what the web and desktop summary (work-summary.js) says.
    [Fact]
    public void Summary_matches_the_chat_page()
    {
        var app = ServerHost.FindAppFolder(AppContext.BaseDirectory)!;
        var script = """
            const { workSummary } = require(process.argv[1]);
            const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
            const labels = { opus: 'Opus 5.5', astra: 'GPT-6 Astra', grok: 'Grok 4.7' };
            process.stdout.write(JSON.stringify(input.map(({ row, stop, changed }) => workSummary(row, { stopReason: stop, changedFiles: changed, modelLabel: id => labels[id] || '' }))));
            """;
        var input = new JsonArray(Rows.SelectMany(row => Cases.Select(c => (JsonNode)new JsonObject { ["row"] = JsonNode.Parse(row), ["stop"] = c.Stop, ["changed"] = c.Changed })).ToArray());
        var node = Process.Start(new ProcessStartInfo(ServerHost.FindNode(AppContext.BaseDirectory)) { ArgumentList = { "-e", script, Path.Combine(app, "work-summary.js") }, RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        node.StandardInput.Write(input.ToJsonString()); node.StandardInput.Close();
        var expected = JsonNode.Parse(node.StandardOutput.ReadToEnd())!.AsArray();
        node.WaitForExit();
        var i = 0;
        foreach (var row in Rows)
            foreach (var (stop, changed) in Cases)
                Assert.Equal(expected[i++]!.AsArray().Select(line => line!.GetValue<string>()), WorkDetails.Summary(JsonNode.Parse(row)!.AsObject(), stop, changed, Label));
    }

    [Fact]
    public void Durations_read_as_in_the_chat_page()
    {
        Assert.Equal("1 s", WorkDetails.Duration(200)); Assert.Equal("45 s", WorkDetails.Duration(45000));
        Assert.Equal("4 min", WorkDetails.Duration(240000)); Assert.Equal("1.5 h", WorkDetails.Duration(5400000));
        Assert.Equal("", WorkDetails.Duration(-1)); Assert.Equal("", WorkDetails.Duration(null));
    }

    [Fact]
    public void Usage_says_what_is_not_reported_and_never_estimates()
    {
        var snapshot = JsonNode.Parse("""
        { "aiUsage": { "recent": [
          { "megaTaskId": "MT-1", "modelId": "opus", "durationMs": 120000, "tokens": { "input": 12000, "output": 3400, "cachedInput": 9000 } },
          { "megaTaskId": "MT-1", "modelId": "opus", "durationMs": 60000, "tokens": { "input": 1000, "output": 600, "cachedInput": 0 } },
          { "megaTaskId": "MT-1", "modelId": "astra", "durationMs": 30000, "tokens": { "input": 500 } },
          { "megaTaskId": "MT-2", "modelId": "opus", "durationMs": 999999 } ] } }
        """)!;
        var row = JsonNode.Parse("""{ "megaTask": { "id": "MT-1" }, "subTasks": [ { "assignedModelId": "opus" }, { "assignedModelId": "astra" }, { "assignedModelId": "grok" } ] }""")!.AsObject();
        var usage = WorkDetails.Usage(snapshot, row, new Dictionary<string, int?> { ["opus"] = 80 }, Label, id => id == "opus" ? 71 : id == "astra" ? 38 : null);
        Assert.Equal(("Opus 5.5", "3 min · 13,000 in · 4,000 out · 9,000 from cache · quota 80% → 71%"), usage[0]);
        Assert.Equal(("GPT-6 Astra", "30 s · 500 in · quota now 38% left"), usage[1]);
        Assert.Equal(("Grok 4.7", "time not reported · tokens not reported · quota not reported"), usage[2]);
    }

    [Fact]
    public void Reads_activity_alerts_and_the_repository()
    {
        var row = JsonNode.Parse("""
        { "dispatch": [ { "alive": false, "modelId": "grok" }, { "alive": true, "modelId": "opus", "activitySession": { "events": [
          { "friendly": "Reading src/api.js" }, { "friendly": "Running tests" }, { "friendly": "Running tests" }, { "friendly": "Unclassified worker activity: x" } ] } } ] }
        """)!.AsObject();
        Assert.Equal(("Opus 5.5", "Reading src/api.js\nRunning tests (x2)"), WorkDetails.Activity(row, Label));
        Assert.Equal(("the crew", "line 7\nline 8"), WorkDetails.Activity(JsonNode.Parse("""{ "workerTail": "line 7\nline 8\n" }""")!.AsObject(), Label));
        var snapshot = JsonNode.Parse("""{ "supervisor": { "projects": [ { "projectId": "p", "alerts": [ { "id": "a1", "title": "Stalled", "message": "No output for 10 min." }, { "id": "a2", "title": "Old", "active": false }, { "id": "a3", "title": "Seen", "ignored": true } ] } ] } }""")!;
        Assert.Equal([new AlertView("a1", "Stalled", "No output for 10 min.")], WorkDetails.Alerts(snapshot, "p"));
        Assert.Equal(new RepoView(true, 4, 2, null), WorkDetails.Repo(JsonNode.Parse("""{ "local": { "git": true, "uncommitted": 4 }, "comparison": { "ahead": 2 } }""")));
        Assert.Equal(new RepoView(true, 2, 0, null), WorkDetails.Repo(JsonNode.Parse("""{ "local": { "git": true, "changes": [" M a", "?? b"] } }""")));
        Assert.False(WorkDetails.Repo(JsonNode.Parse("""{ "local": { "git": false } }""")).Git);
    }
}
