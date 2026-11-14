// Port of engine/autonomy.cjs.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Project autonomy policy. The owner's setting grants authority; model text only
// proposes the next objective and is never parsed into any other action.
public static class Autonomy
{
    public static class LIMITS
    {
        public const int MaxMegaTasks = 5;
        public const int MaxNudgesPerHour = 3;
        public const int MaxObjectiveChars = 4000;

        public const int maxMegaTasks = 5;
        public const int maxNudgesPerHour = 3;
        public const int maxObjectiveChars = 4000;
    }

    public const int MAX_PARTS = 10;
    const double HOUR_MS = 3600000;
    static readonly string[] SIZES = ["light", "normal", "heavy"];
    static readonly string[] WORK_ENDED = ["worker failure", "blocked worker"];

    public static JsonObject CreateSettings(string sessionId, string at) => new()
    {
        ["enabled"] = true,
        ["sessionId"] = sessionId,
        ["enabledAt"] = at,
        ["started"] = false,
        ["approved"] = new JsonArray(),
        ["evaluated"] = new JsonArray(),
        ["nudgedKeys"] = new JsonArray(),
        ["nudgeTimes"] = new JsonArray(),
        ["assumptions"] = new JsonArray(),
        ["stopReason"] = null
    };

    // Rewrites every push URL to a helper that does not exist. A deterrent for
    // accidental pushes, not a sandbox: a process can override its own Git config.
    public static Dictionary<string, string> PushGuardEnvironment(IReadOnlyDictionary<string, string?>? env = null)
    {
        var index = 0;
        if (env is not null && env.TryGetValue("GIT_CONFIG_COUNT", out var countVal) && int.TryParse(countVal, out var c))
        {
            index = c;
        }

        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = (index + 1).ToString(CultureInfo.InvariantCulture),
            [$"GIT_CONFIG_KEY_{index}"] = "url.planwharf-push-blocked://.pushInsteadOf",
            [$"GIT_CONFIG_VALUE_{index}"] = ""
        };
    }

    public static JsonObject PushGuardEnvironment(JsonObject? env)
    {
        var dict = new Dictionary<string, string?>();
        if (env is not null)
        {
            foreach (var pair in env)
            {
                dict[pair.Key] = pair.Value?.GetValue<string>();
            }
        }
        var result = PushGuardEnvironment(dict);
        var obj = new JsonObject();
        foreach (var pair in result) obj[pair.Key] = pair.Value;
        return obj;
    }

    public static JsonObject NextStep(JsonObject options)
    {
        var mode = options["mode"]?.GetValue<string>();
        var sessionId = options["sessionId"]?.GetValue<string>();
        var settings = options["settings"] as JsonObject;
        var row = options["row"] as JsonObject;
        var alerts = options["alerts"] as JsonArray;
        double? now = options.TryGetPropertyValue("now", out var nowNode) && nowNode is JsonValue nv && nv.TryGetValue<double>(out var nd)
            ? nd
            : null;

        return NextStep(mode, sessionId, settings, row, alerts, now);
    }

    public static JsonObject NextStep(string? mode, string? sessionId, JsonObject? settings, JsonObject? row, JsonArray? alerts = null, double? now = null)
    {
        static JsonObject None(string reason) => new() { ["type"] = "none", ["reason"] = reason };

        if (settings is null || settings["enabled"]?.GetValue<bool>() != true) return None("off");
        if (mode != "auto") return None("manual-mode");
        if (settings["sessionId"]?.GetValue<string>() != sessionId) return None("other-session");
        if (settings.TryGetPropertyValue("stopReason", out var srNode) && srNode is JsonValue srVal && srVal.TryGetValue<string>(out var srStr) && !string.IsNullOrEmpty(srStr))
        {
            return None("stopped");
        }
        if (row is null) return None("no-project");

        var status = row["status"]?.GetValue<string>();
        var checkpoint = row["checkpoint"] as JsonObject;
        var checkpointReason = checkpoint?["reason"]?.GetValue<string>();
        var workEnded = status == "checkpointed" && checkpointReason is not null && WORK_ENDED.Contains(checkpointReason);

        var megaTask = row["megaTask"] as JsonObject;
        var pauseRequested = megaTask?["pauseRequested"]?.GetValue<bool>() == true;

        if ((status == "checkpointed" && !workEnded) || status == "paused" || pauseRequested)
        {
            return None("user-paused");
        }

        var megaTaskId = row["megaTaskId"]?.GetValue<string>();
        if (status == "needs_approval" && !string.IsNullOrEmpty(megaTaskId))
        {
            var approved = settings["approved"] as JsonArray;
            if (approved is not null)
            {
                foreach (var a in approved)
                {
                    if (a?.GetValue<string>() == megaTaskId) return None("already-approved");
                }
                if (approved.Count >= LIMITS.MaxMegaTasks)
                {
                    return new JsonObject { ["type"] = "limit", ["reason"] = "MEGATASK_LIMIT" };
                }
            }
            return new JsonObject { ["type"] = "approve", ["megaTaskId"] = megaTaskId };
        }

        var started = settings["started"]?.GetValue<bool>() == true;
        var megaTaskStatus = megaTask?["status"]?.GetValue<string>();
        if (!started && (string.IsNullOrEmpty(megaTaskId) || status == "complete" || (megaTaskStatus ?? "").ToUpperInvariant() == "CANCELLED"))
        {
            return new JsonObject { ["type"] = "start" };
        }

        if (status == "complete" && !string.IsNullOrEmpty(megaTaskId))
        {
            var evaluated = settings["evaluated"] as JsonArray;
            var isEvaluated = false;
            if (evaluated is not null)
            {
                foreach (var ev in evaluated)
                {
                    if (ev?.GetValue<string>() == megaTaskId) { isEvaluated = true; break; }
                }
            }
            if (!isEvaluated) return new JsonObject { ["type"] = "evaluate", ["megaTaskId"] = megaTaskId };
        }

        JsonObject? chosenAlert = null;
        if (alerts is not null)
        {
            var nudgedKeys = new HashSet<string>();
            if (settings["nudgedKeys"] is JsonArray nk)
            {
                foreach (var k in nk) if (k?.GetValue<string>() is string ks) nudgedKeys.Add(ks);
            }

            foreach (var item in alerts)
            {
                if (item is not JsonObject alert) continue;
                if (alert.TryGetPropertyValue("active", out var actNode) && actNode is JsonValue actVal && actVal.TryGetValue<bool>(out var act) && !act) continue;
                if (alert["ignored"]?.GetValue<bool>() == true) continue;
                var key = alert["key"]?.GetValue<string>();
                if (key is not null && nudgedKeys.Contains(key)) continue;

                chosenAlert = alert;
                break;
            }
        }

        if (chosenAlert is null) return None("idle");

        var currentTime = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nudgeTimes = settings["nudgeTimes"] as JsonArray;
        var recentNudges = 0;
        if (nudgeTimes is not null)
        {
            foreach (var nt in nudgeTimes)
            {
                if (nt?.GetValue<string>() is string ntStr && DateTimeOffset.TryParse(ntStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                {
                    if (currentTime - dto.ToUnixTimeMilliseconds() < HOUR_MS) recentNudges++;
                }
            }
        }

        if (recentNudges >= LIMITS.MaxNudgesPerHour) return None("nudge-limit");

        return new JsonObject { ["type"] = "nudge", ["alert"] = (JsonObject)chosenAlert.DeepClone() };
    }

    public static string LeadInstructions() => string.Join("\n",
        "Reply with one JSON object and nothing else: {\"done\":true|false,\"next\":\"objective or null\",\"assumptions\":[\"...\"]}.",
        "Set done to true only if the goal is met by the recorded results. Otherwise put the single next objective in next.",
        "Decide open questions yourself and list what you assumed. Work stays inside the project folder: never push, merge to a shared branch, or publish a release.");

    public static List<string> ChecksText(JsonObject? checks, bool isNullChecks = false)
    {
        if (checks is null && !isNullChecks) return new List<string>();
        if (checks is null) return new List<string> { "Planwharf found no recognised test entry point (package.json test script or test files) in the project folder." };

        var timedOut = checks["timedOut"]?.GetValue<bool>() == true;
        var exitCode = checks["exitCode"]?.GetValue<int>() ?? 0;
        var verdict = timedOut ? "TIMED OUT" : exitCode == 0 ? "PASSED" : $"FAILED (exit {exitCode})";
        var label = checks["label"]?.GetValue<string>() ?? "";
        var seconds = checks["seconds"]?.ToString() ?? "0";
        var output = checks["output"]?.GetValue<string>();
        if (string.IsNullOrEmpty(output)) output = "(no output)";

        return new List<string>
        {
            $"Planwharf ran `{label}` in the project folder just now: {verdict} in {seconds} s. Output tail:",
            "```",
            output,
            "```",
            "This run is fresh evidence. Do not run commands or edit files yourself: decide from the goal, the results and this run."
        };
    }

    public static bool CleanFinish(JsonArray? results, JsonObject? checks)
    {
        if (results is null || results.Count < 2 || checks is null) return false;
        if (checks["timedOut"]?.GetValue<bool>() == true || (checks["exitCode"]?.GetValue<int>() ?? 0) != 0) return false;

        foreach (var r in results)
        {
            if (r is not JsonObject rObj) return false;
            if (rObj["status"]?.GetValue<string>() != "COMPLETE") return false;
            if (rObj["blockers"] is JsonArray blockers && blockers.Count > 0) return false;
        }

        return true;
    }

    public static string EvaluationPrompt(JsonObject options)
    {
        var name = options["name"]?.GetValue<string>() ?? "";
        var goal = options["goal"]?.GetValue<string>();
        var results = options["results"] as JsonArray;
        var checksNode = options.TryGetPropertyValue("checks", out var cn) ? cn : null;
        var checks = checksNode as JsonObject;
        var isNullChecks = checksNode is null && options.ContainsKey("checks");

        var lines = new List<string>();
        if (results is not null)
        {
            var count = results.Count;
            var start = Math.Max(0, count - 6);
            for (var i = start; i < count; i++)
            {
                if (results[i] is JsonObject r)
                {
                    var status = r["status"]?.GetValue<string>() ?? "";
                    var summary = r["summary"]?.GetValue<string>() ?? "no summary";
                    lines.Add($"- {status}: {summary}");
                }
            }
        }

        var parts = new List<string>
        {
            $"Autonomous review of project {name}. The current MegaTask finished.",
            $"Goal: {goal ?? "not recorded"}",
            "Recorded results:"
        };

        if (lines.Count > 0) parts.AddRange(lines);
        else parts.Add("- none recorded");

        if (options.ContainsKey("checks"))
        {
            parts.AddRange(ChecksText(checks, checksNode is null));
        }

        parts.Add(LeadInstructions());
        return string.Join("\n", parts);
    }

    public static string NudgePrompt(JsonObject options)
    {
        var name = options["name"]?.GetValue<string>() ?? "";
        var goal = options["goal"]?.GetValue<string>();
        var alert = options["alert"] as JsonObject ?? new JsonObject();
        var title = alert["title"]?.GetValue<string>() ?? "";
        var message = alert["message"]?.GetValue<string>() ?? "";

        return string.Join("\n",
            $"Supervisor alert for project {name}: {title}. {message}",
            $"Goal: {goal ?? "not recorded"}",
            "If active workers are still making progress, set next to null. If the work is stuck, name the objective that recovers it.",
            LeadInstructions());
    }

    public static string StrictDecisionPrompt(string question) =>
        $"{question}\n\nYour previous reply could not be read. Reply with only the JSON object, with no other text before or after it.";

    public static string AssignmentPrompt(JsonObject options)
    {
        var name = options["name"]?.GetValue<string>() ?? "";
        var goal = options["goal"]?.GetValue<string>();
        var slices = options["slices"] as JsonArray ?? new JsonArray();
        var candidates = options["candidates"] as JsonArray ?? new JsonArray();
        var said = options["said"]?.GetValue<string>()?.Trim() ?? "";

        var quoted = said.Length > 2500 ? "…" + said.Substring(said.Length - 2499) : said;

        var parts = new List<string>
        {
            $"Autonomous planning for project {name}. Goal: {goal ?? "not recorded"}"
        };

        if (!string.IsNullOrEmpty(quoted))
        {
            parts.Add($"You told the user in the chat, before this work started:\n<<<\n{quoted}\n>>>");
            parts.Add("If you proposed a crew there, keep that crew unless one of its models is not allowed below; if you choose differently, say why in \"why\".");
        }

        parts.Add("The MegaTask has these slices, run in order. Each lists the models allowed to take it:");

        for (var i = 0; i < slices.Count; i++)
        {
            var sliceText = slices[i]?.GetValue<string>() ?? "";
            var candArr = (i < candidates.Count ? candidates[i] as JsonArray : null) ?? new JsonArray();
            var candList = new List<string>();
            foreach (var c in candArr)
            {
                if (c is JsonObject cObj)
                {
                    candList.Add($"{cObj["id"]?.GetValue<string>()} ({cObj["label"]?.GetValue<string>()}, provider {cObj["provider"]?.GetValue<string>()})");
                }
            }
            parts.Add($"{i + 1}. {sliceText}\n   Allowed: {string.Join("; ", candList)}");
        }

        parts.Add("Choose one allowed model per slice. Pick the model most likely to do that slice correctly. For each later slice prefer a different provider than the slice before it, so the work is checked independently.");
        parts.Add("Do not run commands or read files: decide from this message only.");
        parts.Add("Reply with one JSON object and nothing else: {\"assign\":[\"worker id for slice 1\", \"...\"],\"why\":\"one sentence\"}.");

        return string.Join("\n", parts);
    }

    public static string SplitPrompt(JsonObject options)
    {
        var name = options["name"]?.GetValue<string>() ?? "";
        var goal = options["goal"]?.GetValue<string>();
        var builders = options["builders"] as JsonArray ?? new JsonArray();
        var verifiers = options["verifiers"] as JsonArray ?? new JsonArray();
        var files = options["files"] as JsonArray;
        var said = options["said"]?.GetValue<string>()?.Trim() ?? "";

        var quoted = said.Length > 2500 ? "…" + said.Substring(said.Length - 2499) : said;

        static string Describe(JsonNode? modelNode)
        {
            if (modelNode is not JsonObject m) return "";
            var id = m["id"]?.GetValue<string>();
            var label = m["label"]?.GetValue<string>();
            var provider = m["provider"]?.GetValue<string>();
            var tier = m["tier"]?.GetValue<string>();
            string quota;
            if (m.TryGetPropertyValue("left", out var leftNode) && leftNode is JsonValue lv && lv.TryGetValue<double>(out var leftVal) && double.IsFinite(leftVal))
            {
                quota = $", {leftVal}% quota left";
            }
            else quota = ", quota not reported";
            return $"{id} ({label}, provider {provider}, {tier}{quota})";
        }

        var parts = new List<string>
        {
            $"Autonomous planning for project {name}. Goal: {goal ?? "not recorded"}"
        };

        if (!string.IsNullOrEmpty(quoted))
        {
            parts.Add($"You told the user in the chat, before this work started:\n<<<\n{quoted}\n>>>");
        }

        parts.Add($"Your crew can work in parallel: up to {MAX_PARTS} workers at the same time, all in the same project folder.");
        parts.Add("Split the work into parts only where that saves time. Parts that run at the same time must touch different files: two workers on one file overwrite each other. A small job stays one part.");
        parts.Add("For each part give: title; detail (complete instructions, the worker sees nothing else); files (the files or folders it may change, relative to the project folder; nothing outside them); dependsOn (indexes of earlier parts that must finish first); size (light, normal or heavy); model (one id allowed to build).");
        parts.Add("Files that every part would touch (project files such as .csproj or package.json, shared configuration) belong to one last integration part that depends on the others.");
        parts.Add("Each model has a weight: light, default or premium. Planwharf enforces it: a light part goes to a light model, a normal part to a default or light model, a heavy part to any model; a part given to a heavier model is moved to a lighter one when there is one. Strong models working at the same time drain their company's shared quota fast: keep premium models for heavy parts.");
        parts.Add("Also name \"verify\": the model that checks the whole work at the end, allowed to verify, ideally from a company that built none of the parts.");

        var builderDescs = new List<string>();
        foreach (var b in builders) builderDescs.Add(Describe(b));
        parts.Add("Models allowed to build: " + string.Join("; ", builderDescs));

        var verifierDescs = new List<string>();
        foreach (var v in verifiers) verifierDescs.Add(Describe(v));
        parts.Add("Models allowed to verify: " + string.Join("; ", verifierDescs));

        if (files is not null && files.Count > 0)
        {
            var listed = files.Take(400).Select(f => f?.GetValue<string>() ?? "").ToList();
            var totalCount = files.Count;
            var header = $"Project files ({listed.Count}{(totalCount > listed.Count ? " of " + totalCount : "")}):\n{string.Join("\n", listed)}";
            parts.Add(header);
        }

        parts.Add("Do not run commands or read files: decide from this message only.");
        parts.Add("Reply with one JSON object and nothing else: {\"parts\":[{\"title\":\"\",\"detail\":\"\",\"files\":[\"relative/path\"],\"dependsOn\":[],\"size\":\"normal\",\"model\":\"id\"}],\"verify\":\"id\",\"why\":\"one sentence\"}");

        return string.Join("\n", parts);
    }

    public static List<JsonObject> JsonObjects(string text)
    {
        var found = new List<JsonObject>();
        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var depth = 0;
            var quoted = false;
            var escaped = false;
            for (var index = start; index < text.Length; index++)
            {
                var c = text[index];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try
                    {
                        var slice = text.Substring(start, index - start + 1);
                        if (JsonNode.Parse(slice) is JsonObject obj) found.Add(obj);
                    }
                    catch
                    {
                        // not JSON
                    }
                    break;
                }
            }
        }
        return found;
    }

    public static JsonObject? ParseDecision(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var list = JsonObjects(text);
        JsonObject? parsed = null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (item.TryGetPropertyValue("done", out var dNode) && dNode is JsonValue dv && dv.TryGetValue<bool>(out _))
            {
                parsed = item;
                break;
            }
        }
        if (parsed is null) return null;

        var done = parsed["done"]!.GetValue<bool>();
        string? next = null;
        if (parsed.TryGetPropertyValue("next", out var nNode) && nNode is JsonValue nv && nv.TryGetValue<string>(out var ns) && !string.IsNullOrWhiteSpace(ns))
        {
            var trimmed = ns.Trim();
            next = trimmed.Length > LIMITS.MaxObjectiveChars ? trimmed.Substring(0, LIMITS.MaxObjectiveChars) : trimmed;
        }

        if (!done && parsed.TryGetPropertyValue("next", out var nCheck) && nCheck is not null && next is null)
        {
            return null;
        }

        var assumptionsArr = new JsonArray();
        if (parsed["assumptions"] is JsonArray assArr)
        {
            var count = 0;
            foreach (var a in assArr)
            {
                if (count >= 10) break;
                if (a is JsonValue av && av.TryGetValue<string>(out var asStr))
                {
                    var clipped = asStr.Length > 500 ? asStr.Substring(0, 500) : asStr;
                    assumptionsArr.Add((JsonNode)JsonValue.Create(clipped));
                    count++;
                }
            }
        }

        var result = new JsonObject
        {
            ["done"] = done,
            ["next"] = done ? null : (next is null ? null : (JsonNode)JsonValue.Create(next)),
            ["assumptions"] = assumptionsArr
        };
        return result;
    }

    public static JsonObject? ParseAssignment(string? text, int count, JsonNode? allowed)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start) return null;

        JsonObject? parsed = null;
        try
        {
            var slice = text.Substring(start, end - start + 1);
            if (JsonNode.Parse(slice) is JsonObject obj) parsed = obj;
        }
        catch
        {
            return null;
        }

        if (parsed?["assign"] is not JsonArray ids || ids.Count != count) return null;

        HashSet<string> AllowedAt(int index)
        {
            var set = new HashSet<string>();
            if (allowed is JsonArray arr)
            {
                if (arr.Count > 0 && arr[0] is JsonArray)
                {
                    if (index < arr.Count && arr[index] is JsonArray sub)
                    {
                        foreach (var x in sub) if (x?.GetValue<string>() is string s) set.Add(s);
                    }
                }
                else
                {
                    foreach (var x in arr) if (x?.GetValue<string>() is string s) set.Add(s);
                }
            }
            return set;
        }

        var resultIds = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var idNode = ids[i];
            if (idNode is not JsonValue jv || !jv.TryGetValue<string>(out var idStr)) return null;
            if (!AllowedAt(i).Contains(idStr)) return null;
            resultIds.Add((JsonNode)JsonValue.Create(idStr));
        }

        var why = "";
        if (parsed.TryGetPropertyValue("why", out var wNode) && wNode is JsonValue wv && wv.TryGetValue<string>(out var ws))
        {
            why = ws.Length > 300 ? ws.Substring(0, 300) : ws;
        }

        return new JsonObject
        {
            ["assign"] = resultIds,
            ["why"] = why
        };
    }

    static readonly Dictionary<string, string[]> FITS = new()
    {
        ["light"] = ["light", "default", "premium"],
        ["normal"] = ["default", "light", "premium"],
        ["heavy"] = ["premium", "default", "light"]
    };

    static readonly Dictionary<string, string[]> ALLOWED_TIERS = new()
    {
        ["light"] = ["light"],
        ["normal"] = ["default", "light"],
        ["heavy"] = ["premium", "default", "light"]
    };

    public static JsonObject FitWeights(JsonArray? parts, JsonArray? builders)
    {
        var builderList = new List<JsonObject>();
        var byId = new Dictionary<string, JsonObject>();
        if (builders is not null)
        {
            foreach (var b in builders)
            {
                if (b is JsonObject bObj && bObj["id"]?.GetValue<string>() is string id)
                {
                    builderList.Add(bObj);
                    byId[id] = bObj;
                }
            }
        }

        var load = new Dictionary<string, int>();
        var moved = new JsonArray();
        var outParts = new JsonArray();

        if (parts is not null)
        {
            foreach (var pNode in parts)
            {
                if (pNode is not JsonObject part) continue;
                var rawSize = part["size"]?.GetValue<string>() ?? "";
                var size = SIZES.Contains(rawSize) ? rawSize : "normal";
                var model = part["model"]?.GetValue<string>() ?? "";

                byId.TryGetValue(model, out var current);
                var currentTier = current?["tier"]?.GetValue<string>();
                var chosen = model;

                if (current is null || currentTier is null || !ALLOWED_TIERS[size].Contains(currentTier))
                {
                    foreach (var tier in FITS[size])
                    {
                        var options = builderList.Where(m => m["tier"]?.GetValue<string>() == tier).ToList();
                        if (options.Count == 0) continue;
                        if (currentTier is not null && Array.IndexOf(FITS[size], tier) >= Array.IndexOf(FITS[size], currentTier)) break;

                        options.Sort((a, b) =>
                        {
                            var aId = a["id"]?.GetValue<string>() ?? "";
                            var bId = b["id"]?.GetValue<string>() ?? "";
                            load.TryGetValue(aId, out var aLoad);
                            load.TryGetValue(bId, out var bLoad);
                            var diff = aLoad - bLoad;
                            if (diff != 0) return diff;

                            var aFinite = a.TryGetPropertyValue("left", out var aLeftNode) && aLeftNode is JsonValue av && av.TryGetValue<double>(out var aL) && double.IsFinite(aL);
                            var bFinite = b.TryGetPropertyValue("left", out var bLeftNode) && bLeftNode is JsonValue bv && bv.TryGetValue<double>(out var bL) && double.IsFinite(bL);
                            var finiteDiff = (aFinite ? 0 : 1) - (bFinite ? 0 : 1);
                            if (finiteDiff != 0) return finiteDiff;

                            var aVal = aFinite ? aLeftNode!.GetValue<double>() : 0;
                            var bVal = bFinite ? bLeftNode!.GetValue<double>() : 0;
                            return bVal.CompareTo(aVal);
                        });

                        chosen = options[0]["id"]!.GetValue<string>();
                        break;
                    }
                }

                load.TryGetValue(chosen, out var curLoad);
                load[chosen] = curLoad + 1;

                if (chosen != model)
                {
                    moved.Add((JsonNode)new JsonObject
                    {
                        ["title"] = part["title"]?.DeepClone(),
                        ["size"] = size,
                        ["from"] = model,
                        ["to"] = chosen
                    });
                }

                var outPart = (JsonObject)part.DeepClone();
                outPart["model"] = chosen;
                outParts.Add((JsonNode)outPart);
            }
        }

        return new JsonObject
        {
            ["parts"] = outParts,
            ["moved"] = moved
        };
    }

    static string NormalPath(string file) => file.Replace('\\', '/').TrimEnd('/');

    static bool Overlaps(IEnumerable<string> a, IEnumerable<string> b)
    {
        var bList = b.ToList();
        return a.Any(x => bList.Any(y => x == y || y.StartsWith(x + "/", StringComparison.Ordinal) || x.StartsWith(y + "/", StringComparison.Ordinal)));
    }

    public static JsonObject? ParseSplit(string? text, JsonObject options)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var builderIds = new HashSet<string>();
        if (options["builders"] is JsonArray bArr)
        {
            foreach (var b in bArr) if (b?.GetValue<string>() is string bs) builderIds.Add(bs);
        }

        var verifierIds = new HashSet<string>();
        if (options["verifiers"] is JsonArray vArr)
        {
            foreach (var v in vArr) if (v?.GetValue<string>() is string vs) verifierIds.Add(vs);
        }

        var list = JsonObjects(text);
        JsonObject? parsed = null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (item["parts"] is JsonArray) { parsed = item; break; }
        }

        if (parsed is null) return null;
        if (parsed["parts"] is not JsonArray rawParts || rawParts.Count == 0 || rawParts.Count > MAX_PARTS) return null;

        var verify = parsed["verify"]?.GetValue<string>();
        if (verify is null || !verifierIds.Contains(verify)) return null;

        var parts = new List<JsonObject>();
        for (var index = 0; index < rawParts.Count; index++)
        {
            if (rawParts[index] is not JsonObject part) return null;
            var title = part["title"]?.GetValue<string>();
            var detail = part["detail"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(detail)) return null;

            var model = part["model"]?.GetValue<string>();
            var size = part["size"]?.GetValue<string>();
            if (model is null || !builderIds.Contains(model) || size is null || !SIZES.Contains(size)) return null;

            if (part["files"] is not JsonArray filesArr || filesArr.Count == 0) return null;
            var files = new List<string>();
            foreach (var fNode in filesArr)
            {
                if (fNode is not JsonValue fv || !fv.TryGetValue<string>(out var fStr) || string.IsNullOrWhiteSpace(fStr)) return null;
                files.Add(NormalPath(fStr.Trim()));
            }

            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file) ||
                    (file.Length >= 2 && char.IsAsciiLetter(file[0]) && file[1] == ':') ||
                    file.StartsWith('/') ||
                    file.Split('/').Contains(".."))
                {
                    return null;
                }
            }

            var dependsOn = new List<int>();
            if (part.ContainsKey("dependsOn"))
            {
                if (part["dependsOn"] is not JsonArray depArr) return null;
                foreach (var dNode in depArr)
                {
                    if (dNode is not JsonValue dv || !dv.TryGetValue<int>(out var depInt) || depInt < 0 || depInt >= index) return null;
                    if (!dependsOn.Contains(depInt)) dependsOn.Add(depInt);
                }
            }

            var filesJsonArr = new JsonArray();
            foreach (var f in files) filesJsonArr.Add((JsonNode)JsonValue.Create(f));

            var depsJsonArr = new JsonArray();
            foreach (var d in dependsOn) depsJsonArr.Add((JsonNode)JsonValue.Create(d));

            var row = new JsonObject
            {
                ["title"] = title.Trim() is var t && t.Length > 200 ? t.Substring(0, 200) : t,
                ["detail"] = detail.Trim() is var dt && dt.Length > 12000 ? dt.Substring(0, 12000) : dt,
                ["files"] = filesJsonArr,
                ["dependsOn"] = depsJsonArr,
                ["size"] = size,
                ["model"] = model
            };
            parts.Add(row);
        }

        HashSet<int> Before(int index)
        {
            var seen = new HashSet<int>();
            var queue = new List<int>();
            if (parts[index]["dependsOn"] is JsonArray da)
            {
                foreach (var d in da) queue.Add(d!.GetValue<int>());
            }
            while (queue.Count > 0)
            {
                var next = queue[^1];
                queue.RemoveAt(queue.Count - 1);
                if (seen.Add(next))
                {
                    if (parts[next]["dependsOn"] is JsonArray pda)
                    {
                        foreach (var pd in pda) queue.Add(pd!.GetValue<int>());
                    }
                }
            }
            return seen;
        }

        for (var later = 1; later < parts.Count; later++)
        {
            for (var earlier = 0; earlier < later; earlier++)
            {
                var earlierFiles = parts[earlier]["files"]!.AsArray().Select(f => f!.GetValue<string>());
                var laterFiles = parts[later]["files"]!.AsArray().Select(f => f!.GetValue<string>());

                if (Overlaps(earlierFiles, laterFiles) && !Before(later).Contains(earlier))
                {
                    var depList = parts[later]["dependsOn"]!.AsArray().Select(d => d!.GetValue<int>()).ToList();
                    depList.Add(earlier);
                    depList.Sort();
                    var newDepArr = new JsonArray();
                    foreach (var d in depList) newDepArr.Add((JsonNode)JsonValue.Create(d));
                    parts[later]["dependsOn"] = newDepArr;
                }
            }
        }

        var why = "";
        if (parsed.TryGetPropertyValue("why", out var wNode) && wNode is JsonValue wv && wv.TryGetValue<string>(out var ws))
        {
            why = ws.Length > 300 ? ws.Substring(0, 300) : ws;
        }

        var partsArr = new JsonArray();
        foreach (var p in parts) partsArr.Add((JsonNode)p);

        return new JsonObject
        {
            ["parts"] = partsArr,
            ["verify"] = verify,
            ["why"] = why
        };
    }

}
