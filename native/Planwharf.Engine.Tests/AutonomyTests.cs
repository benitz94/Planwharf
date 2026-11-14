// Ports the checks of engine/autonomy.test.cjs and the autonomy-alone checks of
// engine/autonomy-crew.test.cjs, plus edge cases checked against JavaScript.
//
// Deferred:
// 1. Real `git push` invocation in pushGuardEnvironment (autonomy.test.cjs lines 80-92)
//    deferred because tests must not depend on git on the computer (rule 11).
// 2. Orchestrator and server integration tests (autonomy.test.cjs lines 97-213 and
//    autonomy-crew.test.cjs lines 26-41, 62-102, 115-176) deferred because the orchestrator
//    and HTTP server are not ported yet.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class AutonomyTests
{
    static double Ms(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    static JsonObject On(JsonObject? extra = null)
    {
        var settings = Autonomy.CreateSettings("s1", "2026-11-03T10:00:00Z");
        settings["started"] = true;
        if (extra is not null)
        {
            foreach (var pair in extra) settings[pair.Key] = pair.Value?.DeepClone();
        }
        return settings;
    }

    static JsonObject Step(JsonObject? settings, JsonObject? row, JsonArray? alerts = null, double? now = null) =>
        Autonomy.NextStep("auto", "s1", settings, row, alerts, now);

    [Fact]
    public void Authority_needs_the_setting_automatic_mode_and_the_session_that_enabled_it()
    {
        var row = new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m1" };
        Assert.Equal("none", Step(null, row)["type"]!.GetValue<string>());

        var disabled = On();
        disabled["enabled"] = false;
        Assert.Equal("none", Step(disabled, row)["type"]!.GetValue<string>());

        Assert.Equal("none", Autonomy.NextStep("manual", "s1", On(), row)["type"]!.GetValue<string>());
        Assert.Equal("none", Autonomy.NextStep("auto", "restarted", On(), row)["type"]!.GetValue<string>());

        var approved = Step(On(), row);
        Assert.Equal("approve", approved["type"]!.GetValue<string>());
        Assert.Equal("m1", approved["megaTaskId"]!.GetValue<string>());
    }

    [Fact]
    public void A_user_pause_or_stop_is_never_overridden_even_with_an_active_alert()
    {
        var alerts = new JsonArray { new JsonObject { ["key"] = "a", ["title"] = "t", ["message"] = "m" } };
        JsonObject[] rows =
        [
            new() { ["status"] = "checkpointed", ["megaTaskId"] = "m1" },
            new() { ["status"] = "checkpointed", ["megaTaskId"] = "m1", ["checkpoint"] = new JsonObject { ["reason"] = "stop" } },
            new() { ["status"] = "watching", ["megaTaskId"] = "m1", ["megaTask"] = new JsonObject { ["pauseRequested"] = true } }
        ];

        foreach (var r in rows)
        {
            Assert.Equal("none", Step(On(), r, alerts)["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public void A_slice_that_ends_blocked_or_failed_reaches_the_lead_instead_of_waiting_forever()
    {
        var alerts = new JsonArray { new JsonObject { ["key"] = "result-25", ["title"] = "An assignment did not finish successfully", ["message"] = "m" } };
        foreach (var reason in new[] { "blocked worker", "worker failure" })
        {
            var row = new JsonObject
            {
                ["status"] = "checkpointed",
                ["megaTaskId"] = "m1",
                ["checkpoint"] = new JsonObject { ["reason"] = reason }
            };
            Assert.Equal("nudge", Step(On(), row, alerts)["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public void The_megatask_cap_stops_approval_and_reports_the_limit()
    {
        var approved = new JsonArray();
        for (var i = 0; i < Autonomy.LIMITS.MaxMegaTasks; i++) approved.Add("old" + i);
        var settings = On(new JsonObject { ["approved"] = approved });

        var step = Step(settings, new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m9" });
        Assert.Equal("limit", step["type"]!.GetValue<string>());
        Assert.Equal("MEGATASK_LIMIT", step["reason"]!.GetValue<string>());
    }

    [Fact]
    public void A_completed_megatask_is_evaluated_once()
    {
        var row = new JsonObject { ["status"] = "complete", ["megaTaskId"] = "m1" };
        Assert.Equal("evaluate", Step(On(), row)["type"]!.GetValue<string>());

        var evaluated = On(new JsonObject { ["evaluated"] = new JsonArray { "m1" } });
        Assert.Equal("none", Step(evaluated, row)["type"]!.GetValue<string>());
    }

    [Fact]
    public void Turning_the_crew_on_after_finished_work_starts_new_work_instead_of_rejudging_the_old_one()
    {
        var fresh = Autonomy.CreateSettings("s1", "2026-11-03T10:00:00Z");
        Assert.Equal("start", Step(fresh, new JsonObject { ["status"] = "complete", ["megaTaskId"] = "m1" })["type"]!.GetValue<string>());
        Assert.Equal("start", Step(fresh, new JsonObject { ["status"] = "idle" })["type"]!.GetValue<string>());
        Assert.Equal("approve", Step(fresh, new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m2" })["type"]!.GetValue<string>());
        Assert.Equal("start", Step(fresh, new JsonObject { ["status"] = "idle", ["megaTaskId"] = "m3", ["megaTask"] = new JsonObject { ["status"] = "CANCELLED" } })["type"]!.GetValue<string>());
    }

    [Fact]
    public void Each_alert_nudges_once_ignored_alerts_never_and_the_hourly_cap_holds()
    {
        var row = new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m1" };
        var now = Ms("2026-11-03T12:00:00Z");
        var alert = new JsonObject { ["key"] = "quiet", ["title"] = "t", ["message"] = "m" };

        Assert.Equal("nudge", Step(On(), row, new JsonArray { (JsonObject)alert.DeepClone() }, now)["type"]!.GetValue<string>());

        var nudged = On(new JsonObject { ["nudgedKeys"] = new JsonArray { "quiet" } });
        Assert.Equal("none", Step(nudged, row, new JsonArray { (JsonObject)alert.DeepClone() }, now)["type"]!.GetValue<string>());

        var ignored = (JsonObject)alert.DeepClone();
        ignored["ignored"] = true;
        Assert.Equal("none", Step(On(), row, new JsonArray { ignored }, now)["type"]!.GetValue<string>());

        var nudgeTimes = new JsonArray();
        for (var i = 0; i < Autonomy.LIMITS.MaxNudgesPerHour; i++) nudgeTimes.Add("2026-11-03T11:30:00Z");
        var capped = On(new JsonObject { ["nudgeTimes"] = nudgeTimes });
        var step = Step(capped, row, new JsonArray { (JsonObject)alert.DeepClone() }, now);
        Assert.Equal("nudge-limit", step["reason"]!.GetValue<string>());

        var oldNudges = new JsonArray();
        for (var i = 0; i < Autonomy.LIMITS.MaxNudgesPerHour; i++) oldNudges.Add("2026-11-03T09:00:00Z");
        var expired = On(new JsonObject { ["nudgeTimes"] = oldNudges });
        Assert.Equal("nudge", Step(expired, row, new JsonArray { (JsonObject)alert.DeepClone() }, now)["type"]!.GetValue<string>());
    }

    [Fact]
    public void A_nudge_is_written_from_detected_facts()
    {
        var text = Autonomy.NudgePrompt(new JsonObject
        {
            ["name"] = "Demo",
            ["goal"] = "Ship",
            ["alert"] = new JsonObject { ["title"] = "No recent progress", ["message"] = "Inspect it." }
        });
        Assert.Contains("No recent progress", text);
        Assert.Contains("Goal: Ship", text);
        Assert.Contains("never push", text);
    }

    [Fact]
    public void Lead_replies_only_yield_done_an_objective_or_assumptions_anything_else_is_rejected()
    {
        var p1 = Autonomy.ParseDecision("{\"done\":true,\"next\":\"ignored\",\"assumptions\":[\"a\"]}");
        Assert.NotNull(p1);
        Assert.True(p1["done"]!.GetValue<bool>());
        Assert.Null(p1["next"]);
        Assert.Equal("[\"a\"]", p1["assumptions"]!.ToJsonString());

        var p2 = Autonomy.ParseDecision("Sure:\n{\"done\":false,\"next\":\"Add tests\"}");
        Assert.NotNull(p2);
        Assert.False(p2["done"]!.GetValue<bool>());
        Assert.Equal("Add tests", p2["next"]!.GetValue<string>());
        Assert.Empty(p2["assumptions"]!.AsArray());

        var p3 = Autonomy.ParseDecision("{\"done\":false,\"next\":null}");
        Assert.NotNull(p3);
        Assert.False(p3["done"]!.GetValue<bool>());
        Assert.Null(p3["next"]);

        string[] badInputs = ["", "all good", "{\"done\":\"yes\"}", "{broken", "{\"done\":false,\"next\":7}"];
        foreach (var bad in badInputs) Assert.Null(Autonomy.ParseDecision(bad));
        Assert.Null(Autonomy.ParseDecision(null));

        var extra = Autonomy.ParseDecision("{\"done\":false,\"next\":\"x\",\"approve\":true,\"push\":true}");
        Assert.NotNull(extra);
        Assert.False(extra.ContainsKey("approve"));
    }

    [Fact]
    public void Push_guard_environment_structure()
    {
        var initial = Autonomy.PushGuardEnvironment(new Dictionary<string, string?>());
        Assert.Equal("1", initial["GIT_CONFIG_COUNT"]);
        Assert.Equal("url.planwharf-push-blocked://.pushInsteadOf", initial["GIT_CONFIG_KEY_0"]);
        Assert.Equal("", initial["GIT_CONFIG_VALUE_0"]);

        var existing = Autonomy.PushGuardEnvironment(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "2" });
        Assert.Equal("3", existing["GIT_CONFIG_COUNT"]);
        Assert.Equal("url.planwharf-push-blocked://.pushInsteadOf", existing["GIT_CONFIG_KEY_2"]);
        Assert.Equal("", existing["GIT_CONFIG_VALUE_2"]);
    }

    [Fact]
    public void Crew_assignment_parsing_and_prompt()
    {
        var parsed = Autonomy.ParseAssignment("{\"assign\":[\"b\",\"a\"],\"why\":\"fast\"}", 2, new JsonArray { "a", "b" });
        Assert.NotNull(parsed);
        Assert.Equal("[\"b\",\"a\"]", parsed["assign"]!.ToJsonString());
        Assert.Equal("fast", parsed["why"]!.GetValue<string>());

        Assert.Null(Autonomy.ParseAssignment("{\"assign\":[\"b\"]}", 2, new JsonArray { "a", "b" }));
        Assert.Null(Autonomy.ParseAssignment("{\"assign\":[\"z\",\"a\"]}", 2, new JsonArray { "a", "b" }));

        var perSliceAllowed = new JsonArray
        {
            new JsonArray { "a" },
            new JsonArray { "b" }
        };
        Assert.Null(Autonomy.ParseAssignment("{\"assign\":[\"a\",\"a\"]}", 2, perSliceAllowed));
        var validPerSlice = Autonomy.ParseAssignment("{\"assign\":[\"a\",\"b\"]}", 2, perSliceAllowed);
        Assert.NotNull(validPerSlice);
        Assert.Equal("[\"a\",\"b\"]", validPerSlice["assign"]!.ToJsonString());

        var prompt = Autonomy.AssignmentPrompt(new JsonObject
        {
            ["name"] = "P",
            ["goal"] = "G",
            ["slices"] = new JsonArray { "one", "two" },
            ["candidates"] = new JsonArray
            {
                new JsonArray { new JsonObject { ["id"] = "a", ["label"] = "A", ["provider"] = "x" } },
                new JsonArray { new JsonObject { ["id"] = "b", ["label"] = "B", ["provider"] = "y" } }
            }
        });
        Assert.Contains("different provider", prompt);
        Assert.DoesNotContain("You told the user", prompt);
        Assert.Contains("Allowed: a (A, provider x)", prompt);
    }

    [Fact]
    public void Clean_finish_checks()
    {
        var ok = new JsonObject { ["status"] = "COMPLETE", ["blockers"] = new JsonArray() };
        var pass = new JsonObject { ["exitCode"] = 0, ["timedOut"] = false };

        Assert.True(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone(), (JsonObject)ok.DeepClone() }, pass));
        Assert.False(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone() }, pass));

        var blocked = (JsonObject)ok.DeepClone();
        blocked["blockers"] = new JsonArray { "x" };
        Assert.False(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone(), blocked }, pass));

        var partial = (JsonObject)ok.DeepClone();
        partial["status"] = "PARTIAL";
        Assert.False(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone(), partial }, pass));

        Assert.False(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone(), (JsonObject)ok.DeepClone() }, new JsonObject { ["exitCode"] = 1, ["timedOut"] = false }));
        Assert.False(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone(), (JsonObject)ok.DeepClone() }, new JsonObject { ["exitCode"] = 0, ["timedOut"] = true }));
        Assert.False(Autonomy.CleanFinish(new JsonArray { (JsonObject)ok.DeepClone(), (JsonObject)ok.DeepClone() }, null));
    }

    [Fact]
    public void Split_prompt_and_parsing_and_weights()
    {
        var builders = new JsonArray
        {
            new JsonObject { ["id"] = "fast", ["label"] = "Fast", ["provider"] = "codex", ["tier"] = "light", ["left"] = 80.0 },
            new JsonObject { ["id"] = "pro", ["label"] = "Pro", ["provider"] = "claude", ["tier"] = "premium" }
        };
        var verifiers = new JsonArray
        {
            new JsonObject { ["id"] = "pro", ["label"] = "Pro", ["provider"] = "claude", ["tier"] = "premium" }
        };

        var prompt = Autonomy.SplitPrompt(new JsonObject
        {
            ["name"] = "Demo",
            ["goal"] = "Ship",
            ["builders"] = builders,
            ["verifiers"] = verifiers,
            ["files"] = new JsonArray { "src/a.cs", "src/b.cs" }
        });
        Assert.Contains("Autonomous planning for project Demo", prompt);
        Assert.Contains("Models allowed to build: fast (Fast, provider codex, light, 80% quota left)", prompt);

        var splitReply = """
        {
            "parts": [
                { "title": "Part 1", "detail": "Do part 1", "files": ["src/a.cs"], "size": "light", "model": "fast" },
                { "title": "Part 2", "detail": "Do part 2", "files": ["src/a.cs", "src/b.cs"], "dependsOn": [], "size": "heavy", "model": "pro" }
            ],
            "verify": "pro",
            "why": "independent check"
        }
        """;

        var options = new JsonObject
        {
            ["builders"] = new JsonArray { "fast", "pro" },
            ["verifiers"] = new JsonArray { "pro" }
        };

        var parsed = Autonomy.ParseSplit(splitReply, options);
        Assert.NotNull(parsed);
        Assert.Equal("pro", parsed["verify"]!.GetValue<string>());
        var parts = parsed["parts"]!.AsArray();
        Assert.Equal(2, parts.Count);
        // Overlapping files put part 1 as dependency of part 2
        Assert.Equal("[0]", parts[1]!["dependsOn"]!.ToJsonString());

        var fitted = Autonomy.FitWeights(parts, builders);
        Assert.Equal(2, fitted["parts"]!.AsArray().Count);
    }
}
