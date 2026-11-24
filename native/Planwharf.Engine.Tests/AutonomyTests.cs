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
            // The lead hears about it at once, without waiting for the alert.
            Assert.Equal("continue", Step(On(), row, alerts)["type"]!.GetValue<string>());
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

    [Fact]
    public void Nudge_window_invalid_times_and_a_zero_active_flag_follow_JavaScript()
    {
        var row = new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m1" };
        var now = Ms("2026-11-03T12:00:00.000Z");
        var alerts = new JsonArray { new JsonObject { ["key"] = "k", ["title"] = "t", ["message"] = "m" } };
        JsonObject Times(string at) => On(new JsonObject { ["nudgeTimes"] = new JsonArray { at, at, at } });

        Assert.Equal("nudge", Step(Times("2026-11-03T11:00:00.000Z"), row, alerts, now)["type"]!.GetValue<string>());
        Assert.Equal("nudge-limit", Step(Times("2026-11-03T11:00:00.001Z"), row, alerts, now)["reason"]!.GetValue<string>());
        Assert.Equal("nudge", Step(Times("nope"), row, alerts, now)["type"]!.GetValue<string>());

        var inactiveThenLive = new JsonArray
        {
            new JsonObject { ["key"] = "old", ["title"] = "t", ["message"] = "m", ["active"] = false },
            new JsonObject { ["key"] = "live", ["title"] = "t2", ["message"] = "m2", ["active"] = 0 }
        };
        var chosen = Step(On(), row, inactiveThenLive, now);
        Assert.Equal("nudge", chosen["type"]!.GetValue<string>());
        Assert.Equal("live", chosen["alert"]!["key"]!.GetValue<string>());

        Assert.Equal("stopped", Step(On(new JsonObject { ["stopReason"] = " " }), row, alerts, now)["reason"]!.GetValue<string>());
        Assert.Equal("user-paused", Step(On(), new JsonObject { ["status"] = "paused", ["megaTaskId"] = "m" }, alerts, now)["reason"]!.GetValue<string>());
        Assert.Equal("already-approved", Step(On(new JsonObject { ["approved"] = new JsonArray { "m1" } }), new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m1" }, null, now)["reason"]!.GetValue<string>());
        Assert.Equal("no-project", Step(On(), null, alerts, now)["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Decisions_clips_and_checks_text_match_JavaScript_when_fields_are_missing()
    {
        var last = Autonomy.ParseDecision("{\"done\":false,\"next\":\"old\"}\n{\"done\":true,\"assumptions\":[\"  a  \",1,\"b\"]}");
        Assert.True(last!["done"]!.GetValue<bool>());
        Assert.Equal("[\"  a  \",\"b\"]", last["assumptions"]!.ToJsonString());
        Assert.Null(Autonomy.ParseDecision("{\"done\":false,\"next\":\"   \"}"));

        var objective = "  " + new string('x', 4001);
        var assumption = new string('y', 501);
        var assumptions = new JsonArray();
        for (var i = 0; i < 12; i++) assumptions.Add(assumption);
        var clipped = Autonomy.ParseDecision(new JsonObject { ["done"] = false, ["next"] = objective, ["assumptions"] = assumptions }.ToJsonString());
        Assert.Equal(4000, clipped!["next"]!.GetValue<string>().Length);
        Assert.Equal('x', clipped["next"]!.GetValue<string>()[0]);
        Assert.Equal(10, clipped["assumptions"]!.AsArray().Count);
        Assert.Equal(500, clipped["assumptions"]![0]!.GetValue<string>().Length);

        var missingExit = Autonomy.EvaluationPrompt(new JsonObject
        {
            ["name"] = "N",
            ["goal"] = "",
            ["results"] = new JsonArray(),
            ["checks"] = new JsonObject { ["label"] = "npm test", ["seconds"] = 1.5, ["timedOut"] = false }
        });
        Assert.Contains("Goal: not recorded", missingExit);
        Assert.Contains("FAILED (exit undefined) in 1.5 s.", missingExit);
        Assert.Contains("(no output)", missingExit);

        var noTests = Autonomy.EvaluationPrompt(new JsonObject { ["name"] = "N", ["results"] = null, ["checks"] = null });
        Assert.Contains("Planwharf found no recognised test entry point", noTests);
        Assert.DoesNotContain("FAILED", noTests);

        var omitted = Autonomy.EvaluationPrompt(new JsonObject
        {
            ["name"] = "N",
            ["goal"] = "G",
            ["results"] = new JsonArray { new JsonObject { ["status"] = "FAILED" }, new JsonObject { ["status"] = "COMPLETE", ["summary"] = "" } }
        });
        Assert.Contains("- FAILED: no summary", omitted);
        Assert.Contains("- COMPLETE: no summary", omitted);
        Assert.DoesNotContain("Planwharf ran", omitted);

        Assert.False(Autonomy.CleanFinish(new JsonArray { new JsonObject { ["status"] = "COMPLETE" }, new JsonObject { ["status"] = "COMPLETE" } }, new JsonObject { ["timedOut"] = false }));
        Assert.True(Autonomy.CleanFinish(
            new JsonArray { new JsonObject { ["status"] = "COMPLETE", ["blockers"] = "x" }, new JsonObject { ["status"] = "COMPLETE" } },
            new JsonObject { ["exitCode"] = 0, ["timedOut"] = false }));
    }

    [Fact]
    public void Weights_paths_and_git_config_counts_follow_JavaScript()
    {
        Assert.Equal("3", Autonomy.PushGuardEnvironment(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "2abc" })["GIT_CONFIG_COUNT"]);
        Assert.Equal("url.planwharf-push-blocked://.pushInsteadOf", Autonomy.PushGuardEnvironment(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "1e2" })["GIT_CONFIG_KEY_1"]);
        var blank = Autonomy.PushGuardEnvironment(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "" });
        Assert.Equal("1", blank["GIT_CONFIG_COUNT"]);
        Assert.Equal("url.planwharf-push-blocked://.pushInsteadOf", blank["GIT_CONFIG_KEY_0"]);
        var negative = Autonomy.PushGuardEnvironment(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "-2" });
        Assert.Equal("-1", negative["GIT_CONFIG_COUNT"]);
        Assert.Equal("url.planwharf-push-blocked://.pushInsteadOf", negative["GIT_CONFIG_KEY_-2"]);

        var tied = Autonomy.FitWeights(new JsonArray
        {
            new JsonObject { ["title"] = "p", ["size"] = "heavy", ["model"] = "missing" },
            new JsonObject { ["title"] = "q", ["size"] = "heavy", ["model"] = "missing" }
        }, new JsonArray
        {
            new JsonObject { ["id"] = "a", ["tier"] = "light", ["left"] = 10 },
            new JsonObject { ["id"] = "b", ["tier"] = "light", ["left"] = 10 }
        });
        Assert.Equal("a", tied["parts"]![0]!["model"]!.GetValue<string>());
        Assert.Equal("b", tied["parts"]![1]!["model"]!.GetValue<string>());
        Assert.Equal("a", tied["moved"]![0]!["to"]!.GetValue<string>());

        var moved = Autonomy.FitWeights(new JsonArray { new JsonObject { ["title"] = "p", ["size"] = "normal", ["model"] = "heavy1" } }, new JsonArray
        {
            new JsonObject { ["id"] = "heavy1", ["tier"] = "premium", ["left"] = 90 },
            new JsonObject { ["id"] = "d1", ["tier"] = "default", ["left"] = 20 },
            new JsonObject { ["id"] = "d2", ["tier"] = "default", ["left"] = 80 },
            new JsonObject { ["id"] = "L", ["tier"] = "light", ["left"] = 99 }
        });
        Assert.Equal("d2", moved["parts"]![0]!["model"]!.GetValue<string>());

        var prompt = Autonomy.SplitPrompt(new JsonObject
        {
            ["name"] = "N",
            ["builders"] = new JsonArray { new JsonObject { ["id"] = "a", ["label"] = "A", ["provider"] = "p", ["tier"] = "light", ["left"] = 80.5 } },
            ["verifiers"] = new JsonArray { new JsonObject { ["id"] = "v", ["label"] = "V", ["provider"] = "q", ["tier"] = "premium", ["left"] = double.NaN } },
            ["files"] = new JsonArray()
        });
        Assert.Contains("light, 80.5% quota left", prompt);
        Assert.Contains("premium, quota not reported", prompt);

        var options = new JsonObject { ["builders"] = new JsonArray { "b" }, ["verifiers"] = new JsonArray { "v" } };
        var overlap = Autonomy.ParseSplit("{\"parts\":[{\"title\":\"a\",\"detail\":\"d\",\"files\":[\"src\"],\"size\":\"normal\",\"model\":\"b\"},{\"title\":\"c\",\"detail\":\"d\",\"files\":[\"src/a.cs\"],\"dependsOn\":[],\"size\":\"normal\",\"model\":\"b\"}],\"verify\":\"v\",\"why\":\"" + new string('z', 400) + "\"}", options);
        Assert.Equal("[0]", overlap!["parts"]![1]!["dependsOn"]!.ToJsonString());
        Assert.Equal(300, overlap["why"]!.GetValue<string>().Length);
        Assert.Null(Autonomy.ParseSplit("{\"parts\":[{\"title\":\"t\",\"detail\":\"d\",\"files\":[\"C:abs\"],\"size\":\"light\",\"model\":\"b\"}],\"verify\":\"v\"}", options));
        Assert.Null(Autonomy.ParseSplit("{\"parts\":[{\"title\":\"t\",\"detail\":\"d\",\"files\":[\"foo/../../x\"],\"size\":\"normal\",\"model\":\"b\"}],\"verify\":\"v\"}", options));
        var escaped = Autonomy.ParseSplit("{\"parts\":[{\"title\":\"t\",\"detail\":\"d\",\"files\":[\"src\\\\\\\\a/\",\"ok\"],\"size\":\"light\",\"model\":\"b\"}],\"verify\":\"v\"}", options);
        Assert.Equal("[\"src//a\",\"ok\"]", escaped!["parts"]![0]!["files"]!.ToJsonString());

        var gate = new System.Threading.Barrier(4);
        var kinds = new string[4];
        System.Threading.Tasks.Parallel.For(0, 4, i =>
        {
            gate.SignalAndWait();
            var settings = On();
            kinds[i] = Autonomy.NextStep("auto", "s1", settings, new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m" + i }, null, now: 1)["type"]!.GetValue<string>();
        });
        Assert.All(kinds, kind => Assert.Equal("approve", kind));
    }

    [Fact]
    public void TheLeadSeesTheDocumentsTheGoalNamesClippedToTheirLimits()
    {
        var files = new[] { "native/porting/jobs/42-phase4.md", "native/porting/jobs/4-other.md", "docs/NEXT-WORK.md", "src/a.js" };
        Assert.Equal(new[] { "docs/NEXT-WORK.md", "native/porting/jobs/42-phase4.md" }, Autonomy.NamedDocuments("Port, as sheet 42 says, the modules (see docs/NEXT-WORK.md)", files));
        Assert.Empty(Autonomy.NamedDocuments("Fix the button", files));
        var docs = Autonomy.ReadDocuments("/p", new[] { "a.md", "missing.md", "big.md" }, file =>
            file.EndsWith("missing.md") ? throw new IOException("missing") : file.EndsWith("big.md") ? new string('x', 20000) : "Modules: one, two, three");
        Assert.Equal(2, docs.Count);
        Assert.Equal("a.md", docs[0]!["path"]!.GetValue<string>());
        Assert.False(docs[0]!["clipped"]!.GetValue<bool>());
        Assert.Equal(12000, docs[1]!["text"]!.GetValue<string>().Length);
        Assert.True(docs[1]!["clipped"]!.GetValue<bool>());
        var text = Autonomy.SplitPrompt(new JsonObject { ["name"] = "P", ["goal"] = "G", ["builders"] = new JsonArray(), ["verifiers"] = new JsonArray(), ["documents"] = docs });
        Assert.Contains("Document the goal names, a.md:\n<<<\nModules: one, two, three\n>>>", text);
        Assert.DoesNotContain("decide from this message only", text);
    }

    [Fact]
    public void WorkThatEndedBlockedGoesBackToTheLeadOnce()
    {
        var settings = new JsonObject { ["enabled"] = true, ["sessionId"] = "s1", ["started"] = true };
        var row = new JsonObject { ["status"] = "checkpointed", ["megaTaskId"] = "m1", ["checkpoint"] = new JsonObject { ["reason"] = "blocked worker" }, ["megaTask"] = new JsonObject { ["pauseRequested"] = true } };
        Assert.Equal("continue", Autonomy.NextStep("auto", "s1", settings, row, null, now: 1)["type"]!.GetValue<string>());
        settings["continued"] = new JsonArray("m1");
        Assert.Equal("idle", Autonomy.NextStep("auto", "s1", settings, (JsonObject)row.DeepClone(), new JsonArray(), now: 1)["reason"]!.GetValue<string>());
    }
}
