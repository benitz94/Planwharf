// Planwharf-owned worker MCP session store (job 25, sheet 18).
// Not a JavaScript port. The engine binds every call to the attempt it opened.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class WorkerMcp
{
    // Documented caps. Oversized text is rejected, never shortened.
    public const int ProgressMax = 280;
    public const int AskQuestionMax = 2000;
    public const int DecisionMax = 32000;
    public const int HandoffMax = 8000;
    public const int AnswerMax = 8000;
    public const int DecisionAnswerMax = 32000;
    public const int SummaryMax = 8000;
    public const int NextActionMax = 2000;
    public const int DefaultAskTimeoutMs = 120_000;
    public const int MaxAskTimeoutMs = 600_000;
    public const int JsonMax = 256 * 1024;
    public const int FilesMax = 200;
    public const int PathItemMax = 500;
    public const int BlockersMax = 20;
    public const int BlockerMax = 500;
    public const int TestsMax = 40;
    public const int CommandMax = 500;
    public const int EvidenceMax = 2000;
    public const int SourcesMax = 40;
    public const int SourceMax = 500;
    public const int ReportPathMax = 1024;
    public const int RecommendedMax = 2000;
    public const long StaleConnectionMs = 2000;

    public sealed record Bind(
        string ProjectId, string Worktree, string ResultId, string AttemptId,
        string SubTaskId, string MegaTaskId, string Provider, string Model);

    public sealed record Collection(bool Completed, string Reason, JsonObject? Result);

    public sealed record ToolReply(bool Ok, string Code, JsonObject? Body)
    {
        public static ToolReply Accepted(JsonObject? body = null) => new(true, "ok", body);
        public static ToolReply Reject(string code) => new(false, code, null);
    }

    public readonly record struct Call(string Token, string ConnectionId);

    public static string Open(string root, Bind bind, int askTimeoutMs = DefaultAskTimeoutMs)
    {
        if (askTimeoutMs < 1 || askTimeoutMs > MaxAskTimeoutMs) throw new ArgumentOutOfRangeException(nameof(askTimeoutMs));
        RequireLabel(bind.ProjectId, 200);
        RequireLabel(bind.Worktree, 1024);
        RequireLabel(bind.ResultId, 200);
        RequireLabel(bind.AttemptId, 200);
        RequireLabel(bind.SubTaskId, 200);
        RequireLabel(bind.MegaTaskId, 200);
        RequireLabel(bind.Provider, 80);
        RequireLabel(bind.Model, 200);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var session = new JsonObject
        {
            ["v"] = 1,
            ["token"] = token,
            ["projectId"] = bind.ProjectId,
            ["worktree"] = bind.Worktree,
            ["resultId"] = bind.ResultId,
            ["attemptId"] = bind.AttemptId,
            ["subTaskId"] = bind.SubTaskId,
            ["megaTaskId"] = bind.MegaTaskId,
            ["provider"] = bind.Provider,
            ["model"] = bind.Model,
            ["state"] = "active",
            ["askTimeoutMs"] = askTimeoutMs,
            ["connectionId"] = "",
            ["heartbeatMs"] = 0,
            ["progress"] = new JsonArray(),
            ["ask"] = null,
            ["decision"] = null,
            ["handoff"] = null,
            ["finish"] = null,
            ["recovery"] = ""
        };
        Directory.CreateDirectory(Dir(root));
        WithSession(root, token, session, _ => true);
        Audit(root, bind.AttemptId, "open", "ok");
        return token;
    }

    public static ToolReply Attach(string root, string token, long nowMs, long staleAfterMs = StaleConnectionMs)
    {
        if (!ValidToken(token)) return ToolReply.Reject("unknown-session");
        return WithSession(root, token, null, session =>
        {
            if (session is null) return ToolReply.Reject("unknown-session");
            var live = Text(session, "connectionId").Length > 0 && nowMs - Long(session, "heartbeatMs") < staleAfterMs;
            if (live) return ToolReply.Reject("busy-connection");
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            session["connectionId"] = id;
            session["heartbeatMs"] = nowMs;
            return ToolReply.Accepted(new JsonObject { ["connectionId"] = id });
        });
    }

    public static void Detach(string root, Call call, long nowMs)
    {
        if (!ValidToken(call.Token)) return;
        WithSession(root, call.Token, null, session =>
        {
            if (session is null || Text(session, "connectionId") != call.ConnectionId) return false;
            session["connectionId"] = "";
            session["heartbeatMs"] = nowMs;
            return true;
        });
    }

    public static ToolReply Progress(string root, Call call, string? status, long nowMs)
    {
        var text = CheckLine(status, ProgressMax);
        if (text.Code != "ok") return ToolReply.Reject(text.Code);
        return Mutate(root, call, nowMs, "progress", true, session =>
        {
            if (Text(session, "state") is "ended") return ToolReply.Reject("ended");
            if (Text(session, "state") is "waiting-decision") return ToolReply.Reject("paused");
            session["progress"]!.AsArray().Add((JsonNode)new JsonObject { ["atMs"] = nowMs, ["text"] = text.Value });
            return ToolReply.Accepted();
        });
    }

    public static JsonArray ProgressOf(string root, string token)
    {
        var session = Read(root, token);
        return session?["progress"]?.DeepClone()?.AsArray() ?? new JsonArray();
    }

    public static void End(string root, string token)
    {
        if (!ValidToken(token)) return;
        WithSession(root, token, null, session =>
        {
            if (session is null) return false;
            session["state"] = "ended";
            Audit(root, Text(session, "attemptId"), "end", "ok");
            return true;
        });
    }

    public static ToolReply Ask(string root, Call call, string? question, bool forOwner, long nowMs)
    {
        var text = CheckBlock(question, AskQuestionMax);
        if (text.Code != "ok") return ToolReply.Reject(text.Code);
        return Mutate(root, call, nowMs, "ask", true, session =>
        {
            if (Text(session, "state") is "ended") return ToolReply.Reject("ended");
            if (Text(session, "state") is "waiting-decision") return ToolReply.Reject("paused");
            if (session["ask"] is JsonObject pending && Text(pending, "status") == "pending")
                return Text(pending, "question") == text.Value ? new ToolReply(true, "pending", null) : ToolReply.Reject("ask-pending");
            if (Text(session, "state") != "active") return ToolReply.Reject("paused");
            var timeout = (int)Long(session, "askTimeoutMs");
            session["state"] = "waiting-ask";
            session["ask"] = new JsonObject
            {
                ["question"] = text.Value,
                ["route"] = forOwner ? "owner" : "lead",
                ["status"] = "pending",
                ["answer"] = "",
                ["deadlineMs"] = nowMs + timeout
            };
            Audit(root, Text(session, "attemptId"), "ask", "pending");
            return new ToolReply(true, "pending", null);
        });
    }

    public static ToolReply PollAsk(string root, Call call, long nowMs)
    {
        return Mutate(root, call, nowMs, "ask", false, session =>
        {
            if (session["ask"] is not JsonObject ask) return ToolReply.Reject("not-pending");
            if (Text(ask, "status") == "answered") return ToolReply.Accepted(new JsonObject { ["answer"] = Text(ask, "answer") });
            if (Text(ask, "status") is "cancelled" or "timed-out") return ToolReply.Reject(Text(ask, "status"));
            if (Text(ask, "status") != "pending") return ToolReply.Reject("not-pending");
            if (nowMs >= Long(ask, "deadlineMs"))
            {
                ask["status"] = "timed-out";
                session["state"] = "active";
                Audit(root, Text(session, "attemptId"), "ask", "timed-out");
                return ToolReply.Reject("timed-out");
            }
            return new ToolReply(true, "pending", null);
        });
    }

    public static ToolReply AnswerAsk(string root, string token, string? answer, bool byOwner)
    {
        var text = CheckBlock(answer, AnswerMax);
        if (text.Code != "ok") return ToolReply.Reject(text.Code);
        if (!ValidToken(token)) return ToolReply.Reject("unknown-session");
        return WithSession(root, token, null, session =>
        {
            if (session is null) return ToolReply.Reject("unknown-session");
            if (session["ask"] is not JsonObject ask || Text(ask, "status") != "pending") return ToolReply.Reject("not-pending");
            var route = Text(ask, "route");
            if (route == "owner" && !byOwner || route == "lead" && byOwner) return ToolReply.Reject("wrong-route");
            ask["status"] = "answered";
            ask["answer"] = text.Value;
            session["state"] = "active";
            Audit(root, Text(session, "attemptId"), "ask", "answered");
            return ToolReply.Accepted();
        });
    }

    public static ToolReply CancelAsk(string root, string token)
    {
        if (!ValidToken(token)) return ToolReply.Reject("unknown-session");
        return WithSession(root, token, null, session =>
        {
            if (session is null) return ToolReply.Reject("unknown-session");
            if (session["ask"] is not JsonObject ask || Text(ask, "status") != "pending") return ToolReply.Reject("not-pending");
            ask["status"] = "cancelled";
            session["state"] = "active";
            Audit(root, Text(session, "attemptId"), "ask", "cancelled");
            return ToolReply.Accepted();
        });
    }

    public static ToolReply Decision(string root, Call call, string? question, long nowMs)
    {
        var text = CheckBlock(question, DecisionMax);
        if (text.Code != "ok") return ToolReply.Reject(text.Code);
        return Mutate(root, call, nowMs, "decision", true, session =>
        {
            if (Text(session, "state") is "ended") return ToolReply.Reject("ended");
            if (Text(session, "state") is "waiting-ask") return ToolReply.Reject("paused");
            if (session["decision"] is JsonObject pending && Text(pending, "status") == "pending")
                return Text(pending, "question") == text.Value ? new ToolReply(true, "pending", null) : ToolReply.Reject("decision-pending");
            if (Text(session, "state") != "active") return ToolReply.Reject("paused");
            session["state"] = "waiting-decision";
            session["decision"] = new JsonObject
            {
                ["question"] = text.Value,
                ["status"] = "pending",
                ["answer"] = "",
                ["highlighted"] = true
            };
            Audit(root, Text(session, "attemptId"), "decision", "pending");
            return new ToolReply(true, "pending", null);
        });
    }

    public static ToolReply PollDecision(string root, Call call, long nowMs)
    {
        return Mutate(root, call, nowMs, "decision", false, session =>
        {
            if (session["decision"] is not JsonObject decision) return ToolReply.Reject("not-pending");
            if (Text(decision, "status") == "answered") return ToolReply.Accepted(new JsonObject { ["answer"] = Text(decision, "answer") });
            if (Text(decision, "status") == "cancelled") return ToolReply.Reject("cancelled");
            if (Text(decision, "status") != "pending") return ToolReply.Reject("not-pending");
            return new ToolReply(true, "pending", null);
        });
    }

    public static ToolReply AnswerDecision(string root, string token, string? answer)
    {
        var text = CheckBlock(answer, DecisionAnswerMax);
        if (text.Code != "ok") return ToolReply.Reject(text.Code);
        if (!ValidToken(token)) return ToolReply.Reject("unknown-session");
        return WithSession(root, token, null, session =>
        {
            if (session is null) return ToolReply.Reject("unknown-session");
            if (session["decision"] is not JsonObject decision || Text(decision, "status") != "pending") return ToolReply.Reject("not-pending");
            decision["status"] = "answered";
            decision["answer"] = text.Value;
            decision["highlighted"] = true;
            session["state"] = "active";
            Audit(root, Text(session, "attemptId"), "decision", "answered");
            return ToolReply.Accepted();
        });
    }

    public static ToolReply CancelDecision(string root, string token)
    {
        if (!ValidToken(token)) return ToolReply.Reject("unknown-session");
        return WithSession(root, token, null, session =>
        {
            if (session is null) return ToolReply.Reject("unknown-session");
            if (session["decision"] is not JsonObject decision || Text(decision, "status") != "pending") return ToolReply.Reject("not-pending");
            decision["status"] = "cancelled";
            session["state"] = "active";
            Audit(root, Text(session, "attemptId"), "decision", "cancelled");
            return ToolReply.Accepted();
        });
    }

    public static bool DependentsPaused(string root, string subTaskId)
    {
        foreach (var session in Sessions(root))
            if (Text(session, "subTaskId") == subTaskId && Text(session, "state") == "waiting-decision") return true;
        return false;
    }

    public static JsonArray PendingDecisions(string root)
    {
        var list = new JsonArray();
        foreach (var session in Sessions(root))
        {
            if (session["decision"] is not JsonObject decision || Text(decision, "status") != "pending") continue;
            list.Add((JsonNode)new JsonObject
            {
                ["token"] = Text(session, "token"),
                ["attemptId"] = Text(session, "attemptId"),
                ["subTaskId"] = Text(session, "subTaskId"),
                ["question"] = Text(decision, "question"),
                ["highlighted"] = true
            });
        }
        return list;
    }

    public static ToolReply Handoff(string root, Call call, string? notes, long nowMs)
    {
        var text = CheckBlock(notes, HandoffMax);
        if (text.Code != "ok") return ToolReply.Reject(text.Code);
        return Mutate(root, call, nowMs, "handoff", true, session =>
        {
            if (Text(session, "state") is "ended") return ToolReply.Reject("ended");
            if (Text(session, "state") != "active") return ToolReply.Reject("paused");
            if (session["handoff"] is JsonObject) return ToolReply.Reject("duplicate");
            session["handoff"] = new JsonObject
            {
                ["notes"] = text.Value,
                ["fromModel"] = Text(session, "model"),
                ["fromAttempt"] = Text(session, "attemptId"),
                ["subTaskId"] = Text(session, "subTaskId"),
                ["atMs"] = nowMs
            };
            return ToolReply.Accepted();
        });
    }

    public static string? SuccessorBrief(string root, string subTaskId, string model, string attemptId)
    {
        JsonObject? best = null;
        long bestAt = long.MinValue;
        foreach (var session in Sessions(root))
        {
            if (session["handoff"] is not JsonObject handoff) continue;
            if (Text(handoff, "subTaskId") != subTaskId || Text(handoff, "fromAttempt") == attemptId || Text(handoff, "fromModel") == model) continue;
            var at = Long(handoff, "atMs");
            if (best is not null && at <= bestAt) continue;
            best = handoff;
            bestAt = at;
        }
        return best is null ? null : Text(best, "notes");
    }

    public static ToolReply Finish(string root, Call call, JsonObject? body, long nowMs)
    {
        if (body is null) return ToolReply.Reject("malformed");
        if (body.ToJsonString().Length > JsonMax) return ToolReply.Reject("too-long");
        return Mutate(root, call, nowMs, "finish", true, session =>
        {
            if (Text(session, "state") is "ended") return ToolReply.Reject("ended");
            if (Text(session, "state") != "active") return ToolReply.Reject("paused");
            foreach (var key in new[] { "projectId", "worktree", "token", "store", "storeRoot", "recipient", "connectionId", "provider", "model", "cli" })
                if (body.ContainsKey(key)) return ToolReply.Reject("identity-mismatch");
            if (!SameIdentity(session, body)) return ToolReply.Reject("identity-mismatch");
            var errors = FieldErrors(body);
            var identity = new JsonObject
            {
                ["resultId"] = Text(session, "resultId"),
                ["attemptId"] = Text(session, "attemptId"),
                ["subTaskId"] = Text(session, "subTaskId"),
                ["megaTaskId"] = Text(session, "megaTaskId")
            };
            if (errors.Count == 0 && !WorkerResult.ValidateResult(body, identity)) errors.Add("result");
            if (errors.Count > 0)
            {
                var fields = new JsonArray();
                foreach (var name in errors) fields.Add((JsonNode)JsonValue.Create(name)!);
                return new ToolReply(false, "invalid", new JsonObject { ["fields"] = fields });
            }
            if (session["finish"] is JsonObject existing)
                return JsonNode.DeepEquals(existing, body) ? ToolReply.Accepted() : ToolReply.Reject("already-accepted");
            session["finish"] = body.DeepClone();
            session["finishWrites"] = 1;
            return ToolReply.Accepted();
        });
    }

    public static void RecordSetupFailure(string root, string token)
    {
        if (!ValidToken(token)) return;
        WithSession(root, token, null, session =>
        {
            if (session is null) return false;
            session["recovery"] = "mcp-setup-failed";
            Audit(root, Text(session, "attemptId"), "setup", "mcp-setup-failed");
            return true;
        });
    }

    public static Collection Collect(string root, string token, int exitCode, string folder, string? createdAt)
    {
        var session = Read(root, token);
        if (session?["finish"] is not JsonObject finish) return new Collection(false, "no-claim", null);
        if (Text(finish, "status") == "COMPLETE" && exitCode != 0) return new Collection(false, "process-exit", null);
        var copy = finish.DeepClone().AsObject();
        if (!StructuredFinal.Truthy(copy["createdAt"]) && createdAt is { Length: > 0 }) copy["createdAt"] = createdAt;
        copy["exitCode"] = exitCode;
        copy["evidencePath"] = SessionPath(root, token);
        var judged = WorkerResult.RejectMissingClaims(copy, folder);
        var completed = Text(judged, "status") == "COMPLETE";
        return new Collection(completed, completed ? "ok" : "false-claim", judged);
    }

    public static Collection CollectJson(string file, JsonObject identity, string exitFile, string folder)
    {
        var result = WorkerResult.ReadValidatedResult(file, identity, exitFile);
        if (result is null) return new Collection(false, "json-rejected", null);
        var judged = WorkerResult.RejectMissingClaims(result, folder);
        var completed = Text(judged, "status") == "COMPLETE";
        return new Collection(completed, completed ? "ok" : "false-claim", judged);
    }

    public static Collection CollectAttempt(string root, string token, int exitCode, string folder, string? createdAt, string? jsonFile, JsonObject identity, string? exitFile)
    {
        var mcp = Collect(root, token, exitCode, folder, createdAt);
        if (mcp.Reason != "no-claim" || jsonFile is null || exitFile is null) return mcp;
        return CollectJson(jsonFile, identity, exitFile, folder);
    }

    public static JsonObject? Read(string root, string token)
    {
        if (!ValidToken(token)) return null;
        var path = SessionPath(root, token);
        if (!File.Exists(path)) return null;
        return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
    }

    internal static string Dir(string root) => Path.Combine(Path.GetFullPath(root), "sessions");

    static string SessionPath(string root, string token) => Path.Combine(Dir(root), token + ".json");

    static bool ValidToken(string? token) => token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    static void RequireLabel(string? value, int max)
    {
        var line = CheckLine(value, max);
        if (line.Code != "ok") throw new ArgumentException(line.Code);
    }

    readonly record struct Line(string Code, string Value);

    static Line CheckLine(string? value, int max)
    {
        if (value is null) return new("malformed", "");
        if (value.Any(c => c < ' ' || c == '\u007f')) return new("unsupported-characters", "");
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return new("empty", "");
        if (trimmed.Length > max) return new("too-long", "");
        return new("ok", trimmed);
    }

    static Line CheckBlock(string? value, int max)
    {
        if (value is null) return new("malformed", "");
        if (value.Any(c => c != '\n' && c != '\t' && (c < ' ' || c == '\u007f'))) return new("unsupported-characters", "");
        if (value.Trim().Length == 0) return new("empty", "");
        if (value.Length > max) return new("too-long", "");
        return new("ok", value);
    }

    static bool SameIdentity(JsonObject session, JsonObject body)
    {
        foreach (var key in StructuredFinal.IdentityKeys)
            if (Text(body, key).Length == 0 || Text(body, key) != Text(session, key)) return false;
        return true;
    }

    static List<string> FieldErrors(JsonObject body)
    {
        var errors = new List<string>();
        if (Text(body, "status") is not ("COMPLETE" or "FAILED" or "BLOCKED")) errors.Add("status");
        Need(body, "summary", SummaryMax, false, true, errors);
        Need(body, "nextAction", NextActionMax, true, true, errors);
        Strings(body["filesChanged"], "filesChanged", FilesMax, PathItemMax, false, errors);
        Strings(body["blockers"], "blockers", BlockersMax, BlockerMax, false, errors);
        if (body["tests"] is not JsonArray tests || tests.Count > TestsMax) errors.Add("tests");
        else foreach (var item in tests)
        {
            if (item is not JsonObject test || Text(test, "status") is not ("PASSED" or "FAILED" or "NOT_RUN")) { errors.Add("tests"); break; }
            var before = errors.Count;
            Need(test, "command", CommandMax, false, false, errors);
            Need(test, "evidence", EvidenceMax, true, true, errors);
            if (errors.Count != before) break;
        }
        if (body.ContainsKey("sources")) Strings(body["sources"], "sources", SourcesMax, SourceMax, false, errors);
        if (body.ContainsKey("reportPath")) Need(body, "reportPath", ReportPathMax, false, false, errors);
        if (body.ContainsKey("recommendedNextAction")) Need(body, "recommendedNextAction", RecommendedMax, true, true, errors);
        if (body.ContainsKey("createdAt") && (Text(body, "createdAt") is not { Length: > 0 } date ||
            !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))) errors.Add("createdAt");
        return errors;
    }

    static void Need(JsonObject body, string key, int max, bool allowEmpty, bool block, List<string> errors)
    {
        if (body[key] is not JsonValue v || !v.TryGetValue<string>(out var text)) { errors.Add(key); return; }
        if (text.Any(c => block ? c != '\n' && c != '\t' && (c < ' ' || c == '\u007f') : c < ' ' || c == '\u007f')) { errors.Add(key); return; }
        if (!allowEmpty && text.Trim().Length == 0 || text.Length > max) errors.Add(key);
    }

    static void Strings(JsonNode? node, string key, int maxItems, int maxLen, bool block, List<string> errors)
    {
        if (node is not JsonArray array || array.Count > maxItems) { errors.Add(key); return; }
        foreach (var item in array)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length > maxLen || text.Trim().Length == 0) { errors.Add(key); return; }
            if (text.Any(c => block ? c != '\n' && c != '\t' && (c < ' ' || c == '\u007f') : c < ' ' || c == '\u007f')) { errors.Add(key); return; }
        }
    }

    static ToolReply Mutate(string root, Call call, long nowMs, string tool, bool record, Func<JsonObject, ToolReply> action)
    {
        if (!ValidToken(call.Token)) return ToolReply.Reject("unknown-session");
        return WithSession(root, call.Token, null, session =>
        {
            if (session is null) return ToolReply.Reject("unknown-session");
            if (Text(session, "connectionId") != call.ConnectionId) return Reject(root, session, tool, "stale-connection");
            session["heartbeatMs"] = nowMs;
            var reply = action(session);
            if (record && reply.Code != "pending") Audit(root, Text(session, "attemptId"), tool, reply.Code);
            return reply;
        });
    }

    static ToolReply Reject(string root, JsonObject session, string tool, string code)
    {
        Audit(root, Text(session, "attemptId"), tool, code);
        return ToolReply.Reject(code);
    }

    static T WithSession<T>(string root, string token, JsonObject? created, Func<JsonObject?, T> action)
    {
        Directory.CreateDirectory(Dir(root));
        var lockPath = Path.Combine(Dir(root), token + ".lock");
        using var stream = OpenLock(lockPath);
        JsonObject? session = created;
        var path = SessionPath(root, token);
        if (created is null)
        {
            if (!File.Exists(path)) return action(null);
            session = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        var result = action(session);
        if (session is not null) Save(path, session);
        return result;
    }

    static FileStream OpenLock(string lockPath, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        for (var delay = 5; ; delay = Math.Min(100, delay * 2))
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (AtomicRename.IsRetryable(e) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(delay);
            }
        }
    }

    static void Save(string path, JsonObject session)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, session.ToJsonString(), new UTF8Encoding(false));
        AtomicRename.RenameWithRetry(temp, path);
    }

    static void Audit(string root, string attemptId, string tool, string code)
    {
        var line = attemptId + "\t" + tool + "\t" + code + "\n";
        var path = Path.Combine(Path.GetFullPath(root), "audit.log");
        var bytes = Encoding.UTF8.GetBytes(line);
        var deadline = Environment.TickCount64 + 2000;
        for (var delay = 5; ; delay = Math.Min(100, delay * 2))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
                return;
            }
            catch (Exception e) when (AtomicRename.IsRetryable(e) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(delay);
            }
        }
    }

    static IEnumerable<JsonObject> Sessions(string root)
    {
        var dir = Dir(root);
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            JsonObject? session;
            try { session = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { continue; }
            if (session is not null) yield return session;
        }
    }

    static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static long Long(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var n)) return n;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d is >= long.MinValue and <= long.MaxValue) return (long)d;
        return 0;
    }
}
