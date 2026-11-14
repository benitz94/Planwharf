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

    static JsonObject Event(string id, string? at, JsonNode? megaTaskId)
    {
        var ev = new JsonObject { ["id"] = id };
        if (at is not null) ev["createdAt"] = at;
        if (megaTaskId is not null) ev["reference"] = new JsonObject { ["megaTaskId"] = megaTaskId.DeepClone() };
        return ev;
    }

    [Fact]
    public void Missing_and_null_fields_read_like_JavaScript_templates()
    {
        var events = new JsonArray { Event("e", "2026-01-01T00:00:00.000Z", JsonValue.Create("m")) };
        var missingTitle = ProjectHistory.SummarizeTimeline(events, new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["status"] = "COMPLETE" } },
            ["results"] = new JsonArray { new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE" } }
        });
        Assert.Equal("Completed undefined: COMPLETE.", missingTitle[0]!["summary"]!.GetValue<string>());

        var nullTitle = ProjectHistory.SummarizeTimeline(new JsonArray { Event("e", "2026-01-01T00:00:00.000Z", JsonValue.Create("m")) }, new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["title"] = null, ["status"] = "RUNNING" } },
            ["results"] = new JsonArray()
        });
        Assert.Equal("null: RUNNING.", nullTitle[0]!["summary"]!.GetValue<string>());

        var blankSummary = ProjectHistory.SummarizeTimeline(new JsonArray { Event("e", "2026-01-02T00:00:00.000Z", JsonValue.Create("m")) }, new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["title"] = "T", ["status"] = "COMPLETE" } },
            ["results"] = new JsonArray { new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE" }, new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE", ["summary"] = "abc" } }
        });
        Assert.Equal("Completed T: COMPLETE.  abc", blankSummary[0]!["summary"]!.GetValue<string>());

        var longJoined = string.Join(" ", Enumerable.Repeat(new string('s', 300), 3));
        var clipped = ProjectHistory.SummarizeTimeline(new JsonArray { Event("e", "2026-01-03T00:00:00.000Z", JsonValue.Create("m")) }, new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["title"] = "T", ["status"] = "OPEN" } },
            ["results"] = new JsonArray
            {
                new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE", ["summary"] = new string('s', 300) },
                new JsonObject { ["megaTaskId"] = "m", ["status"] = "FAILED", ["summary"] = "no" },
                new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE", ["summary"] = new string('s', 300) },
                new JsonObject { ["megaTaskId"] = "m", ["status"] = "COMPLETE", ["summary"] = new string('s', 300) }
            }
        });
        Assert.Equal("T: OPEN. " + longJoined[..600], clipped[0]!["summary"]!.GetValue<string>());
        Assert.Equal(600, longJoined[..600].Length);
    }

    [Fact]
    public void Numeric_mega_task_ids_group_and_timestamps_sort()
    {
        var events = new JsonArray
        {
            Event("later", "2026-05-02T00:00:00.000Z", JsonValue.Create(1)),
            Event("earlier", "2026-05-01T00:00:00.000Z", JsonValue.Create(1)),
            Event("other", "2026-04-01T00:00:00.000Z", null)
        };
        var summary = ProjectHistory.SummarizeTimeline(events, new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = 1, ["title"] = "T", ["status"] = "OPEN" } },
            ["results"] = new JsonArray()
        });
        Assert.Equal(["other", "milestone:1"], summary.Select(node => node!["id"]!.GetValue<string>()));
        Assert.Equal(2, summary[1]!["evidenceCount"]!.GetValue<int>());
        Assert.Equal("T: OPEN.", summary[1]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public void Checkpoint_diff_reports_absent_values_and_refuses_a_broken_snapshot()
    {
        var same = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "a", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() };
        var missingRevision = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = (JsonObject)same.DeepClone(),
            ["completedSubTaskIds"] = new JsonArray()
        }, (JsonObject)same.DeepClone(), 3);
        Assert.Equal("Living Plan changed from Rundefined to R3", missingRevision["changes"]![0]!.GetValue<string>());
        Assert.True(missingRevision["requiresConfirmation"]!.GetValue<bool>());

        var nullHead = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = null, ["fingerprint"] = "a", ["comparable"] = true },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        }, new JsonObject { ["fingerprint"] = "a", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() }, 1);
        Assert.Equal("HEAD changed from unavailable to unavailable", nullHead["changes"]![0]!.GetValue<string>());

        var emptyHead = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = "", ["fingerprint"] = "a", ["comparable"] = true },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        }, new JsonObject { ["repoHead"] = "b", ["fingerprint"] = "a", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() }, 1);
        Assert.Equal("HEAD changed from unavailable to b", emptyHead["changes"]![0]!.GetValue<string>());

        var bothMissing = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["fingerprint"] = "a", ["comparable"] = true },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        }, new JsonObject { ["fingerprint"] = "a", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() }, 1);
        Assert.Empty(bothMissing["changes"]!.AsArray());
        Assert.False(bothMissing["requiresConfirmation"]!.GetValue<bool>());

        var zeroComparable = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "a", ["comparable"] = 0 },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        }, (JsonObject)same.DeepClone(), 1);
        Assert.Equal("Repository comparison is incomplete; inspect technical evidence before Resume", zeroComparable["changes"]![0]!.GetValue<string>());

        var repeated = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = (JsonObject)same.DeepClone(),
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray { "kept" }
        }, (JsonObject)same.DeepClone(), 1, new JsonArray { "new", "new", "kept" });
        Assert.Equal("2 previously pending SubTask(s) already completed; these will not be replayed", repeated["changes"]![0]!.GetValue<string>());
        Assert.False(repeated["requiresConfirmation"]!.GetValue<bool>());

        Assert.ThrowsAny<Exception>(() => ProjectHistory.CheckpointDiff(new JsonObject { ["planRevisionId"] = 1 }, (JsonObject)same.DeepClone(), 1));
        Assert.ThrowsAny<Exception>(() => ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "old", ["comparable"] = true },
            ["planRevisionId"] = 1
        }, new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "new", ["comparable"] = true }, 1));
        Assert.ThrowsAny<Exception>(() => ProjectHistory.SummarizeTimeline(new JsonArray { null }, new JsonObject()));
        Assert.ThrowsAny<Exception>(() => ProjectHistory.SummarizeTimeline(new JsonArray
        {
            new JsonObject { ["id"] = "x", ["reference"] = new JsonObject { ["megaTaskId"] = "m" } }
        }, new JsonObject { ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["title"] = "T", ["status"] = "OPEN" } }, ["results"] = new JsonArray() }));
    }
}
