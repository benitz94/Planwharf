// Port of engine/activity-sessions.cjs.
using System.Text;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class ActivitySessions
{
    public const int SessionLimit = 24;
    public const int EventLimit = 50;
    public const int TailBytes = 32768;

    private static string? Text(string? value) =>
        !string.IsNullOrEmpty(value) ? ActivityEvents.Sanitize(value, 160) : null;

    private static string? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, out _) ? value : null;

    public static string ReadRecordedTail(string? file, string? root)
    {
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(root)) return "";
        try
        {
            var baseDir = Path.GetFullPath(root);
            var target = Path.GetFullPath(file);
            var relative = Path.GetRelativePath(baseDir, target);
            if (string.IsNullOrEmpty(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
                return "";

            if (!File.Exists(target)) return "";
            var fi = new FileInfo(target);
            var size = fi.Length;
            var length = (int)Math.Min(size, TailBytes);
            var start = size - length;

            using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length];
            var bytesRead = fs.Read(buffer, 0, length);
            var tail = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (start > 0)
            {
                var idx = tail.IndexOf('\n');
                tail = idx >= 0 ? tail[(idx + 1)..] : "";
            }
            return tail;
        }
        catch
        {
            return "";
        }
    }

    public static JsonObject? AssignmentSession(JsonObject options)
    {
        var projectId = options["projectId"]?.GetValue<string>();
        var row = options["row"] as JsonObject ?? new JsonObject();
        var part = options["part"] as JsonObject ?? new JsonObject();
        var execution = options["execution"] as JsonObject ?? new JsonObject();
        var tail = options["tail"]?.GetValue<string>() ?? "";
        bool historical = options["historical"]?.GetValue<bool>() == true;
        int index = options["index"]?.GetValue<int>() ?? 0;

        if (string.IsNullOrEmpty(projectId)) return null;
        var partProjectId = part["projectId"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(partProjectId) && partProjectId != projectId) return null;

        var megaTaskId = part["megaTaskId"]?.GetValue<string>() ?? row["megaTaskId"]?.GetValue<string>();
        var megaTasks = (execution["megaTasks"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var mega = megaTasks.FirstOrDefault(m => m["id"]?.GetValue<string>() == megaTaskId && m["projectId"]?.GetValue<string>() == projectId);
        if (!string.IsNullOrEmpty(megaTaskId) && mega == null) return null;

        var subTaskId = part["subTaskId"]?.GetValue<string>();
        var subTasks = (execution["subTasks"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var sub = subTasks.FirstOrDefault(s => s["id"]?.GetValue<string>() == subTaskId && s["megaTaskId"]?.GetValue<string>() == megaTaskId);
        if (!string.IsNullOrEmpty(subTaskId) && sub == null) return null;

        var attemptId = part["attemptId"]?.GetValue<string>();
        var results = (execution["results"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var candidate = (mega != null && sub != null && !string.IsNullOrEmpty(attemptId))
            ? results.FirstOrDefault(r => r["attemptId"]?.GetValue<string>() == attemptId &&
                                         r["megaTaskId"]?.GetValue<string>() == mega["id"]?.GetValue<string>() &&
                                         r["subTaskId"]?.GetValue<string>() == sub["id"]?.GetValue<string>())
            : null;

        var partProv = part["providerId"]?.GetValue<string>() ?? part["provider"]?.GetValue<string>();
        var partCli = part["cli"]?.GetValue<string>();
        var partModelId = part["modelId"]?.GetValue<string>();

        JsonObject? result = null;
        if (candidate != null)
        {
            var candProj = candidate["projectId"]?.GetValue<string>();
            var candProv = candidate["provider"]?.GetValue<string>();
            var candCli = candidate["cli"]?.GetValue<string>();
            var candAssigned = candidate["assignedModelId"]?.GetValue<string>();

            bool matchProj = string.IsNullOrEmpty(candProj) || candProj == projectId;
            bool matchProv = string.IsNullOrEmpty(partProv) || partProv == candProv;
            bool matchCli = string.IsNullOrEmpty(partCli) || partCli == candCli;
            bool matchModel = string.IsNullOrEmpty(partModelId) || string.IsNullOrEmpty(candAssigned) || partModelId == candAssigned;

            if (matchProj && matchProv && matchCli && matchModel)
                result = candidate;
        }

        var provider = partProv ?? result?["provider"]?.GetValue<string>();
        var model = partCli ?? result?["cli"]?.GetValue<string>();
        var adapter = part["adapter"]?.GetValue<string>() ?? (provider is "codex" or "grok" or "antigravity" or "claude" ? provider : null);

        bool validated = false;
        if (result != null && !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(model) &&
            result["provider"]?.GetValue<string>() == provider &&
            result["cli"]?.GetValue<string>() == model &&
            result["evidenceKind"]?.GetValue<string>() != "TECHNICAL" &&
            sub != null)
        {
            var identity = new JsonObject
            {
                ["resultId"] = "R-" + attemptId,
                ["attemptId"] = attemptId,
                ["megaTaskId"] = megaTaskId,
                ["subTaskId"] = sub["id"]?.GetValue<string>()
            };
            var exitCode = result["exitCode"] is JsonNode ecNode ? ecNode.GetValue<int>() : 0;
            validated = WorkerResult.ValidateResult(result, identity) && (result["status"]?.GetValue<string>() != "COMPLETE" || exitCode == 0);
        }

        var role = sub != null ? ModelRoles.AssignmentRole(sub) : (part["role"]?.GetValue<string>() is "worker" or "reviewer" ? part["role"]?.GetValue<string>() : null);
        var eventsCtx = new JsonObject
        {
            ["provider"] = provider ?? "unknown",
            ["adapter"] = adapter ?? "unknown",
            ["model"] = model ?? ""
        };

        var allEvents = Activity.ActivityEventsList(tail, eventsCtx);
        var events = allEvents.Count > EventLimit ? allEvents.Skip(allEvents.Count - EventLimit).ToList() : allEvents.ToList();

        bool alive = !historical && part["observedAlive"]?.GetValue<bool>() == true;
        var pidNode = part["pid"];
        int pid = (pidNode is JsonNode pNode && pNode.GetValue<int>() is int pInt) ? pInt : 0;
        string processState = historical ? "recorded" : (alive ? "running" : (part["observedAlive"]?.GetValue<bool>() == false && pid > 0 ? "ended" : "unknown"));

        string status = part["stoppedByUser"]?.GetValue<bool>() == true ? "stopped" :
                        alive ? (role == "reviewer" ? "reviewing" : "running") :
                        (validated && result?["status"]?.GetValue<string>() == "COMPLETE") ? "completed" :
                        (validated && result?["status"]?.GetValue<string>() == "FAILED") ? "failed" :
                        (validated && result?["status"]?.GetValue<string>() == "BLOCKED") ? "waiting" :
                        row["status"]?.GetValue<string>() == "checkpointed" ? "checkpointed" :
                        sub?["status"]?.GetValue<string>() == "PENDING" ? "pending" :
                        sub?["status"]?.GetValue<string>() == "BLOCKED" ? "waiting" :
                        (result?["status"]?.GetValue<string>() == "FAILED" || sub?["status"]?.GetValue<string>() == "FAILED" || row["status"]?.GetValue<string>() == "failed") ? "failed" : "unknown";

        var warnings = new List<string>();
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) warnings.Add("Exact provider/model identity was not retained for this assignment.");
        if (string.IsNullOrEmpty(attemptId)) warnings.Add("Exact attempt identity was not retained for this assignment.");
        if (string.IsNullOrEmpty(role)) warnings.Add("Assignment role was not retained.");
        if (events.Count == 0) warnings.Add("No readable assignment output is retained in this view.");
        if (!validated) warnings.Add("No matching backend-validated structured result is recorded.");
        if (candidate != null && result == null) warnings.Add("Recorded result identity does not match this assignment.");
        if (string.IsNullOrEmpty(adapter)) warnings.Add("Adapter identity was not retained; detailed protocol capability is unknown.");

        var idComposite = $"{Uri.EscapeDataString(projectId)}:{Uri.EscapeDataString(megaTaskId ?? "legacy")}:{Uri.EscapeDataString(attemptId ?? "unrecorded-" + index)}";

        var technicalEv = ActivityEvidence.TechnicalEvidence(tail, new JsonObject
        {
            ["provider"] = provider ?? "unknown",
            ["adapter"] = adapter ?? "unknown"
        });

        var latestConfirmed = events.LastOrDefault(e => e["provenance"]?.GetValue<string>() == "observed");
        var reportedOutcomes = events.Where(e => e["provenance"]?.GetValue<string>() == "reported" &&
            e["classification"]?.GetValue<string>() is "test" or "build" or "warning").TakeLast(5).ToList();

        var sessionObj = new JsonObject
        {
            ["id"] = idComposite,
            ["projectId"] = Text(projectId),
            ["jobId"] = Text(mega?["taskId"]?.GetValue<string>() ?? row["jobId"]?.GetValue<string>()),
            ["megaTaskId"] = Text(megaTaskId),
            ["subTaskId"] = Text(part["subTaskId"]?.GetValue<string>()),
            ["attemptId"] = Text(attemptId),
            ["recordedSessionId"] = Text(part["sessionId"]?.GetValue<string>() ?? row["sessionId"]?.GetValue<string>() ?? row["authorizedSessionId"]?.GetValue<string>()),
            ["role"] = role,
            ["provider"] = Text(provider),
            ["adapter"] = Text(adapter),
            ["model"] = Text(model),
            ["modelId"] = Text(part["modelId"]?.GetValue<string>() ?? result?["assignedModelId"]?.GetValue<string>()),
            ["startedAt"] = Timestamp(part["startedAt"]?.GetValue<string>() ?? result?["startedAt"]?.GetValue<string>()),
            ["completedAt"] = (validated && sub?["resultId"]?.GetValue<string>() == result?["resultId"]?.GetValue<string>()) ? Timestamp(sub?["completedAt"]?.GetValue<string>()) : null,
            ["reportedResultAt"] = Timestamp(result?["createdAt"]?.GetValue<string>()),
            ["processState"] = processState,
            ["state"] = status,
            ["historical"] = historical,
            ["validatedResultStatus"] = validated ? result?["status"]?.GetValue<string>() : null,
            ["recordedResultStatus"] = Text(result?["status"]?.GetValue<string>()),
            ["exitCode"] = result?["exitCode"] is JsonNode exitNode ? exitNode.GetValue<int>() : null,
            ["capabilities"] = Activity.ActivityCapabilities(new JsonObject { ["adapter"] = adapter ?? "unknown" }),
            ["latestActivity"] = events.Count > 0 ? (JsonNode)events[^1].DeepClone() : null,
            ["latestConfirmedActivity"] = latestConfirmed != null ? (JsonNode)latestConfirmed.DeepClone() : null,
            ["reportedOutcomes"] = new JsonArray(reportedOutcomes.Select(o => (JsonNode)o.DeepClone()).ToArray()),
            ["technicalEvidence"] = technicalEv,
            ["events"] = new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()),
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray())
        };

        return sessionObj;
    }

    public static IReadOnlyList<JsonObject> ProjectActivitySessions(JsonObject options)
    {
        var projectId = options["projectId"]?.GetValue<string>() ?? "";
        var current = (options["current"] as JsonArray)?.OfType<JsonObject>().Take(SessionLimit).ToList() ?? new List<JsonObject>();
        var sessions = new List<JsonObject>(current);
        var knownAttempts = sessions.Select(s => s["attemptId"]?.GetValue<string>()).Where(a => !string.IsNullOrEmpty(a)).ToHashSet(StringComparer.Ordinal);

        void Add(JsonObject? s)
        {
            if (s == null || sessions.Count >= SessionLimit) return;
            var aId = s["attemptId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(aId) && knownAttempts.Contains(aId)) return;
            sessions.Add(s);
            if (!string.IsNullOrEmpty(aId)) knownAttempts.Add(aId);
        }

        var history = (options["history"] as JsonArray)?.OfType<JsonObject>().Where(r => r["id"]?.GetValue<string>() == projectId).TakeLast(SessionLimit).Reverse().ToList() ?? new List<JsonObject>();
        var execution = options["execution"] as JsonObject ?? new JsonObject();

        foreach (var record in history)
        {
            var dispatch = (record["dispatch"] as JsonArray)?.OfType<JsonObject>().Take(SessionLimit).ToList() ?? new List<JsonObject>();
            for (int i = 0; i < dispatch.Count; i++)
            {
                if (sessions.Count >= SessionLimit) break;
                var part = dispatch[i];
                var sessionOpt = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["row"] = record.DeepClone(),
                    ["part"] = part.DeepClone(),
                    ["execution"] = execution.DeepClone(),
                    ["historical"] = true,
                    ["index"] = i,
                    ["tail"] = ""
                };
                Add(AssignmentSession(sessionOpt));
            }
        }

        return sessions;
    }

    public static JsonObject PagedProjectActivitySessions(JsonObject options)
    {
        var projectId = options["projectId"]?.GetValue<string>() ?? "";
        var row = options["row"] as JsonObject ?? new JsonObject();
        var currentParts = (options["currentParts"] as JsonArray ?? row["dispatch"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var history = (options["history"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var execution = options["execution"] as JsonObject ?? new JsonObject();

        int limit = options["limit"]?.GetValue<int>() ?? SessionLimit;
        limit = Math.Min(SessionLimit, Math.Max(1, limit));
        int offset = options["offset"]?.GetValue<int>() ?? 0;
        offset = Math.Max(0, offset);

        var metadata = new List<(JsonObject part, JsonObject record, bool historical, int index)>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        var megas = (execution["megaTasks"] as JsonArray)?.OfType<JsonObject>().Where(m => m["projectId"]?.GetValue<string>() == projectId).ToDictionary(m => m["id"]?.GetValue<string>() ?? "", m => m, StringComparer.Ordinal) ?? new Dictionary<string, JsonObject>();
        var subs = (execution["subTasks"] as JsonArray)?.OfType<JsonObject>().ToDictionary(s => s["id"]?.GetValue<string>() ?? "", s => s, StringComparer.Ordinal) ?? new Dictionary<string, JsonObject>();

        void Add(JsonObject part, JsonObject record, bool historical, int index)
        {
            if (string.IsNullOrEmpty(projectId)) return;
            var pProj = part["projectId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(pProj) && pProj != projectId) return;

            var megaTaskId = part["megaTaskId"]?.GetValue<string>() ?? record["megaTaskId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(megaTaskId) && !megas.ContainsKey(megaTaskId)) return;

            var subTaskId = part["subTaskId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(subTaskId) && subs.TryGetValue(subTaskId, out var sub) && sub["megaTaskId"]?.GetValue<string>() != megaTaskId) return;

            var attemptId = part["attemptId"]?.GetValue<string>();
            var key = !string.IsNullOrEmpty(attemptId) ? $"{megaTaskId ?? "null"}:{subTaskId ?? "null"}:{attemptId}" : "legacy-" + index;
            if (known.Contains(key)) return;
            known.Add(key);
            metadata.Add((part, record, historical, index));
        }

        int idx = 0;
        var rowProj = row["projectId"]?.GetValue<string>();
        var rowId = row["id"]?.GetValue<string>();
        if ((string.IsNullOrEmpty(rowProj) || rowProj == projectId) && (string.IsNullOrEmpty(rowId) || rowId == projectId))
        {
            foreach (var part in currentParts) Add(part, row, false, idx++);
        }

        foreach (var record in history.Where(r => r["id"]?.GetValue<string>() == projectId).Reverse())
        {
            if (record["dispatch"] is JsonArray dArr)
            {
                foreach (var part in dArr.OfType<JsonObject>()) Add(part, record, true, idx++);
            }
        }

        if (execution["results"] is JsonArray resArr)
        {
            foreach (var result in resArr.OfType<JsonObject>().Where(r => megas.ContainsKey(r["megaTaskId"]?.GetValue<string>() ?? "") && (string.IsNullOrEmpty(r["projectId"]?.GetValue<string>()) || r["projectId"]?.GetValue<string>() == projectId)).Reverse())
            {
                var copy = (JsonObject)result.DeepClone();
                copy["modelId"] = result["assignedModelId"]?.DeepClone();
                copy.Remove("logPath");
                Add(copy, new JsonObject(), true, idx++);
            }
        }

        var pagedSlice = metadata.Skip(offset).Take(limit).ToList();
        var sessions = new JsonArray();
        foreach (var item in pagedSlice)
        {
            var logPath = item.part["logPath"]?.GetValue<string>();
            var sessionOpt = new JsonObject
            {
                ["projectId"] = projectId,
                ["row"] = item.record.DeepClone(),
                ["part"] = item.part.DeepClone(),
                ["historical"] = item.historical,
                ["index"] = item.index,
                ["execution"] = execution.DeepClone(),
                ["tail"] = ""
            };
            var sess = AssignmentSession(sessionOpt);
            if (sess != null) sessions.Add((JsonNode)sess);
        }

        return new JsonObject
        {
            ["sessions"] = sessions,
            ["total"] = metadata.Count,
            ["offset"] = offset,
            ["limit"] = limit
        };
    }
}
