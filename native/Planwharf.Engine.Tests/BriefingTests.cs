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

    [Fact]
    public void Recent_context_keeps_the_first_six_turns_newest_first_and_clips_them()
    {
        var turns = new JsonArray();
        for (var i = 0; i < 8; i++)
        {
            turns.Add(new JsonObject
            {
                ["question"] = "q" + i + new string('Q', 1700),
                ["answer"] = i == 0 ? null : "a" + i
            });
        }

        var context = Briefing.RecentContext(turns);
        Assert.Equal(["q5", "q4", "q3", "q2", "q1", "q0"], context.Select(turn => turn!["user"]!.GetValue<string>()[..2]));
        Assert.All(context, turn => Assert.Equal(1600, turn!["user"]!.GetValue<string>().Length));
        Assert.Null(context[5]!["orchestrator"]);
        Assert.Equal("a5", context[0]!["orchestrator"]!.GetValue<string>());
        Assert.Equal(8, turns.Count);
        Assert.ThrowsAny<Exception>(() => Briefing.RecentContext(new JsonArray { null }));
    }

    [Fact]
    public void Compact_state_keeps_the_latest_rows_drops_raw_logs_and_rejects_a_null_worker()
    {
        var notes = new JsonArray();
        for (var i = 0; i < 14; i++) notes.Add(new JsonObject { ["id"] = "n" + i, ["text"] = "t" + i, ["aiRelevant"] = i % 2 == 0 });
        var state = new JsonObject
        {
            ["focus"] = new JsonObject
            {
                ["id"] = "p",
                ["lastCommit"] = "h",
                ["dirty"] = new JsonArray { "a" },
                ["lastLog"] = "secret",
                ["plan"] = new JsonArray { new string('p', 500), 7 },
                ["workers"] = new JsonArray { new JsonObject { ["id"] = "w", ["log"] = "L", ["activity"] = "act" } }
            },
            ["others"] = new JsonArray { new JsonObject { ["id"] = "o", ["name"] = "O", ["repository"] = new JsonObject { ["secret"] = 1 } } },
            ["executionHandoff"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "old" }, new JsonObject { ["id"] = "m", ["title"] = "T", ["goal"] = "g", ["status"] = "OPEN" } },
                ["subTasks"] = new JsonArray
                {
                    new JsonObject { ["megaTaskId"] = "m", ["id"] = "done", ["status"] = "COMPLETE" },
                    new JsonObject { ["megaTaskId"] = "m", ["id"] = "go", ["status"] = "PENDING", ["dependencies"] = new JsonArray { "done" } },
                    new JsonObject { ["megaTaskId"] = "old", ["id"] = "x", ["status"] = "PENDING" }
                },
                ["results"] = new JsonArray
                {
                    new JsonObject { ["megaTaskId"] = "m", ["subTaskId"] = "s1", ["status"] = "COMPLETE", ["summary"] = "one" },
                    new JsonObject { ["megaTaskId"] = "m", ["subTaskId"] = "s2", ["status"] = "COMPLETE", ["summary"] = "two" },
                    new JsonObject { ["megaTaskId"] = "m", ["subTaskId"] = "s3", ["status"] = "COMPLETE", ["summary"] = "three" },
                    new JsonObject { ["megaTaskId"] = "m", ["subTaskId"] = "s4", ["status"] = "COMPLETE", ["summary"] = "four" }
                },
                ["checkpoints"] = new JsonArray { new JsonObject { ["megaTaskId"] = "m", ["id"] = "c2", ["completedSubTaskIds"] = new JsonArray { "done" } } },
                ["notes"] = notes
            },
            ["researches"] = new JsonArray
            {
                new JsonObject { ["projectId"] = "p", ["id"] = "r1" },
                new JsonObject { ["projectId"] = "other", ["id"] = "r2" },
                new JsonObject { ["projectId"] = "p", ["id"] = "r3" }
            }
        };

        var brief = Briefing.CompactState(state, new JsonObject { ["head"] = "h", ["dirty"] = new JsonArray { "a" } });
        Assert.False(brief["focus"]!.AsObject().ContainsKey("lastLog"));
        Assert.False(brief["focus"]!["workers"]![0]!.AsObject().ContainsKey("log"));
        Assert.Equal(7, brief["focus"]!["plan"]![1]!.GetValue<int>());
        Assert.Equal(400, brief["focus"]!["plan"]![0]!.GetValue<string>().Length);
        Assert.False(brief["others"]![0]!.AsObject().ContainsKey("repository"));
        Assert.Equal(["s2", "s3", "s4"], brief["executionHandoff"]!["results"]!.AsArray().Select(row => row!["subTaskId"]!.GetValue<string>()));
        Assert.Equal(["go"], brief["executionHandoff"]!["subTasks"]!.AsArray().Select(row => row!["id"]!.GetValue<string>()));
        Assert.Equal(["n0", "n2", "n4", "n6", "n8", "n10", "n12"], brief["executionHandoff"]!["notes"]!.AsArray().Select(row => row!["id"]!.GetValue<string>()));
        Assert.Equal(["r1", "r3"], brief["researches"]!.AsArray().Select(row => row!["id"]!.GetValue<string>()));
        Assert.False(brief["repositoryDelta"]!["headChanged"]!.GetValue<bool>());
        Assert.Equal("secret", state["focus"]!["lastLog"]!.GetValue<string>());
        Assert.Equal(14, state["executionHandoff"]!["notes"]!.AsArray().Count);

        var broken = (JsonObject)state.DeepClone();
        broken["focus"]!["workers"] = new JsonArray { null };
        Assert.ThrowsAny<Exception>(() => Briefing.CompactState(broken));
    }
}
