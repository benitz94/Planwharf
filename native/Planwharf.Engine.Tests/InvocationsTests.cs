using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Deferred cases from native/porting/jobs/23-phase2-rest.md. Not implemented in this file:
// attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// createServer, createOrchestrator, spawnWorker, runner.cjs, and the HTTP loops in
// task-completion.test.cjs, grill-chat.test.cjs, project-export.test.cjs, project-teams.test.cjs,
// start-over.test.cjs (archiveProject), and worker-containment.test.cjs (launch artifacts and provider cwd)
// Live npm test, live node --test, and live taskkill
// Byte-identical randomUUID values across JavaScript and C#
// gitEnvironment and within as parity JSON
// The browser root / window / AMD wrappers, models.json, and provider-catalog
// A second process appending to invocations.jsonl at the same time
// Reading the checkout's plan.json or .git
public class InvocationsTests
{
    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    static string Temp(string name)
    {
        var root = Directory.CreateTempSubdirectory("pw-ledger-");
        var dir = Path.Combine(root.FullName, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Gone(string dir) => Directory.Delete(Directory.GetParent(dir)!.FullName, true);

    static int Num(JsonNode? node) => (int)double.Parse(node!.ToJsonString(), CultureInfo.InvariantCulture);

    static JsonObject GroupAt(JsonNode? list, int index) => (JsonObject)((JsonArray)list!)[index]!;

    static JsonObject RecentRole(JsonObject view, string role)
    {
        foreach (var item in (JsonArray)view["recent"]!)
            if (item is JsonObject row && item["role"]?.GetValue<string>() == role) return row;
        throw new InvalidOperationException(role);
    }

    static JsonObject ById(JsonObject view, string id)
    {
        foreach (var item in (JsonArray)view["recent"]!)
            if (item!["id"]!.GetValue<string>() == id) return (JsonObject)item;
        throw new InvalidOperationException(id);
    }

    static int Lines(string file) => File.ReadAllText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    static string Worker(InvocationLedger ledger, string attempt, int? pid = null, string? result = null, string? exit = null, string project = "p", string provider = "fixture", string model = "exact")
    {
        var id = ledger.Start(new JsonObject
        {
            ["role"] = "worker", ["projectId"] = project, ["provider"] = provider, ["model"] = model, ["attemptId"] = attempt
        });
        var fields = new JsonObject();
        if (pid is not null) fields["pid"] = pid.Value;
        if (result is not null) fields["resultPath"] = result;
        if (exit is not null) fields["exitPath"] = exit;
        if (fields.Count > 0) ledger.Update(id, fields);
        return id;
    }

    [Fact]
    public void Ledger_retains_exact_identity_durations_terminal_states_and_reliable_tokens_across_restart()
    {
        var dir = Temp("identity");
        try
        {
            long at = 0;
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(at));
            var id = ledger.Start(new JsonObject
            {
                ["role"] = "orchestrator", ["projectId"] = "p", ["provider"] = "fixture", ["model"] = "exact",
                ["reason"] = "reasoning", ["env"] = new JsonObject { ["TOKEN"] = "must-not-persist" }, ["token"] = "must-not-persist"
            });
            at = 2000;
            ledger.Finish(id, "COMPLETE", JsonValue.Create(0), new JsonObject { ["input"] = 12, ["output"] = 4 });
            var worker = ledger.Start(new JsonObject
            {
                ["role"] = "worker", ["projectId"] = "p", ["provider"] = "fixture", ["model"] = "exact",
                ["reason"] = "approved work", ["attemptId"] = "a"
            });
            ledger.Update(worker, new JsonObject { ["pid"] = 123 });
            at = 3000;
            ledger.Reconcile(new JsonObject
            {
                ["execution"] = new JsonObject
                {
                    ["results"] = new JsonArray { new JsonObject { ["attemptId"] = "a", ["status"] = "BLOCKED", ["exitCode"] = null } }
                }
            }, _ => false);
            var reloaded = Invocations.CreateInvocationLedger(dir, () => Iso(0)).View("p");
            Assert.Equal(2000, Num(GroupAt(reloaded["orchestrator"], 0)["durationMs"]));
            Assert.Equal(1, Num(GroupAt(reloaded["orchestrator"], 0)["calls"]));
            Assert.Equal(1, Num(GroupAt(reloaded["workers"], 0)["BLOCKED"]));
            var lead = RecentRole(reloaded, "orchestrator");
            var hand = RecentRole(reloaded, "worker");
            Assert.Equal(id, lead["id"]!.GetValue<string>());
            Assert.Equal(worker, hand["id"]!.GetValue<string>());
            Assert.Equal("reasoning", lead["reason"]!.GetValue<string>());
            Assert.Equal("unread", lead["accountType"]!.GetValue<string>());
            Assert.Equal(12, Num(lead["tokens"]!["input"]));
            Assert.Equal(4, Num(lead["tokens"]!["output"]));
            Assert.False(((JsonObject)lead["tokens"]!).ContainsKey("cachedInput"));
            Assert.False(hand.ContainsKey("tokens"));
            Assert.Equal(123, Num(hand["pid"]));
            Assert.Equal("invocations.jsonl", Path.GetFileName(ledger.File));
            var raw = File.ReadAllText(ledger.File);
            Assert.DoesNotContain("must-not-persist", raw);
            Assert.DoesNotContain("\"env\"", raw);
            Assert.DoesNotContain("\"token\"", raw);
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id);
            Assert.NotEqual(id, worker);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ledger.File));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Supervisor_usage_is_separate_and_unfinished_calls_become_blocked_after_restart()
    {
        var dir = Temp("supervisor");
        try
        {
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(0));
            var id = ledger.Start(new JsonObject
            {
                ["role"] = "supervisor", ["projectId"] = "a", ["provider"] = "api", ["model"] = "exact-fixture", ["modelId"] = "advisory"
            });
            var live = ledger.View("a");
            Assert.Equal(1, Num(GroupAt(live["supervisors"], 0)["RUNNING"]));
            Assert.Empty((JsonArray)live["orchestrator"]!);
            Assert.Empty((JsonArray)live["workers"]!);
            var again = Invocations.CreateInvocationLedger(dir, () => Iso(5000)).View("a");
            var supervisors = GroupAt(again["supervisors"], 0);
            Assert.Equal(1, Num(supervisors["BLOCKED"]));
            Assert.Equal(0, Num(supervisors["RUNNING"]));
            Assert.Equal(1, Num(supervisors["calls"]));
            Assert.Empty((JsonArray)Invocations.CreateInvocationLedger(dir, () => Iso(5000)).View("b")["supervisors"]!);
            Assert.Equal(id, ((JsonArray)again["recent"]!)[0]!["id"]!.GetValue<string>());
            Assert.Equal("unread", ((JsonArray)again["recent"]!)[0]!["accountType"]!.GetValue<string>());
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Reload_blocks_a_running_orchestrator_reviewer_and_supervisor_but_not_a_worker()
    {
        var dir = Temp("roles");
        try
        {
            var first = Invocations.CreateInvocationLedger(dir, () => Iso(0));
            foreach (var role in new[] { "orchestrator", "reviewer", "supervisor", "worker" })
                first.Start(new JsonObject { ["role"] = role, ["projectId"] = "p", ["provider"] = "fixture", ["model"] = role });
            var restored = Invocations.CreateInvocationLedger(dir, () => Iso(1000)).View("p");
            Assert.Equal(1, Num(GroupAt(restored["orchestrator"], 0)["BLOCKED"]));
            Assert.Equal(0, Num(GroupAt(restored["orchestrator"], 0)["RUNNING"]));
            Assert.Equal(1, Num(GroupAt(restored["supervisors"], 0)["BLOCKED"]));
            Assert.Equal("BLOCKED", RecentRole(restored, "reviewer")["status"]!.GetValue<string>());
            Assert.Equal("RUNNING", RecentRole(restored, "worker")["status"]!.GetValue<string>());
            Assert.Equal(1, Num(GroupAt(restored["workers"], 0)["RUNNING"]));
            Assert.Equal(0, Num(GroupAt(restored["workers"], 0)["BLOCKED"]));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Second_finish_and_a_missing_id_append_nothing()
    {
        var dir = Temp("finish");
        try
        {
            long at = 100;
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(at));
            var id = ledger.Start(new JsonObject { ["role"] = "worker", ["projectId"] = "p", ["provider"] = "a", ["model"] = "m" });
            at = 40;
            ledger.Finish(id, "FAILED", JsonValue.Create(2));
            var lines = Lines(ledger.File);
            ledger.Finish(id, "COMPLETE", JsonValue.Create(0), new JsonObject { ["input"] = 1, ["output"] = 1 });
            ledger.Finish("missing", "COMPLETE");
            ledger.Update(id, new JsonObject { ["pid"] = 9, ["token"] = "must-not-persist" });
            Assert.Equal(lines, Lines(ledger.File));
            var row = RecentRole(ledger.View("p"), "worker");
            Assert.Equal("FAILED", row["status"]!.GetValue<string>());
            Assert.Equal(2, Num(row["processExit"]));
            Assert.Equal(0, Num(row["durationMs"]));
            Assert.False(row.ContainsKey("pid"));
            Assert.False(row.ContainsKey("tokens"));
            Assert.DoesNotContain("must-not-persist", File.ReadAllText(ledger.File));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Update_keeps_only_pid_exitPath_and_resultPath_while_running()
    {
        var dir = Temp("update");
        try
        {
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(0));
            var id = Worker(ledger, "a");
            var exit = Path.Combine(dir, "exit.json");
            var result = Path.Combine(dir, "result.txt");
            ledger.Update(id, new JsonObject
            {
                ["pid"] = 44, ["exitPath"] = exit, ["resultPath"] = result, ["env"] = "must-not-persist", ["token"] = "must-not-persist"
            });
            var row = RecentRole(ledger.View(null), "worker");
            Assert.Equal(44, Num(row["pid"]));
            Assert.Equal(exit, row["exitPath"]!.GetValue<string>());
            Assert.Equal(result, row["resultPath"]!.GetValue<string>());
            Assert.False(row.ContainsKey("env"));
            Assert.False(row.ContainsKey("token"));
            Assert.Equal(2, Lines(ledger.File));
            Assert.DoesNotContain("must-not-persist", File.ReadAllText(ledger.File));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Process_exit_and_tokens_keep_only_safe_integers()
    {
        var dir = Temp("tokens");
        try
        {
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(0));
            var bad = Worker(ledger, "bad");
            ledger.Finish(bad, "COMPLETE", JsonValue.Create(1.5), new JsonObject { ["input"] = 1.5, ["output"] = 2 });
            var oversize = Worker(ledger, "unsafe");
            ledger.Finish(oversize, "COMPLETE", JsonValue.Create("0"), new JsonObject { ["input"] = 9007199254740992L, ["output"] = 1 });
            var edge = Worker(ledger, "edge");
            ledger.Finish(edge, "COMPLETE", JsonValue.Create(true), new JsonObject { ["input"] = 9007199254740991L, ["output"] = 0, ["cachedInput"] = 1.5 });
            var cached = Worker(ledger, "cached", provider: "other");
            ledger.Finish(cached, "COMPLETE", null, new JsonObject { ["input"] = -2, ["output"] = 3, ["cachedInput"] = 0, ["secret"] = "must-not-persist" });
            var view = ledger.View("p");
            Assert.True(ById(view, bad)["processExit"] is null);
            Assert.False(ById(view, bad).ContainsKey("tokens"));
            Assert.True(ById(view, oversize)["processExit"] is null);
            Assert.False(ById(view, oversize).ContainsKey("tokens"));
            Assert.True(ById(view, edge)["processExit"] is null);
            Assert.Equal(9007199254740991L, long.Parse(ById(view, edge)["tokens"]!["input"]!.ToJsonString(), CultureInfo.InvariantCulture));
            Assert.False(((JsonObject)ById(view, edge)["tokens"]!).ContainsKey("cachedInput"));
            Assert.Equal(-2, Num(ById(view, cached)["tokens"]!["input"]));
            Assert.Equal(0, Num(ById(view, cached)["tokens"]!["cachedInput"]));
            Assert.Equal(1, Num(GroupAt(view["workers"], 1)["tokenReadings"]));
            Assert.DoesNotContain("must-not-persist", File.ReadAllText(ledger.File));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Reconcile_prefers_the_snapshot_and_reads_optional_usage()
    {
        var dir = Temp("snapshot");
        try
        {
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(0));
            var result = Path.Combine(dir, "out.txt");
            File.WriteAllText(result, "STATUS: COMPLETE\n");
            File.WriteAllText(result + ".usage.json", "{\"input\":8,\"output\":1,\"cachedInput\":2,\"secret\":\"must-not-persist\"}");
            var matched = Worker(ledger, "a", 5, result);
            ledger.Reconcile(new JsonObject
            {
                ["execution"] = new JsonObject { ["results"] = new JsonArray { new JsonObject { ["attemptId"] = "a", ["status"] = "FAILED", ["exitCode"] = 7 } } }
            }, _ => true);
            var row = ById(ledger.View("p"), matched);
            Assert.Equal("FAILED", row["status"]!.GetValue<string>());
            Assert.Equal(7, Num(row["processExit"]));
            Assert.Equal(8, Num(row["tokens"]!["input"]));
            Assert.Equal(2, Num(row["tokens"]!["cachedInput"]));
            var bare = Path.Combine(dir, "bare.txt");
            File.WriteAllText(bare, "STATUS: COMPLETE\n");
            var missingUsage = Worker(ledger, "b", 6, bare);
            var unsafeUsage = Path.Combine(dir, "unsafe.txt");
            File.WriteAllText(unsafeUsage, "STATUS: COMPLETE\n");
            File.WriteAllText(unsafeUsage + ".usage.json", "{\"input\":9007199254740992,\"output\":1}");
            var omitted = Worker(ledger, "c", 7, unsafeUsage);
            ledger.Reconcile(new JsonObject
            {
                ["execution"] = new JsonObject
                {
                    ["results"] = new JsonArray
                    {
                        new JsonObject { ["attemptId"] = "b", ["status"] = "COMPLETE", ["exitCode"] = 0 },
                        new JsonObject { ["attemptId"] = "c", ["status"] = "COMPLETE", ["exitCode"] = 0 }
                    }
                }
            }, _ => true);
            var view = ledger.View("p");
            Assert.Equal("COMPLETE", ById(view, missingUsage)["status"]!.GetValue<string>());
            Assert.False(ById(view, missingUsage).ContainsKey("tokens"));
            Assert.False(ById(view, omitted).ContainsKey("tokens"));
            Assert.DoesNotContain("must-not-persist", File.ReadAllText(ledger.File));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Reconcile_reads_a_dead_workers_exit_and_leading_status()
    {
        var dir = Temp("dead");
        try
        {
            var cases = new (string text, string expect)[]
            {
                ("STATUS: COMPLETE\n", "COMPLETE"),
                ("note\nSTATUS: FAILED\n", "FAILED"),
                ("STATUS: BLOCKED\n", "BLOCKED"),
                ("STATUS: CHECKPOINTED\n", "BLOCKED"),
                ("STATUS: COMPLETED\n", "BLOCKED"),
                ("no status\n", "BLOCKED"),
                ("prefix\rSTATUS: COMPLETE", "COMPLETE")
            };
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(0));
            var ids = new List<(string id, string expect)>();
            for (var n = 0; n < cases.Length; n++)
            {
                var result = Path.Combine(dir, "r" + n + ".txt");
                var exit = Path.Combine(dir, "e" + n + ".json");
                File.WriteAllText(result, cases[n].text);
                File.WriteAllText(exit, n == 0 ? "{\"exitCode\":0}" : "{\"exitCode\":1.5}");
                ids.Add((Worker(ledger, "attempt-" + n, 10 + n, result, exit), cases[n].expect));
            }
            var longPath = Path.Combine(dir, "long.txt");
            File.WriteAllText(longPath, new string('x', 8192) + "STATUS: COMPLETE\n");
            var hidden = Worker(ledger, "long", 99, longPath);
            ledger.Reconcile(null, _ => false);
            var view = ledger.View("p");
            foreach (var (id, expect) in ids) Assert.Equal(expect, ById(view, id)["status"]!.GetValue<string>());
            Assert.Equal(0, Num(ById(view, ids[0].id)["processExit"]));
            Assert.True(ById(view, ids[1].id)["processExit"] is null);
            Assert.Equal("BLOCKED", ById(view, hidden)["status"]!.GetValue<string>());
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Reconcile_leaves_a_row_running_until_the_worker_is_known_dead()
    {
        foreach (var mode in new[] { "alive", "no-pid", "no-fn" })
        {
            var dir = Temp(mode);
            try
            {
                var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(0));
                var result = Path.Combine(dir, "out.txt");
                File.WriteAllText(result, "STATUS: COMPLETE\n");
                var id = Worker(ledger, "a", mode == "no-pid" ? null : 8, result);
                if (mode == "alive") ledger.Reconcile(null, _ => true);
                else if (mode == "no-pid") ledger.Reconcile(null, _ => false);
                else ledger.Reconcile(null, null);
                Assert.Equal("RUNNING", ById(ledger.View("p"), id)["status"]!.GetValue<string>());
            }
            finally { Gone(dir); }
        }
    }

    [Fact]
    public void A_truncated_line_is_kept_and_a_missing_newline_is_repaired_before_new_records()
    {
        var dir = Temp("lines");
        try
        {
            var file = Path.Combine(dir, "invocations.jsonl");
            const string kept = "{\"id\":\"keep\",\"role\":\"worker\",\"status\":\"COMPLETE\",\"projectId\":\"p\",\"provider\":\"a\",\"model\":\"b\",\"durationMs\":4,\"startTime\":\"1970-01-01T00:00:00.000Z\"}";
            File.WriteAllText(file, "{\"id\":\"brok\"\n" + kept);
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(10));
            var id = ledger.Start(new JsonObject { ["role"] = "orchestrator", ["projectId"] = "p", ["provider"] = "a", ["model"] = "b" });
            var raw = File.ReadAllText(file);
            Assert.Contains("{\"id\":\"brok\"", raw, StringComparison.Ordinal);
            Assert.Contains(kept + "\n", raw, StringComparison.Ordinal);
            var loaded = ledger.View("p");
            Assert.Equal(2, ((JsonArray)loaded["recent"]!).Count);
            Assert.Equal("COMPLETE", RecentRole(loaded, "worker")["status"]!.GetValue<string>());
            var again = Invocations.CreateInvocationLedger(dir, () => Iso(20)).View("p");
            Assert.Equal(id, RecentRole(again, "orchestrator")["id"]!.GetValue<string>());
            Assert.Equal("BLOCKED", RecentRole(again, "orchestrator")["status"]!.GetValue<string>());
            Assert.Equal("COMPLETE", RecentRole(again, "worker")["status"]!.GetValue<string>());
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void The_latest_line_for_an_id_replaces_the_row()
    {
        var dir = Temp("dup");
        try
        {
            var file = Path.Combine(dir, "invocations.jsonl");
            File.WriteAllText(file,
                "{\"id\":\"same\",\"role\":\"worker\",\"status\":\"RUNNING\",\"projectId\":\"p\",\"provider\":\"a\",\"model\":\"m\",\"startTime\":\"1970-01-01T00:00:00.000Z\"}\n"
                + "{\"id\":\"same\",\"role\":\"worker\",\"status\":\"COMPLETE\",\"projectId\":\"p\",\"provider\":\"a\",\"model\":\"m\",\"startTime\":\"1970-01-01T00:00:00.000Z\",\"durationMs\":9,\"endTime\":\"1970-01-01T00:00:09.000Z\",\"processExit\":0}\n"
                + "{\"id\":\"later\",\"role\":\"worker\",\"status\":\"RUNNING\",\"projectId\":\"p\",\"provider\":\"a\",\"model\":\"m\"}\n");
            var view = Invocations.CreateInvocationLedger(dir, () => Iso(0)).View("p");
            var recent = (JsonArray)view["recent"]!;
            Assert.Equal(2, recent.Count);
            Assert.Equal("later", recent[0]!["id"]!.GetValue<string>());
            Assert.Equal("COMPLETE", recent[1]!["status"]!.GetValue<string>());
            Assert.Equal(9, Num(recent[1]!["durationMs"]));
            var workers = GroupAt(view["workers"], 0);
            Assert.Equal(2, Num(workers["calls"]));
            Assert.Equal(1, Num(workers["COMPLETE"]));
            Assert.Equal(1, Num(workers["RUNNING"]));
            Assert.Equal(9, Num(workers["durationMs"]));
        }
        finally { Gone(dir); }
    }

    [Fact]
    public void Two_starts_remain_and_view_groups_one_project_and_the_last_twenty()
    {
        var dir = Temp("å folder");
        try
        {
            long at = 0;
            var ledger = Invocations.CreateInvocationLedger(dir, () => Iso(at));
            var other = Worker(ledger, "o", project: "other", provider: "a");
            var left = Worker(ledger, "l", provider: "a");
            var right = Worker(ledger, "r", provider: "a");
            var third = Worker(ledger, "t", provider: "b");
            at = 4000;
            ledger.Finish(left, "COMPLETE", JsonValue.Create(0), new JsonObject { ["input"] = 3, ["output"] = 4, ["accountType"] = "normal" });
            var hidden = ledger.View("other");
            Assert.Single((JsonArray)hidden["recent"]!);
            Assert.Equal(other, RecentRole(hidden, "worker")["id"]!.GetValue<string>());
            var view = ledger.View("p");
            Assert.Equal(3, ((JsonArray)view["recent"]!).Count);
            var same = GroupAt(view["workers"], 0);
            var apart = GroupAt(view["workers"], 1);
            Assert.Equal("a", same["provider"]!.GetValue<string>());
            Assert.Equal(2, Num(same["calls"]));
            Assert.Equal(1, Num(same["COMPLETE"]));
            Assert.Equal(1, Num(same["RUNNING"]));
            Assert.Equal(4000, Num(same["durationMs"]));
            Assert.Equal(3, Num(same["inputTokens"]));
            Assert.Equal(4, Num(same["outputTokens"]));
            Assert.Equal(1, Num(same["tokenReadings"]));
            Assert.Equal("unread", ById(view, left)["accountType"]!.GetValue<string>());
            Assert.Equal("b", apart["provider"]!.GetValue<string>());
            Assert.Equal(1, Num(apart["RUNNING"]));
            Assert.False(apart.ContainsKey("inputTokens"));
            Assert.NotEqual(left, right);
            Assert.NotEqual(left, third);
            var restored = Invocations.CreateInvocationLedger(dir, () => Iso(0)).View("p");
            Assert.Equal(left, ById(restored, left)["id"]!.GetValue<string>());
            Assert.Equal(right, ById(restored, right)["id"]!.GetValue<string>());
            Assert.Equal("RUNNING", ById(restored, right)["status"]!.GetValue<string>());
            for (var n = 0; n < 18; n++) Worker(ledger, "extra-" + n);
            var recent = (JsonArray)ledger.View("p")["recent"]!;
            Assert.Equal(20, recent.Count);
            Assert.Equal("extra-17", recent[0]!["attemptId"]!.GetValue<string>());
            Assert.DoesNotContain(recent.Select(item => item!["id"]!.GetValue<string>()), seen => seen == left);
        }
        finally { Gone(dir); }
    }
}
