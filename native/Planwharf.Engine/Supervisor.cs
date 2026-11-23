// Port of engine/supervisor.cjs.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public sealed class Supervisor
{
    private readonly string? _file;
    private readonly string? _dir;
    private readonly Func<JsonObject, Task<JsonObject>>? _review;
    private readonly Func<string> _now;
    private readonly JsonObject _store;
    private bool _busy;

    public Supervisor(string? dir, Func<JsonObject, Task<JsonObject>>? review = null, Func<string>? now = null)
    {
        _dir = dir;
        _file = !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "supervisor.json") : null;
        _review = review;
        _now = now ?? (() => DateTime.UtcNow.ToString("o"));

        _store = new JsonObject
        {
            ["schema"] = 2,
            ["name"] = "Supervisor",
            ["configured"] = false,
            ["modelId"] = "",
            ["autoReview"] = false,
            ["alerts"] = new JsonArray(),
            ["turns"] = new JsonArray(),
            ["autoCalls"] = new JsonArray()
        };

        bool migrated = false;
        if (!string.IsNullOrEmpty(_file) && File.Exists(_file))
        {
            try
            {
                var text = File.ReadAllText(_file);
                var saved = JsonNode.Parse(text) as JsonObject;
                if (saved != null)
                {
                    var schema = saved["schema"]?.GetValue<int>() ?? 0;
                    if (schema is not 1 and not 2 || saved["alerts"] is not JsonArray || saved["turns"] is not JsonArray)
                        throw new InvalidOperationException("Invalid Supervisor storage");

                    foreach (var kvp in saved)
                        _store[kvp.Key] = kvp.Value != null ? (JsonNode)kvp.Value.DeepClone() : null;

                    if (schema == 1)
                    {
                        _store["schema"] = 2;
                        _store["legacyModelId"] = _store["modelId"]?.ToString() ?? "";
                        _store["modelId"] = "";
                        _store["autoReview"] = false;
                        migrated = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
            }
        }

        bool interrupted = false;
        var turns = _store["turns"] as JsonArray;
        if (turns != null)
        {
            foreach (var tNode in turns)
            {
                if (tNode is JsonObject turn && turn["status"]?.ToString() == "running")
                {
                    turn["status"] = "failed";
                    turn["answer"] = "This answer was interrupted by a backend restart. Ask again for current evidence.";
                    turn["finishedAt"] = _now();
                    interrupted = true;
                }
            }
        }

        if (interrupted || migrated) Save();
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_file) || string.IsNullOrEmpty(_dir)) return;
        Directory.CreateDirectory(_dir);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, _store.ToJsonString());
        File.Move(temp, _file, overwrite: true);
    }

    public static string Label(string? input)
    {
        var trimmed = (input ?? "").Trim();
        return string.IsNullOrEmpty(trimmed) ? "Supervisor" : string.Concat(trimmed.Take(80));
    }

    public static string? AdvisoryError(JsonObject? model, IReadOnlyList<JsonObject>? plans = null)
    {
        if (model == null || model["roles"]?["supervisor"]?.GetValue<bool>() != true)
            return "Supervisor permission is required; Reviewer permission does not authorize advisory calls.";
        if (!ModelRoles.SupportsSupervisor(model))
            return "No verified no-tool Supervisor transport for this adapter. No fallback was used.";
        if (model["enabled"]?.GetValue<bool>() == false || model["available"]?.GetValue<bool>() == false || model["providerEnabled"]?.GetValue<bool>() == false)
            return "The selected Supervisor model or provider is unavailable. No fallback was used.";
        if (!model.ContainsKey("providerCatalog"))
        {
            var rem = model["remaining"];
            var c = model["cost"];
            if (rem is JsonValue remV && c is JsonValue cV &&
                double.TryParse(remV.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var remD) &&
                double.TryParse(cV.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cD) &&
                remD < cD)
            {
                return "The selected Supervisor has no remaining model usage budget. No fallback was used.";
            }
        }
        var plansArr = new JsonArray();
        if (plans != null) foreach (var p in plans) plansArr.Add((JsonNode)p.DeepClone());
        var status = ModelPolicy.UsagePolicy(model, plansArr);
        if (status["blocked"]?.GetValue<bool>() == true || model["usagePolicy"]?["blocked"]?.GetValue<bool>() == true)
            return "The selected Supervisor is blocked by its quota policy. No fallback was used.";
        if (plans != null)
        {
            var poolId = model["quotaPoolId"]?.ToString() ?? model["plan"]?.ToString();
            if (plans.Any(pool => pool["id"]?.ToString() == poolId && pool["readable"]?.GetValue<bool>() == true && (pool["exhausted"]?.GetValue<bool>() == true || pool["leftPct"]?.GetValue<double>() == 0)))
                return "The selected Supervisor is blocked by its quota policy. No fallback was used.";
        }
        return null;
    }

    public static JsonObject ProjectFacts(JsonObject snapshot, string projectId, long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var projects = snapshot["projects"] as JsonArray;
        var row = projects?.FirstOrDefault(p => p?["id"]?.ToString() == projectId) as JsonObject;
        if (row == null) throw new InvalidOperationException("Select an existing project.");

        var subs = (snapshot["execution"]?["subTasks"] as JsonArray) ?? (row["subTasks"] as JsonArray) ?? new JsonArray();
        var results = (snapshot["execution"]?["results"] as JsonArray) ?? new JsonArray();
        var dispatch = (row["dispatch"] as JsonArray) ?? new JsonArray();
        var megaTaskId = row["megaTaskId"]?.ToString();

        var workers = new JsonArray();
        foreach (var pNode in dispatch)
        {
            if (pNode is not JsonObject part) continue;
            var subTaskId = part["subTaskId"]?.ToString();
            var sub = subs.FirstOrDefault(s => s?["id"]?.ToString() == subTaskId) as JsonObject;
            var partMega = sub?["megaTaskId"]?.ToString() ?? part["megaTaskId"]?.ToString() ?? megaTaskId;
            if (!string.IsNullOrEmpty(megaTaskId) && partMega != megaTaskId) continue;

            var attemptId = part["attemptId"]?.ToString();
            var result = results.FirstOrDefault(r => r?["attemptId"]?.ToString() == attemptId) as JsonObject;
            var modelId = part["modelId"]?.ToString();
            var models = snapshot["models"] as JsonArray;
            var model = models?.FirstOrDefault(m => m?["id"]?.ToString() == modelId) as JsonObject;

            var role = (sub?["type"]?.ToString() == "AUDIT" || part["type"]?.ToString() == "AUDIT") ? "reviewer" : "worker";
            var alive = part["alive"]?.GetValue<bool>() == true;
            var rowStatus = row["status"]?.ToString();

            string status;
            if (alive) status = "running";
            else if (result?["status"] != null) status = result["status"]!.ToString();
            else if (part["recordedSubTaskStatus"] != null) status = part["recordedSubTaskStatus"]!.ToString();
            else status = rowStatus == "checkpointed" ? "paused" : "not running";

            string summary;
            if (result?["summary"] != null) summary = result["summary"]!.ToString();
            else if (alive)
            {
                var taskName = sub?["title"]?.ToString() ?? part["slice"]?.ToString() ?? "the assignment";
                summary = $"{(role == "reviewer" ? "Checking" : "Working on")} {taskName}.";
            }
            else summary = "No active process; check recorded evidence.";

            var act = part["activity"]?.ToString() ?? "";
            if (act.Length > 700) act = act[..700];

            workers.Add((JsonNode)new JsonObject
            {
                ["id"] = attemptId ?? part["pid"]?.ToString() ?? modelId ?? "",
                ["subTaskId"] = subTaskId,
                ["role"] = role,
                ["modelId"] = modelId,
                ["provider"] = part["provider"]?.ToString() ?? model?["providerId"]?.ToString() ?? model?["provider"]?.ToString(),
                ["model"] = part["cli"]?.ToString() ?? model?["cli"]?.ToString(),
                ["task"] = sub?["title"]?.ToString() ?? part["slice"]?.ToString() ?? row["jobTitle"]?.ToString() ?? "Assigned work",
                ["alive"] = alive,
                ["status"] = status,
                ["summary"] = summary,
                ["activity"] = act,
                ["resultId"] = result?["resultId"]?.ToString()
            });
        }

        foreach (var sNode in subs)
        {
            if (sNode is not JsonObject sub) continue;
            if (sub["megaTaskId"]?.ToString() == megaTaskId && sub["type"]?.ToString() == "AUDIT")
            {
                var sId = sub["id"]?.ToString();
                if (!workers.Any(w => w?["subTaskId"]?.ToString() == sId))
                {
                    var assignedModelId = sub["assignedModelId"]?.ToString();
                    var models = snapshot["models"] as JsonArray;
                    var model = models?.FirstOrDefault(m => m?["id"]?.ToString() == assignedModelId) as JsonObject;
                    var subStatus = sub["status"]?.ToString();
                    workers.Add((JsonNode)new JsonObject
                    {
                        ["id"] = sId,
                        ["subTaskId"] = sId,
                        ["role"] = "reviewer",
                        ["modelId"] = assignedModelId,
                        ["provider"] = model?["providerId"]?.ToString() ?? model?["provider"]?.ToString(),
                        ["model"] = model?["cli"]?.ToString(),
                        ["task"] = sub["title"]?.ToString(),
                        ["status"] = subStatus,
                        ["alive"] = false,
                        ["summary"] = subStatus == "PENDING" ? "Waiting for approval or prerequisite work." : "See the recorded review result."
                    });
                }
            }
        }

        var activeCount = workers.Count(w => w?["alive"]?.GetValue<bool>() == true);
        if (activeCount == 0 && row["workerAlive"]?.GetValue<bool>() == true) activeCount = 1;

        var alerts = new JsonArray();
        void AddAlert(string kind, string severity, string title, string message, JsonArray evidence)
        {
            var rawKey = $"{projectId}|{megaTaskId ?? row["jobId"]?.ToString()}|{kind}|{evidence.ToJsonString()}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant()[..24];
            var actions = new JsonArray { (JsonNode)"inspect", (JsonNode)"ask" };
            if (activeCount > 0 && !string.IsNullOrEmpty(megaTaskId) && row["status"]?.ToString() == "watching")
            {
                actions.Add((JsonNode)"pause");
                actions.Add((JsonNode)"stop");
            }
            actions.Add((JsonNode)"ignore");

            alerts.Add((JsonNode)new JsonObject
            {
                ["key"] = key,
                ["projectId"] = projectId,
                ["megaTaskId"] = megaTaskId,
                ["kind"] = kind,
                ["severity"] = severity,
                ["title"] = title,
                ["message"] = message,
                ["evidence"] = (JsonNode)evidence.DeepClone(),
                ["actions"] = actions
            });
        }

        if (row["status"]?.ToString() == "complete" && activeCount > 0)
        {
            var ev = new JsonArray();
            foreach (var w in workers)
                if (w?["alive"]?.GetValue<bool>() == true) ev.Add((JsonNode)w["id"]!.ToString());
            AddAlert("conflicting-completion", "critical", "Completion conflicts with active work", "The backend reports completion but workers are still active. Do not rely on this completion yet.", ev);
        }

        var st = row["status"]?.ToString();
        if (st == "failed" || (st != null && st.StartsWith("blocked_")))
        {
            var ev = new JsonArray { (JsonNode)(st ?? ""), (JsonNode)(row["attemptId"]?.ToString() ?? ""), (JsonNode)(megaTaskId ?? "") };
            AddAlert("execution-blocked", "warning", "Work needs attention", row["next"]?.ToString() ?? row["waitingFor"]?.ToString() ?? "The execution is blocked or failed. Review its evidence before resuming.", ev);
        }

        var relevantResults = results.Where(r => r?["megaTaskId"]?.ToString() == megaTaskId).ToList();
        foreach (var rNode in relevantResults)
        {
            if (rNode is not JsonObject r) continue;
            var rStatus = r["status"]?.ToString();
            var modelChange = r["modelChange"]?.GetValue<bool>() == true;
            if ((rStatus == "FAILED" || rStatus == "BLOCKED") && !modelChange)
            {
                var ev = new JsonArray { (JsonNode)(r["resultId"]?.ToString() ?? ""), (JsonNode)(r["subTaskId"]?.ToString() ?? ""), (JsonNode)(rStatus ?? "") };
                AddAlert("result-" + r["resultId"]?.ToString(), "warning", "An assignment did not finish successfully", r["summary"]?.ToString() ?? "A worker or reviewer reported a blocker.", ev);
            }
        }

        foreach (var wNode in workers)
        {
            if (wNode is not JsonObject w) continue;
            var alive = w["alive"]?.GetValue<bool>() == true;
            var hasResult = !string.IsNullOrEmpty(w["resultId"]?.ToString());
            var wStatus = w["status"]?.ToString();
            if (!alive && !hasResult && wStatus == "RUNNING")
            {
                var ev = new JsonArray { (JsonNode)(w["id"]?.ToString() ?? ""), (JsonNode)(w["subTaskId"]?.ToString() ?? "") };
                AddAlert("missing-result-" + w["id"]?.ToString(), "warning", "Worker stopped without a validated result", $"{w["task"]} has no live process and no validated result.", ev);
            }
        }

        var megaTasks = snapshot["execution"]?["megaTasks"] as JsonArray;
        var projectMegas = new HashSet<string>(megaTasks?.Where(m => m?["projectId"]?.ToString() == projectId).Select(m => m?["id"]?.ToString() ?? "") ?? Enumerable.Empty<string>());
        var recent = results.Where(r => projectMegas.Contains(r?["megaTaskId"]?.ToString() ?? "")).TakeLast(3).ToList();
        if (recent.Count == 3 && recent.All(r => r?["status"]?.ToString() == "FAILED" || r?["status"]?.ToString() == "BLOCKED"))
        {
            var ev = new JsonArray();
            foreach (var r in recent) ev.Add((JsonNode)(r?["resultId"]?.ToString() ?? ""));
            AddAlert("repeated-failures", "warning", "Three consecutive unsuccessful results", "The recent attempts are not making verified progress. Review the approach before retrying.", ev);
        }

        if (activeCount > 0 && row["progressAt"] != null && DateTimeOffset.TryParse(row["progressAt"]!.ToString(), out var progressDto))
        {
            var progressMs = progressDto.ToUnixTimeMilliseconds();
            if (now - progressMs > 10 * 60 * 1000)
            {
                var ev = new JsonArray { (JsonNode)row["progressAt"]!.ToString() };
                foreach (var w in workers)
                    if (w?["alive"]?.GetValue<bool>() == true) ev.Add((JsonNode)w["id"]!.ToString());
                AddAlert("quiet-execution", "warning", "No recent progress recorded", "A process is still alive, but no progress has been recorded for over ten minutes. This is a signal to inspect, not proof of failure.", ev);
            }
        }

        var rowName = row["name"]?.ToString() ?? "";
        string summaryStr;
        if (activeCount > 0)
        {
            summaryStr = row["megaTask"]?["pauseRequested"]?.GetValue<bool>() == true
                ? $"{rowName}: finishing active work before pausing."
                : $"{rowName}: {activeCount} active assignment{(activeCount == 1 ? "" : "s")}.";
        }
        else if (st == "complete") summaryStr = $"{rowName}: the backend recorded completion; no workers are active.";
        else if (st == "checkpointed") summaryStr = $"{rowName}: paused with a saved checkpoint.";
        else if (st == "needs_approval") summaryStr = $"{rowName}: a plan awaits your approval; workers have not started.";
        else if (st == "failed" || st == "blocked_no_worker" || st == "blocked_no_folder") summaryStr = $"{rowName}: execution needs attention.";
        else summaryStr = $"{rowName}: no workers are active. {row["next"]?.ToString() ?? ""}".Trim();

        var allTimeline = snapshot["execution"]?["timeline"] as JsonArray;
        var projTimeline = allTimeline?.Where(e => e?["projectId"]?.ToString() == projectId).TakeLast(12).Select(e => new JsonObject
        {
            ["id"] = e?["id"]?.ToString(),
            ["summary"] = e?["summary"]?.ToString(),
            ["at"] = e?["createdAt"]?.ToString()
        }).ToList() ?? new List<JsonObject>();
        var timelineArr = new JsonArray();
        foreach (var t in projTimeline) timelineArr.Add((JsonNode)t);

        var resultObj = new JsonObject
        {
            ["projectId"] = projectId,
            ["name"] = rowName,
            ["status"] = st
        };
        if (row.ContainsKey("megaTaskId"))
            resultObj["megaTaskId"] = row["megaTaskId"]?.ToString();
        resultObj["summary"] = summaryStr;
        resultObj["next"] = row["next"]?.ToString() ?? "";
        resultObj["active"] = activeCount;
        resultObj["workers"] = workers;
        resultObj["alerts"] = alerts;
        resultObj["timeline"] = timelineArr;
        resultObj["canPause"] = activeCount > 0 && !string.IsNullOrEmpty(megaTaskId) && st == "watching";
        resultObj["checkpointId"] = row["checkpointId"]?.ToString();

        return resultObj;
    }

    public JsonObject Observe(JsonObject snapshot)
    {
        bool changed = false;
        var activeKeys = new HashSet<string>();
        var added = new List<JsonObject>();
        var nowVal = _now();
        var nowParsed = DateTimeOffset.TryParse(nowVal, out var dto) ? dto.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var projects = snapshot["projects"] as JsonArray;
        var storeAlerts = _store["alerts"] as JsonArray ?? new JsonArray();
        _store["alerts"] = storeAlerts;

        if (projects != null)
        {
            foreach (var pNode in projects)
            {
                if (pNode is not JsonObject proj) continue;
                var projId = proj["id"]?.ToString() ?? "";
                var facts = ProjectFacts(snapshot, projId, nowParsed);
                if (facts["alerts"] is JsonArray detAlerts)
                {
                    foreach (var aNode in detAlerts)
                    {
                        if (aNode is not JsonObject det) continue;
                        var key = det["key"]?.ToString() ?? "";
                        activeKeys.Add(key);

                        var old = storeAlerts.LastOrDefault(a => a?["key"]?.ToString() == key && a["active"]?.GetValue<bool>() == true) as JsonObject;
                        if (old == null)
                        {
                            var rec = (JsonObject)det.DeepClone();
                            rec["id"] = Guid.NewGuid().ToString();
                            rec["active"] = true;
                            rec["ignored"] = false;
                            rec["createdAt"] = nowVal;
                            storeAlerts.Add((JsonNode)rec);
                            added.Add(rec);
                            changed = true;
                        }
                    }
                }
            }
        }

        foreach (var aNode in storeAlerts)
        {
            if (aNode is JsonObject alert && alert["active"]?.GetValue<bool>() == true && !activeKeys.Contains(alert["key"]?.ToString() ?? ""))
            {
                alert["active"] = false;
                alert["resolvedAt"] = nowVal;
                changed = true;
            }
        }

        if (changed) Save();

        var autoReview = _store["autoReview"]?.GetValue<bool>() == true;
        var modelId = _store["modelId"]?.ToString();
        var models = snapshot["models"] as JsonArray;
        var plans = (snapshot["plans"] as JsonArray)?.Cast<JsonObject>().ToList();
        var selectedModel = models?.FirstOrDefault(m => m?["id"]?.ToString() == modelId) as JsonObject;

        var autoCalls = _store["autoCalls"] as JsonArray ?? new JsonArray();
        _store["autoCalls"] = autoCalls;

        var hourAgo = nowParsed - 3600000;
        var recentCallsCount = autoCalls.Count(c => DateTimeOffset.TryParse(c?.ToString(), out var cdto) && cdto.ToUnixTimeMilliseconds() > hourAgo);

        if (autoReview && !string.IsNullOrEmpty(modelId) && AdvisoryError(selectedModel, plans) == null && !_busy && added.Count > 0 && recentCallsCount < 3)
        {
            var firstAlert = added[0];
            autoCalls.Add((JsonNode)nowVal);
            Save();
            _ = Ask(snapshot, firstAlert["projectId"]?.ToString() ?? "", $"Explain this anomaly and suggest a safe next action: {firstAlert["title"]}. {firstAlert["message"]}", automatic: true);
        }

        return View(snapshot);
    }

    public JsonObject View(JsonObject snapshot)
    {
        var nowVal = _now();
        var nowParsed = DateTimeOffset.TryParse(nowVal, out var dto) ? dto.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var projects = snapshot["projects"] as JsonArray;
        var storeAlerts = _store["alerts"] as JsonArray ?? new JsonArray();
        var storeTurns = _store["turns"] as JsonArray ?? new JsonArray();

        var projList = new JsonArray();
        if (projects != null)
        {
            foreach (var pNode in projects)
            {
                if (pNode is not JsonObject proj) continue;
                var projId = proj["id"]?.ToString() ?? "";
                var facts = ProjectFacts(snapshot, projId, nowParsed);

                var pAlerts = new JsonArray();
                foreach (var aNode in storeAlerts)
                    if (aNode is JsonObject a && a["projectId"]?.ToString() == projId && a["active"]?.GetValue<bool>() == true)
                        pAlerts.Add((JsonNode)a.DeepClone());
                facts["alerts"] = pAlerts;

                var pTurns = new JsonArray();
                var filteredTurns = storeTurns.Where(t => t?["projectId"]?.ToString() == projId).TakeLast(30);
                foreach (var t in filteredTurns)
                    if (t != null) pTurns.Add((JsonNode)t.DeepClone());
                facts["turns"] = pTurns;

                projList.Add((JsonNode)facts);
            }
        }

        var models = snapshot["models"] as JsonArray;
        var plans = (snapshot["plans"] as JsonArray)?.Cast<JsonObject>().ToList();
        var modelList = new JsonArray();
        if (models != null)
        {
            foreach (var mNode in models)
            {
                if (mNode is not JsonObject m) continue;
                if (m["roles"]?["supervisor"]?.GetValue<bool>() == true)
                {
                    var advErr = AdvisoryError(m, plans);
                    modelList.Add((JsonNode)new JsonObject
                    {
                        ["id"] = m["id"]?.ToString(),
                        ["label"] = m["label"]?.ToString(),
                        ["provider"] = m["providerId"]?.ToString() ?? m["provider"]?.ToString(),
                        ["model"] = m["cli"]?.ToString(),
                        ["available"] = advErr == null,
                        ["reason"] = advErr ?? ""
                    });
                }
            }
        }

        return new JsonObject
        {
            ["name"] = Label(_store["name"]?.ToString()),
            ["configured"] = _store["configured"]?.GetValue<bool>() == true,
            ["modelId"] = _store["modelId"]?.ToString() ?? "",
            ["autoReview"] = _store["autoReview"]?.GetValue<bool>() == true,
            ["busy"] = _busy,
            ["projects"] = projList,
            ["models"] = modelList
        };
    }

    public JsonObject Settings(JsonObject input, JsonObject snapshot)
    {
        if (input.ContainsKey("name"))
        {
            _store["name"] = Label(input["name"]?.ToString());
            _store["configured"] = true;
        }

        if (input.ContainsKey("modelId"))
        {
            var mId = input["modelId"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(mId))
            {
                var models = snapshot["models"] as JsonArray;
                var found = models?.FirstOrDefault(m => m is JsonObject mObj && mObj["id"]?.ToString() == mId && mObj["roles"]?["supervisor"]?.GetValue<bool>() == true && ModelRoles.SupportsSupervisor(mObj)) as JsonObject;
                if (found == null)
                    throw new InvalidOperationException("Select an eligible Supervisor model; no substitution is allowed.");
            }
            _store["modelId"] = mId;
        }

        if (input.ContainsKey("autoReview"))
        {
            var ar = input["autoReview"]?.GetValue<bool>() == true;
            if (ar && string.IsNullOrEmpty(_store["modelId"]?.ToString()))
                throw new InvalidOperationException("Select a Supervisor before enabling anomaly analysis.");
            _store["autoReview"] = ar;
        }

        if (string.IsNullOrEmpty(_store["modelId"]?.ToString()))
            _store["autoReview"] = false;

        Save();
        return View(snapshot);
    }

    public void Ignore(string projectId, string alertId)
    {
        var alerts = _store["alerts"] as JsonArray;
        var alert = alerts?.FirstOrDefault(a => a?["id"]?.ToString() == alertId && a["projectId"]?.ToString() == projectId && a["active"]?.GetValue<bool>() == true) as JsonObject;
        if (alert == null)
            throw new InvalidOperationException("This alert is no longer active for the selected project.");

        alert["ignored"] = true;
        alert["ignoredAt"] = _now();
        Save();
    }

    public async Task<JsonObject> Ask(JsonObject snapshot, string projectId, string question, bool automatic = false)
    {
        if (_busy) throw new InvalidOperationException("A Supervisor answer is already in progress.");
        var trimmed = (question ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 8000)
            throw new InvalidOperationException("Enter a question of at most 8000 characters.");

        var nowVal = _now();
        var nowParsed = DateTimeOffset.TryParse(nowVal, out var dto) ? dto.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var facts = ProjectFacts(snapshot, projectId, nowParsed);
        _busy = true;

        var mId = _store["modelId"]?.ToString() ?? "";
        var turn = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["projectId"] = projectId,
            ["question"] = trimmed,
            ["at"] = nowVal,
            ["automatic"] = automatic,
            ["mode"] = !string.IsNullOrEmpty(mId) ? "supervisor" : "facts",
            ["modelId"] = !string.IsNullOrEmpty(mId) ? mId : null,
            ["status"] = "running"
        };

        var turns = _store["turns"] as JsonArray ?? new JsonArray();
        _store["turns"] = turns;
        turns.Add((JsonNode)turn);
        Save();

        try
        {
            if (!string.IsNullOrEmpty(mId))
            {
                var models = snapshot["models"] as JsonArray;
                var plans = (snapshot["plans"] as JsonArray)?.Cast<JsonObject>().ToList();
                var model = models?.FirstOrDefault(m => m?["id"]?.ToString() == mId) as JsonObject;
                var denied = AdvisoryError(model, plans);
                if (denied != null) throw new InvalidOperationException(denied);

                if (_review == null) throw new InvalidOperationException("Supervisor review handler not configured.");

                var history = new JsonArray();
                var pastCompleted = turns.Where(t => t?["projectId"]?.ToString() == projectId && t["status"]?.ToString() == "complete").TakeLast(6);
                foreach (var t in pastCompleted)
                {
                    if (t is JsonObject tObj)
                    {
                        history.Add((JsonNode)new JsonObject
                        {
                            ["question"] = tObj["question"]?.ToString(),
                            ["answer"] = tObj["answer"]?.ToString()
                        });
                    }
                }

                var req = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["modelId"] = mId,
                    ["question"] = trimmed,
                    ["facts"] = facts,
                    ["history"] = history
                };

                var res = await _review(req);
                if (res["failure"]?.GetValue<bool>() == true)
                    throw new InvalidOperationException(res["diagnostic"]?["message"]?.ToString() ?? res["answer"]?.ToString() ?? "Supervisor analysis failed.");

                var ans = res["answer"]?.ToString() ?? "";
                if (ans.Length > 40000) ans = ans[..40000];
                if (string.IsNullOrWhiteSpace(ans)) throw new InvalidOperationException("Supervisor returned no answer.");

                turn["answer"] = ans;
                turn["provider"] = res["provider"]?.ToString();
                turn["model"] = res["model"]?.ToString();
                turn["tokens"] = res["tokens"] != null ? (JsonNode)res["tokens"]!.DeepClone() : null;
            }
            else
            {
                var isReviewer = trimmed.Contains("reviewer", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("revisor", StringComparison.OrdinalIgnoreCase);
                var workers = facts["workers"] as JsonArray;
                var selected = isReviewer
                    ? workers?.Where(w => w?["role"]?.ToString() == "reviewer").ToList()
                    : workers?.ToList();

                var lines = new List<string>
                {
                    "Live evidence (no AI interpretation):",
                    facts["summary"]?.ToString() ?? ""
                };
                if (selected != null)
                {
                    foreach (var w in selected)
                    {
                        var role = w?["role"]?.ToString() == "reviewer" ? "Reviewer" : "Worker";
                        lines.Add($"{role} {w?["provider"]}/{w?["model"]}: {w?["task"]} — {w?["status"]}. {w?["summary"]}");
                    }
                }
                if (facts["alerts"] is JsonArray al)
                {
                    foreach (var a in al)
                        lines.Add($"Attention: {a?["title"]}. {a?["message"]}");
                }
                var next = facts["next"]?.ToString();
                if (!string.IsNullOrEmpty(next)) lines.Add("Next recorded action: " + next);
                lines.Add("Select a Supervisor model for interpretation of this evidence.");

                turn["answer"] = string.Join("\n\n", lines.Where(l => !string.IsNullOrEmpty(l)));
            }
            turn["status"] = "complete";
        }
        catch (Exception ex)
        {
            turn["status"] = "failed";
            turn["answer"] = ex.Message;
        }
        finally
        {
            _busy = false;
            turn["finishedAt"] = _now();
            Save();
        }

        return View(snapshot);
    }
}
