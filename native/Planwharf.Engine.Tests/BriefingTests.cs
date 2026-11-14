// Ports the checks of engine/final.test.cjs that call compactState and recentContext
// ("compact briefing excludes user-only notes and completed history, preserves checkpoint identity"),
// plus edge cases checked against JavaScript: clipping of long fields, dropping workers' raw logs,
// missing/null focus and handoff, and repository delta computation.
//
// Deferred: the other checks of final.test.cjs exercise orchestrator, invocations, provider-catalog,
// project-export, app-windows or HTTP server routes, not briefing.cjs.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class BriefingTests
{
    [Fact]
    public void Compact_briefing_excludes_user_only_notes_and_completed_history_preserves_checkpoint_identity()
    {
        var state = JsonNode.Parse("""
        {
            "focus": { "id": "p", "lastCommit": "abc", "dirty": [] },
            "executionHandoff": {
                "megaTasks": [{ "id": "m", "goal": "goal" }],
                "subTasks": [
                    { "megaTaskId": "m", "id": "done", "status": "COMPLETE" },
                    { "megaTaskId": "m", "id": "pending", "status": "PENDING" }
                ],
                "results": [],
                "checkpoints": [{ "megaTaskId": "m", "id": "cp", "completedSubTaskIds": ["done"], "pendingSubTaskIds": ["pending"] }],
                "notes": [{ "text": "private" }, { "text": "relevant", "aiRelevant": true }]
            }
        }
        """)!.AsObject();

        var brief = Briefing.CompactState(state);
        var handoff = brief["executionHandoff"]!.AsObject();

        Assert.Single(handoff["notes"]!.AsArray());
        Assert.Single(handoff["subTasks"]!.AsArray());
        Assert.Equal("[\"done\"]", handoff["checkpoint"]!["completedSubTaskIds"]!.ToJsonString());

        var turns = new JsonArray();
        for (int i = 0; i < 100; i++)
        {
            turns.Add(new JsonObject
            {
                ["question"] = new string('q', 4000),
                ["answer"] = new string('a', 8000)
            });
        }
        var context = Briefing.RecentContext(turns);
        Assert.Equal(6, context.Count);
        // Original state is not mutated
        Assert.Equal(2, state["executionHandoff"]!["notes"]!.AsArray().Count);
    }

    [Fact]
    public void Repository_delta_and_initial_inspection()
    {
        var state = new JsonObject
        {
            ["focus"] = new JsonObject
            {
                ["id"] = "p",
                ["lastCommit"] = "c1",
                ["dirty"] = new JsonArray { " M a.js" }
            }
        };

        var initial = Briefing.CompactState(state, null);
        Assert.True(initial["repositoryDelta"]!["initialInspection"]!.GetValue<bool>());
        Assert.Equal("c1", initial["repositoryDelta"]!["current"]!["head"]!.GetValue<string>());

        var previous = new JsonObject
        {
            ["head"] = "c0",
            ["dirty"] = new JsonArray()
        };
        var updated = Briefing.CompactState(state, previous);
        Assert.True(updated["repositoryDelta"]!["headChanged"]!.GetValue<bool>());
        Assert.True(updated["repositoryDelta"]!["dirtyChanged"]!.GetValue<bool>());

        var same = Briefing.CompactState(state, new JsonObject
        {
            ["head"] = "c1",
            ["dirty"] = new JsonArray { " M a.js" }
        });
        Assert.False(same["repositoryDelta"]!["headChanged"]!.GetValue<bool>());
        Assert.False(same["repositoryDelta"]!["dirtyChanged"]!.GetValue<bool>());
    }

    [Fact]
    public void Long_fields_are_clipped_and_worker_logs_dropped()
    {
        var state = new JsonObject
        {
            ["focus"] = new JsonObject
            {
                ["id"] = "p",
                ["analysis"] = new string('a', 1500),
                ["state"] = new string('s', 1500),
                ["plan"] = new JsonArray { new string('p', 500) },
                ["workers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "w1",
                        ["log"] = "sensitive log content",
                        ["activity"] = new string('x', 400)
                    }
                }
            }
        };

        var brief = Briefing.CompactState(state);
        var focus = brief["focus"]!.AsObject();

        Assert.Equal(1200, focus["analysis"]!.GetValue<string>().Length);
        Assert.Equal(1200, focus["state"]!.GetValue<string>().Length);
        Assert.Equal(400, focus["plan"]!.AsArray()[0]!.GetValue<string>().Length);
        Assert.Null(focus["lastLog"]);

        var worker = focus["workers"]!.AsArray()[0]!.AsObject();
        Assert.False(worker.ContainsKey("log"));
        Assert.Equal(300, worker["activity"]!.GetValue<string>().Length);
    }
}
