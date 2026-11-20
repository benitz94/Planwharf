// Port of engine/sentinel.js.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Duck-typed stand-in for the attempt store. Phase 3 owns attempts.js.
public sealed class SentinelEngine
{
    public Func<JsonNode?, JsonNode?>? GetResult { get; init; }
    public Action<JsonObject>? CloseAttempt { get; init; }
    public Func<JsonObject, JsonNode?>? GetActiveAttempt { get; init; }
    public Func<JsonObject, JsonNode?>? Claim { get; init; }
}

public sealed class SentinelHooks
{
    public Func<JsonObject, JsonNode?>? ReadResult { get; init; }
    public Func<JsonObject, bool>? IsAlive { get; init; }
}

public sealed class SentinelSession
{
    readonly SentinelEngine? engine;
    readonly SentinelHooks hooks;

    internal SentinelSession(SentinelEngine? engine, SentinelHooks hooks)
    {
        this.engine = engine;
        this.hooks = hooks;
    }

    public JsonObject Tick(JsonNode? attempt, JsonObject? extra = null) =>
        Sentinel.Tick(engine, hooks, attempt, extra);
}

public static class Sentinel
{
    // JavaScript \s, without the extra characters .NET \s matches.
    const string Ws = @"[\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]";
    const RegexOptions Js = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex CompleteLine = new("(^|\n)" + Ws + "*STATUS:" + Ws + "*COMPLETE" + Ws + @"*(\n|$)", Js);
    static readonly Regex FailedLine = new("(^|\n)" + Ws + "*STATUS:" + Ws + "*FAILED" + Ws + @"*(\n|$)", Js);

    public static SentinelSession CreateSentinel(SentinelEngine? engine, SentinelHooks? hooks = null) =>
        new(engine, hooks ?? new SentinelHooks());

    public static JsonObject? ReadResultFile(string? resultPath)
    {
        if (resultPath is null || StructuredFinal.Trim(resultPath).Length == 0) return null;
        try
        {
            if (!File.Exists(resultPath)) return null;
            var raw = File.ReadAllText(resultPath);
            if (CompleteLine.IsMatch(raw)) return Verdict("COMPLETE", raw);
            if (FailedLine.IsMatch(raw)) return Verdict("FAILED", raw);
            return Verdict(null, raw);
        }
        catch (Exception)
        {
            // A thrown read answers null, the same as a missing file. There is no browser require() branch.
            return null;
        }
    }

    internal static JsonObject Tick(SentinelEngine? engine, SentinelHooks hooks, JsonNode? attempt, JsonObject? extra)
    {
        if (engine is null || attempt is not JsonObject row)
            return Done(false, false, false, false, "invalid data");

        var recorded = engine.GetResult is { } getResult ? getResult(row["id"]?.DeepClone()) : row;
        var fileVerdict = hooks.ReadResult is { } read ? read(row) : ReadResultFile(ResultPath(recorded, row));
        var fileStatus = fileVerdict is JsonObject verdict ? verdict["status"] : null;
        var attachedComplete = recorded is JsonObject recordedObject
            && StructuredFinal.String(recordedObject["resultStatus"]) == "COMPLETE";
        if (StructuredFinal.String(fileStatus) == "COMPLETE" || attachedComplete)
            return Done(false, true, false, false, "result file");

        var alive = hooks.IsAlive is { } isAlive && isAlive(row);
        if (!alive && !StructuredFinal.Truthy(fileStatus))
        {
            if (engine.CloseAttempt is { } close
                && recorded is JsonObject activeRow
                && StructuredFinal.String(activeRow["status"]) == "active")
            {
                var closing = new JsonObject { ["reason"] = "no verdict" };
                closing["attemptId"] = row["id"]?.DeepClone() ?? JsonNode.Parse("null");
                close(closing);
            }
            // closed stays true even when the row was not active and closeAttempt was not called.
            return Done(false, false, true, false, "no verdict");
        }

        JsonNode? active = engine.GetActiveAttempt is { } getActive
            ? getActive(Query(row))
            : recorded is JsonObject still && StructuredFinal.String(still["status"]) == "active" ? still : null;
        if (active is JsonObject activeObject && StructuredFinal.Truthy(activeObject["id"]))
            return Done(true, false, false, false, "claim still valid");

        var opts = extra ?? new JsonObject();
        if (StructuredFinal.Truthy(opts["candidates"]) && StructuredFinal.Truthy(opts["modelId"]) && engine.Claim is { } claim)
        {
            var next = claim(new JsonObject
            {
                ["taskId"] = row["taskId"]?.DeepClone(),
                ["projectId"] = row["projectId"]?.DeepClone(),
                ["modelId"] = opts["modelId"]!.DeepClone(),
                ["candidates"] = opts["candidates"]!.DeepClone()
            });
            var nextObject = next as JsonObject;
            var ok = nextObject is not null && StructuredFinal.Truthy(nextObject["ok"]);
            JsonNode reason = ok
                ? JsonValue.Create("reassigned")!
                : nextObject is not null && StructuredFinal.Truthy(nextObject["reason"])
                    ? nextObject["reason"]!.DeepClone()
                    : JsonValue.Create("not reassigned")!;
            var reassigned = Done(ok, false, false, ok, reason);
            reassigned["attempt"] = nextObject?["attempt"]?.DeepClone() ?? JsonNode.Parse("null");
            return reassigned;
        }

        return Done(true, false, false, false, "waiting");
    }

    static string? ResultPath(JsonNode? recorded, JsonObject attempt)
    {
        var recordedPath = TruthyPath(recorded);
        if (recordedPath is not null) return AsPath(recordedPath);
        return AsPath(TruthyPath(attempt));
    }

    static JsonNode? TruthyPath(JsonNode? record)
    {
        if (record is not JsonObject o) return null;
        var path = o["resultPath"];
        return StructuredFinal.Truthy(path) ? path : null;
    }

    static string? AsPath(JsonNode? path) =>
        path is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static JsonObject Query(JsonObject row)
    {
        var query = new JsonObject();
        query["taskId"] = row["taskId"]?.DeepClone() ?? JsonNode.Parse("null");
        query["projectId"] = row["projectId"]?.DeepClone() ?? JsonNode.Parse("null");
        return query;
    }

    static JsonObject Verdict(string? status, string raw)
    {
        var verdict = new JsonObject();
        verdict["status"] = status is null ? JsonNode.Parse("null") : JsonValue.Create(status);
        verdict["raw"] = raw;
        return verdict;
    }

    static JsonObject Done(bool waiting, bool complete, bool closed, bool reassigned, string reason) =>
        Done(waiting, complete, closed, reassigned, JsonValue.Create(reason)!);

    static JsonObject Done(bool waiting, bool complete, bool closed, bool reassigned, JsonNode reason) => new()
    {
        ["waiting"] = waiting,
        ["complete"] = complete,
        ["closed"] = closed,
        ["reassigned"] = reassigned,
        ["reason"] = reason
    };
}
