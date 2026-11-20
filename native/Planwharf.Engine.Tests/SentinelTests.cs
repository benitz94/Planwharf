// Deferred, not implemented: attempts.js, createAttemptEngine, and every sentinel test that needs the real store.
// The three fake-engine cases from engine/attempts.test.cjs are in scope. Phase 3 owns the store.
using System.Text.Json;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class SentinelTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("pw-engine-sentinel-").FullName;
    public void Dispose() => Directory.Delete(folder, true);

    string Write(string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    static string? Status(JsonObject? verdict) =>
        verdict is null || !verdict.ContainsKey("status") ? null : verdict["status"] is null ? "null" : verdict["status"]!.GetValue<string>();

    [Fact]
    public void ReadResultFile_blank_missing_and_unreadable_paths_are_null()
    {
        Assert.Null(Sentinel.ReadResultFile(null));
        Assert.Null(Sentinel.ReadResultFile(""));
        Assert.Null(Sentinel.ReadResultFile(" \t"));
        Assert.Null(Sentinel.ReadResultFile(Path.Combine(folder, "missing.txt")));
        Assert.Null(Sentinel.ReadResultFile(folder));
        Assert.Null(Sentinel.ReadResultFile("bad\0path"));
        var locked = Path.Combine(folder, "locked.txt");
        using (var held = new FileStream(locked, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            held.WriteByte((byte)'x');
            Assert.Null(Sentinel.ReadResultFile(locked));
        }
    }

    [Fact]
    public void ReadResultFile_COMPLETE_wins_FAILED_is_a_line_and_COMPLETED_is_not_COMPLETE()
    {
        var complete = Sentinel.ReadResultFile(Write("complete.txt", "  status:\tcomplete  \r\n"));
        Assert.Equal("COMPLETE", Status(complete));
        Assert.Equal("  status:\tcomplete  \r\n", complete!["raw"]!.GetValue<string>());

        var failed = Sentinel.ReadResultFile(Write("failed.txt", "note\nSTATUS: FAILED\n"));
        Assert.Equal("FAILED", Status(failed));

        var both = Sentinel.ReadResultFile(Write("both.txt", "STATUS: FAILED\nSTATUS: COMPLETE\n"));
        Assert.Equal("COMPLETE", Status(both));
        var completeFirst = Sentinel.ReadResultFile(Write("complete-first.txt", "STATUS: COMPLETE\nSTATUS: FAILED\n"));
        Assert.Equal("COMPLETE", Status(completeFirst));

        var completed = Sentinel.ReadResultFile(Write("completed.txt", "STATUS: COMPLETED\n"));
        Assert.Equal("null", Status(completed));
        Assert.Equal("STATUS: COMPLETED\n", completed!["raw"]!.GetValue<string>());

        var other = Sentinel.ReadResultFile(Write("other.txt", "xx STATUS: COMPLETE\nSTATUS: COMPLETE extra\n"));
        Assert.Equal("null", Status(other));
        var empty = Sentinel.ReadResultFile(Write("empty.txt", ""));
        Assert.Equal("null", Status(empty));
        Assert.Equal("", empty!["raw"]!.GetValue<string>());
    }

    [Fact(DisplayName = "sentinel: written COMPLETE ends waiting even if process is alive")]
    public void Written_COMPLETE_ends_waiting_even_if_process_is_alive()
    {
        var resultPath = Write("task.result.txt", "STATUS: COMPLETE\n");
        var row = Attempt();
        row["resultPath"] = resultPath;
        row["resultStatus"] = "COMPLETE";
        var store = new FakeStore(row);
        var passed = Attempt();
        var tick = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks { IsAlive = _ => true }).Tick(passed);
        Assert.Equal("""{"waiting":false,"complete":true,"closed":false,"reassigned":false,"reason":"result file"}""", tick.ToJsonString());
        Assert.Empty(store.Closes);
        Assert.Equal("active", store.GetActive()!["status"]!.GetValue<string>());
    }

    [Fact(DisplayName = "sentinel: missing verdict closes the attempt")]
    public void Missing_verdict_closes_the_attempt()
    {
        var store = new FakeStore(Attempt());
        var tick = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks { IsAlive = _ => false }).Tick(Attempt());
        Assert.Equal("""{"waiting":false,"complete":false,"closed":true,"reassigned":false,"reason":"no verdict"}""", tick.ToJsonString());
        Assert.Null(store.GetActive());
        var closed = store.GetResult();
        Assert.Equal("closed", closed!["status"]!.GetValue<string>());
        Assert.Equal("no verdict", closed["closedReason"]!.GetValue<string>());
        Assert.Equal("attempt-1", store.Closes[0]["attemptId"]!.GetValue<string>());
    }

    [Fact(DisplayName = "sentinel: does not open a second attempt while the first is active")]
    public void Does_not_open_a_second_attempt_while_the_first_is_active()
    {
        var store = new FakeStore(Attempt());
        var extra = new JsonObject { ["candidates"] = new JsonArray("mod-c"), ["modelId"] = "mod-c" };
        var tick = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks { IsAlive = _ => true })
            .Tick(Attempt(), extra);
        Assert.Equal("""{"waiting":true,"complete":false,"closed":false,"reassigned":false,"reason":"claim still valid"}""", tick.ToJsonString());
        Assert.Empty(store.Claims);
        var second = store.Engine().Claim!(extra);
        Assert.False(second!["ok"]!.GetValue<bool>());
        Assert.Equal("attempt-1", store.Row["id"]!.GetValue<string>());
    }

    [Fact]
    public void Tick_rejects_a_missing_engine_or_a_non_object_attempt()
    {
        var row = Attempt();
        Assert.Equal("invalid data", Sentinel.CreateSentinel(null).Tick(row)["reason"]!.GetValue<string>());
        Assert.Equal("invalid data", Sentinel.CreateSentinel(new SentinelEngine()).Tick(null)["reason"]!.GetValue<string>());
        Assert.Equal("invalid data", Sentinel.CreateSentinel(new SentinelEngine()).Tick(new JsonArray())["reason"]!.GetValue<string>());
        Assert.Equal("invalid data", Sentinel.CreateSentinel(new SentinelEngine()).Tick(JsonValue.Create("attempt"))["reason"]!.GetValue<string>());
    }

    [Fact]
    public void A_COMPLETE_record_or_file_finishes_without_closing_even_when_the_process_is_alive()
    {
        var alive = 0;
        var store = new FakeStore(Attempt());
        store.Row["resultStatus"] = "COMPLETE";
        var fromRecord = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks { IsAlive = _ => { alive++; return true; } }).Tick(Attempt());
        Assert.Equal("result file", fromRecord["reason"]!.GetValue<string>());
        Assert.True(fromRecord["complete"]!.GetValue<bool>());
        Assert.Equal(0, alive);
        Assert.Empty(store.Closes);

        var recordedPath = Write("recorded.txt", "STATUS: COMPLETE\n");
        var attemptPath = Write("attempt.txt", "STATUS: FAILED\n");
        store = new FakeStore(Attempt());
        store.Row["resultPath"] = recordedPath;
        var passed = Attempt();
        passed["resultPath"] = attemptPath;
        var fromFile = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks { IsAlive = _ => true }).Tick(passed);
        Assert.Equal("result file", fromFile["reason"]!.GetValue<string>());
        Assert.Empty(store.Closes);
    }

    [Fact]
    public void A_dead_process_with_no_verdict_closes_only_an_active_row()
    {
        var paused = Attempt();
        paused["status"] = "paused";
        var store = new FakeStore(paused);
        var tick = Sentinel.CreateSentinel(store.Engine()).Tick(paused);
        Assert.Equal("no verdict", tick["reason"]!.GetValue<string>());
        Assert.True(tick["closed"]!.GetValue<bool>());
        Assert.Empty(store.Closes);
        Assert.Equal("paused", store.Row["status"]!.GetValue<string>());

        store = new FakeStore(Attempt());
        var noClose = Sentinel.CreateSentinel(new SentinelEngine { GetResult = _ => store.Row.DeepClone() }).Tick(Attempt());
        Assert.True(noClose["closed"]!.GetValue<bool>());
        Assert.Equal("active", store.Row["status"]!.GetValue<string>());
    }

    [Fact]
    public void FAILED_is_a_verdict_and_does_not_close_or_complete()
    {
        var path = Write("failed-tick.txt", "STATUS: FAILED\n");
        var store = new FakeStore(Attempt());
        store.Row["resultPath"] = path;
        JsonObject? seen = null;
        var built = store.Engine();
        var engine = new SentinelEngine
        {
            GetResult = built.GetResult,
            CloseAttempt = built.CloseAttempt,
            GetActiveAttempt = query => { seen = query.DeepClone().AsObject(); return null; },
            Claim = built.Claim
        };
        var tick = Sentinel.CreateSentinel(engine, new SentinelHooks { IsAlive = _ => false }).Tick(Attempt());
        Assert.Equal("""{"waiting":true,"complete":false,"closed":false,"reassigned":false,"reason":"waiting"}""", tick.ToJsonString());
        Assert.Empty(store.Closes);
        Assert.Equal("task-1", seen!["taskId"]!.GetValue<string>());
        Assert.Equal("biblioteca", seen["projectId"]!.GetValue<string>());
    }

    [Fact]
    public void An_active_id_blocks_reassignment_and_a_missing_lookup_uses_the_recorded_row()
    {
        var store = new FakeStore(Attempt());
        var extra = new JsonObject { ["candidates"] = new JsonArray(), ["modelId"] = "mod-c" };
        var blocked = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), extra);
        Assert.Equal("claim still valid", blocked["reason"]!.GetValue<string>());
        Assert.Empty(store.Claims);

        var engine = new SentinelEngine { GetResult = _ => store.Row.DeepClone() };
        var fallback = Sentinel.CreateSentinel(engine, new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), extra);
        Assert.Equal("claim still valid", fallback["reason"]!.GetValue<string>());

        store.Row["id"] = "";
        var open = Sentinel.CreateSentinel(new SentinelEngine
        {
            GetResult = _ => store.Row.DeepClone(),
            Claim = _ => new JsonObject { ["ok"] = true }
        }, new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), extra);
        Assert.Equal("reassigned", open["reason"]!.GetValue<string>());
        Assert.Null(open["attempt"]);
    }

    [Fact]
    public void Reassign_passes_the_next_attempt_or_the_next_reason()
    {
        JsonObject? seen = null;
        var candidates = new JsonArray("mod-a", "mod-c");
        var engine = new SentinelEngine
        {
            GetResult = _ => new JsonObject { ["status"] = "closed" },
            GetActiveAttempt = _ => null,
            Claim = args =>
            {
                seen = args.DeepClone().AsObject();
                return new JsonObject
                {
                    ["ok"] = true,
                    ["attempt"] = new JsonObject { ["id"] = "attempt-2", ["status"] = "active" }
                };
            }
        };
        var extra = new JsonObject { ["candidates"] = candidates, ["modelId"] = "mod-c" };
        var tick = Sentinel.CreateSentinel(engine, new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), extra);
        Assert.Equal("reassigned", tick["reason"]!.GetValue<string>());
        Assert.True(tick["waiting"]!.GetValue<bool>());
        Assert.True(tick["reassigned"]!.GetValue<bool>());
        Assert.False(tick["complete"]!.GetValue<bool>());
        Assert.Equal("attempt-2", tick["attempt"]!["id"]!.GetValue<string>());
        Assert.Equal("mod-c", seen!["modelId"]!.GetValue<string>());
        Assert.Equal("task-1", seen["taskId"]!.GetValue<string>());
        Assert.Equal("biblioteca", seen["projectId"]!.GetValue<string>());
        Assert.Equal("mod-a", seen["candidates"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(2, candidates.Count);

        var refused = Sentinel.CreateSentinel(new SentinelEngine
        {
            GetResult = engine.GetResult,
            GetActiveAttempt = engine.GetActiveAttempt,
            Claim = _ => new JsonObject { ["ok"] = false, ["reason"] = "model not eligible" }
        }, new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), extra);
        Assert.Equal("model not eligible", refused["reason"]!.GetValue<string>());
        Assert.False(refused["waiting"]!.GetValue<bool>());
        Assert.False(refused["reassigned"]!.GetValue<bool>());
        Assert.Null(refused["attempt"]);

        var missing = Sentinel.CreateSentinel(new SentinelEngine
        {
            GetResult = engine.GetResult,
            GetActiveAttempt = engine.GetActiveAttempt,
            Claim = _ => null
        }, new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), extra);
        Assert.Equal("not reassigned", missing["reason"]!.GetValue<string>());
        Assert.Null(missing["attempt"]);
    }

    [Fact]
    public void Hooks_default_to_the_file_and_a_dead_process_and_blank_ids_do_not_reassign()
    {
        var path = Write("hook.txt", "STATUS: FAILED\n");
        var reads = 0;
        var store = new FakeStore(Attempt());
        store.Row["resultPath"] = path;
        var hooked = Sentinel.CreateSentinel(store.Engine(), new SentinelHooks
        {
            ReadResult = _ => { reads++; return new JsonObject { ["status"] = "COMPLETE", ["raw"] = "hook" }; }
        }).Tick(Attempt());
        Assert.Equal("result file", hooked["reason"]!.GetValue<string>());
        Assert.Equal(1, reads);

        var blank = new JsonObject { ["candidates"] = "", ["modelId"] = "mod-c" };
        var waiting = Sentinel.CreateSentinel(new SentinelEngine
        {
            GetResult = _ => new JsonObject { ["status"] = "closed" },
            GetActiveAttempt = _ => new JsonObject { ["id"] = "" }
        }, new SentinelHooks { IsAlive = _ => true }).Tick(Attempt(), blank);
        Assert.Equal("waiting", waiting["reason"]!.GetValue<string>());

        var recorded = Attempt();
        recorded["resultPath"] = JsonValue.Create(1);
        var passed = Attempt();
        passed["resultPath"] = Write("ignored-complete.txt", "STATUS: COMPLETE\n");
        var ignored = Sentinel.CreateSentinel(new SentinelEngine { GetResult = _ => recorded }, new SentinelHooks { IsAlive = _ => true })
            .Tick(passed);
        Assert.Equal("claim still valid", ignored["reason"]!.GetValue<string>());

        recorded = Attempt();
        recorded["resultPath"] = "";
        passed = Attempt();
        passed["resultPath"] = Write("fallback.txt", "status: complete");
        var fallback = Sentinel.CreateSentinel(new SentinelEngine { GetResult = _ => recorded }, new SentinelHooks { IsAlive = _ => true })
            .Tick(passed);
        Assert.Equal("result file", fallback["reason"]!.GetValue<string>());
    }

    static JsonObject Attempt() => new()
    {
        ["id"] = "attempt-1",
        ["status"] = "active",
        ["taskId"] = "task-1",
        ["projectId"] = "biblioteca"
    };

    sealed class FakeStore
    {
        public JsonObject Row;
        public List<JsonObject> Closes { get; } = [];
        public List<JsonObject> Claims { get; } = [];

        public FakeStore(JsonObject row) => Row = row;

        public SentinelEngine Engine() => new()
        {
            GetResult = _ => Row.DeepClone(),
            CloseAttempt = args =>
            {
                Closes.Add(args.DeepClone().AsObject());
                Row["status"] = "closed";
                Row["closedReason"] = args["reason"]?.DeepClone();
            },
            GetActiveAttempt = _ =>
                Row["status"]?.GetValue<string>() == "active" ? Row.DeepClone() : null,
            Claim = args =>
            {
                Claims.Add(args.DeepClone().AsObject());
                if (Row["status"]?.GetValue<string>() == "active")
                    return new JsonObject { ["ok"] = false, ["reason"] = "claim still valid" };
                return new JsonObject { ["ok"] = true, ["attempt"] = new JsonObject { ["id"] = "attempt-2" } };
            }
        };

        public JsonObject? GetActive() => Engine().GetActiveAttempt!(new JsonObject()) as JsonObject;
        public JsonObject? GetResult() => Engine().GetResult!(null) as JsonObject;
    }
}
