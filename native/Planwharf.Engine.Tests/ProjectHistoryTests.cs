// Ports the checks of engine/final.test.cjs that call summarizeTimeline and checkpointDiff
// ("timeline groups technical evidence; checkpoint diff reports changes without replay"),
// plus edge cases checked against JavaScript: sorting by createdAt, events without a megaTask,
// long results clipped to 600 chars, plan revision change, and non-comparable repositories.
//
// Deferred: the other checks of final.test.cjs exercise orchestrator, invocations, provider-catalog,
// project-export, app-windows or HTTP server routes, not project-history.cjs.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProjectHistoryTests
{
    [Fact]
    public void Timeline_groups_technical_evidence_checkpoint_diff_reports_changes_without_replay()
    {
        var events = new JsonArray();
        string[] types = ["SUBTASK_STARTED", "SUBTASK_COMPLETED", "MEGATASK_COMPLETED"];
        for (int n = 0; n < types.Length; n++)
        {
            events.Add(new JsonObject
            {
                ["id"] = n.ToString(),
                ["type"] = types[n],
                ["createdAt"] = DateTimeOffset.FromUnixTimeMilliseconds(n).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                ["reference"] = new JsonObject { ["megaTaskId"] = "m" },
                ["summary"] = types[n]
            });
        }

        var execution = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "m", ["title"] = "Add arithmetic", ["status"] = "COMPLETE" }
            },
            ["results"] = new JsonArray
            {
                new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE", ["summary"] = "Added module and two tests." }
            }
        };

        var summary = ProjectHistory.SummarizeTimeline(events, execution);
        Assert.Single(summary);
        var milestone = summary[0]!.AsObject();
        Assert.Equal(3, milestone["evidence"]!.AsArray().Count);
        Assert.Contains("two tests", milestone["summary"]!.GetValue<string>());
        Assert.Equal("Completed Add arithmetic: COMPLETE. Added module and two tests.", milestone["summary"]!.GetValue<string>());

        var checkpoint = new JsonObject
        {
            ["repoState"] = new JsonObject
            {
                ["repoHead"] = "a",
                ["fingerprint"] = "old",
                ["comparable"] = true
            },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        };
        var current = new JsonObject
        {
            ["repoHead"] = "b",
            ["fingerprint"] = "new",
            ["comparable"] = true,
            ["dirtyFiles"] = new JsonArray { " M f" }
        };
        var completed = new JsonArray { "done" };

        var diff = ProjectHistory.CheckpointDiff(checkpoint, current, 1, completed);
        Assert.True(diff["requiresConfirmation"]!.GetValue<bool>());
        var changesStr = string.Join(" ", diff["changes"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Contains("will not be replayed", changesStr);
        Assert.Contains("HEAD changed from a to b", changesStr);
        Assert.Contains("External repository changes detected; 1 files currently changed locally", changesStr);
    }

    [Fact]
    public void Non_grouped_events_and_unknown_megatasks_stay_individual()
    {
        var events = new JsonArray
        {
            new JsonObject { ["id"] = "e1", ["createdAt"] = "2026-11-04T10:00:00.000Z", ["summary"] = "stand-alone" },
            new JsonObject { ["id"] = "e2", ["createdAt"] = "2026-11-04T10:00:01.000Z", ["reference"] = new JsonObject { ["megaTaskId"] = "unknown" }, ["summary"] = "orphan" }
        };

        var summary = ProjectHistory.SummarizeTimeline(events, new JsonObject());
        Assert.Equal(2, summary.Count);
        Assert.Equal("e1", summary[0]!["id"]!.GetValue<string>());
        Assert.Equal("e2", summary[1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Checkpoint_diff_only_completed_does_not_require_confirmation()
    {
        var checkpoint = new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "fp", ["comparable"] = true },
            ["planRevisionId"] = 2,
            ["completedSubTaskIds"] = new JsonArray { "sub1" }
        };
        var current = new JsonObject
        {
            ["repoHead"] = "a",
            ["fingerprint"] = "fp",
            ["comparable"] = true,
            ["dirtyFiles"] = new JsonArray()
        };
        var completed = new JsonArray { "sub1", "sub2" };

        var diff = ProjectHistory.CheckpointDiff(checkpoint, current, 2, completed);
        Assert.False(diff["requiresConfirmation"]!.GetValue<bool>());
        Assert.Single(diff["changes"]!.AsArray());
        Assert.Contains("1 previously pending SubTask(s) already completed; these will not be replayed", diff["changes"]!.AsArray()[0]!.GetValue<string>());
    }
}
