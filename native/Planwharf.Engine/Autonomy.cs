// Port of engine/autonomy.cjs.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Project autonomy policy. The owner's setting grants authority; model text only
// proposes the next objective and is never parsed into any other action.
public static class Autonomy
{
    public static class LIMITS
    {
        public const int MaxMegaTasks = 20;
        public const int MaxNudgesPerHour = 3;
        public const int MaxObjectiveChars = 4000;

        public const int maxMegaTasks = 20;
        public const int maxNudgesPerHour = 3;
        public const int maxObjectiveChars = 4000;
    }

    public const int MAX_PARTS = 10;
    // A big job may have more parts than run at once: the rest start as earlier ones finish.
    public const int MAX_SPLIT_PARTS = 30;
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
        if (env is not null && env.TryGetValue("GIT_CONFIG_COUNT", out var countVal))
            index = ParseIntOrZero(countVal);

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
        options.TryGetPropertyValue("mode", out var mode);
        options.TryGetPropertyValue("sessionId", out var sessionId);
        var settings = options["settings"] as JsonObject;
        JsonObject? row = null;
        if (options.TryGetPropertyValue("row", out var rowNode) && rowNode is not null)
            row = rowNode as JsonObject ?? new JsonObject();

        var alertsOmitted = !options.ContainsKey("alerts");
        JsonArray? alerts = null;
        if (!alertsOmitted)
        {
            var alertsNode = options["alerts"];
            if (alertsNode is null) throw new InvalidOperationException("Cannot read properties of null (reading 'find')");
            if (alertsNode is not JsonArray found) throw new InvalidOperationException("alerts.find is not a function");
            alerts = found;
        }

        double nowMs;
        if (!options.ContainsKey("now")) nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        else if (options["now"] is null) nowMs = 0;
        else if (options["now"] is JsonValue nowValue && nowValue.GetValueKind() == JsonValueKind.Number) nowMs = Number(nowValue);
        else nowMs = double.NaN;

        return Decide(TryString(mode), sessionId, settings, row, alertsOmitted, alerts, nowMs);
    }

    public static JsonObject NextStep(string? mode, string? sessionId, JsonObject? settings, JsonObject? row, JsonArray? alerts = null, double? now = null) =>
        Decide(mode, sessionId is null ? null : JsonValue.Create(sessionId), settings, row, alerts is null, alerts, now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    static JsonObject Decide(string? mode, JsonNode? sessionId, JsonObject? settings, JsonObject? row, bool alertsOmitted, JsonArray? alerts, double nowMs)
    {
        static JsonObject None(string reason) => new() { ["type"] = "none", ["reason"] = reason };

        if (settings is null || !Truthy(settings, "enabled")) return None("off");
        if (mode != "auto") return None("manual-mode");
        if (!JsonSame(settings.TryGetPropertyValue("sessionId", out var storedSession) ? storedSession : null, sessionId)) return None("other-session");
        if (Truthy(settings, "stopReason")) return None("stopped");
        if (row is null) return None("no-project");

        var status = TryString(row["status"]);
        var checkpoint = row["checkpoint"] as JsonObject;
        var checkpointReason = TryString(checkpoint?["reason"]);
        var workEnded = status == "checkpointed" && checkpointReason is not null && WORK_ENDED.Contains(checkpointReason);
        var megaTask = row["megaTask"] as JsonObject;
        if ((status == "checkpointed" && !workEnded) || status == "paused" || (megaTask is not null && Truthy(megaTask, "pauseRequested") && !workEnded))
            return None("user-paused");

        var hasMegaId = row.TryGetPropertyValue("megaTaskId", out var megaId) && TruthyNode(megaId);
        // Work that ended blocked or failed goes back to the lead at once, with its results.
        if (workEnded && hasMegaId && !(Truthy(settings, "continued") && Includes(settings, "continued", megaId, "includes")))
            return new JsonObject { ["type"] = "continue", ["megaTaskId"] = megaId!.DeepClone() };
        if (status == "needs_approval" && hasMegaId)
        {
            if (Includes(settings, "approved", megaId, "includes")) return None("already-approved");
            if (CollectionLength(settings["approved"]!) >= LIMITS.MaxMegaTasks)
                return new JsonObject { ["type"] = "limit", ["reason"] = "MEGATASK_LIMIT" };
            return new JsonObject { ["type"] = "approve", ["megaTaskId"] = megaId!.DeepClone() };
        }

        var started = Truthy(settings, "started");
        var megaStatus = megaTask is null || !megaTask.TryGetPropertyValue("status", out var megaStatusNode) || !TruthyNode(megaStatusNode)
            ? ""
            : JsText(megaStatusNode, true).ToUpperInvariant();
        if (!started && (!hasMegaId || status == "complete" || megaStatus == "CANCELLED"))
            return new JsonObject { ["type"] = "start" };

        if (status == "complete" && hasMegaId)
        {
            if (!Includes(settings, "evaluated", megaId, "includes"))
                return new JsonObject { ["type"] = "evaluate", ["megaTaskId"] = megaId!.DeepClone() };
        }

        JsonNode? chosen = null;
        if (!alertsOmitted)
        {
            foreach (var item in alerts!)
            {
                if (item is null) throw new InvalidOperationException("Cannot read properties of null (reading 'active')");
                var alert = item as JsonObject;
                if (alert is not null && alert.TryGetPropertyValue("active", out var active) && active is JsonValue activeValue && activeValue.GetValueKind() == JsonValueKind.False) continue;
                if (alert is not null && Truthy(alert, "ignored")) continue;
                var nudged = alert is not null && alert.ContainsKey("key")
                    ? Includes(settings, "nudgedKeys", alert["key"], "includes")
                    : IncludesMissing(settings, "nudgedKeys");
                if (nudged) continue;
                chosen = item;
                break;
            }
        }

        if (chosen is null) return None("idle");
        if (!settings.TryGetPropertyValue("nudgeTimes", out var timesNode))
            throw new InvalidOperationException("Cannot read properties of undefined (reading 'filter')");
        if (timesNode is null) throw new InvalidOperationException("Cannot read properties of null (reading 'filter')");
        if (timesNode is not JsonArray nudgeTimes) throw new InvalidOperationException("nudgeTimes.filter is not a function");

        var recentNudges = 0;
        foreach (var at in nudgeTimes)
        {
            if (TryString(at) is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                && nowMs - parsed.ToUnixTimeMilliseconds() < HOUR_MS)
                recentNudges++;
        }
        if (recentNudges >= LIMITS.MaxNudgesPerHour) return None("nudge-limit");
        return new JsonObject { ["type"] = "nudge", ["alert"] = chosen.DeepClone() };
    }

    public static string LeadInstructions() => string.Join("\n",
        "Reply with one JSON object and nothing else: {\"done\":true|false,\"next\":\"objective or null\",\"assumptions\":[\"...\"],\"summary\":\"one or two plain sentences\"}",
        "Set done to true only if the goal is met by the recorded results. Otherwise put the single next objective in next.",
        PlainLines.SummaryAsk,
        "Decide open questions yourself and list what you assumed. Work stays inside the project folder: never push, merge to a shared branch, or publish a release.");

    public static List<string> ChecksText(JsonObject? checks, bool isNullChecks = false)
    {
        if (checks is null && !isNullChecks) return new List<string>();
        if (checks is null) return new List<string> { "Planwharf found no recognised test entry point (package.json test script or test files) in the project folder." };

        var timedOut = Truthy(checks, "timedOut");
        var hasExit = checks.TryGetPropertyValue("exitCode", out var exitNode);
        var passed = hasExit && exitNode is JsonValue exitValue && exitValue.GetValueKind() == JsonValueKind.Number && Number(exitValue) == 0;
        var verdict = timedOut ? "TIMED OUT" : passed ? "PASSED" : $"FAILED (exit {JsText(exitNode, hasExit)})";
        var label = JsText(checks, "label");
        var seconds = JsText(checks, "seconds");
        var output = Truthy(checks, "output") ? JsText(checks, "output") : "(no output)";

        return new List<string>
        {
            $"Planwharf ran `{label}` in the project folder just now: {verdict} in {seconds} s. Output tail:",
            "```",
            output,
            "```",
            "This run is fresh evidence: do not run the tests again. Do not edit files. Inspect the project folder (files, git log, git diff) when the results leave a doubt."
        };
    }

    public static bool CleanFinish(JsonArray? results, JsonObject? checks)
    {
        if (results is null || results.Count < 2 || checks is null) return false;
        var exitIsZero = checks.TryGetPropertyValue("exitCode", out var exitNode) && exitNode is JsonValue exitValue && exitValue.GetValueKind() == JsonValueKind.Number && Number(exitValue) == 0;
        if (Truthy(checks, "timedOut") || !exitIsZero) return false;

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
        var name = JsText(options, "name");
        var results = options["results"] as JsonArray;
        var checksNode = options.TryGetPropertyValue("checks", out var cn) ? cn : null;
        var checks = checksNode as JsonObject;
        var isNullChecks = checksNode is null && options.ContainsKey("checks");

        var lines = ResultLines(results);

        var parts = new List<string>
        {
            $"Autonomous review of project {name}. The current MegaTask finished.",
            $"Goal: {(Truthy(options, "goal") ? JsText(options, "goal") : "not recorded")}",
            "Recorded results:"
        };

        if (lines.Count > 0) parts.AddRange(lines);
        else parts.Add("- none recorded");

        if (options.ContainsKey("checks"))
        {
            parts.AddRange(ChecksText(checks, checksNode is null));
        }

        parts.Add("A worker that did part of the goal is not the end of it: if the goal is not met, put all the remaining work in next, so the crew continues.");
        parts.Add(LeadInstructions());
        return string.Join("\n", parts);
    }

    static List<string> ResultLines(JsonArray? results)
    {
        var lines = new List<string>();
        if (results is null) return lines;
        for (var i = Math.Max(0, results.Count - 6); i < results.Count; i++)
        {
            if (results[i] is not JsonObject r) continue;
            var summary = Truthy(r, "summary") ? JsText(r, "summary") : "no summary";
            var files = r["filesChanged"] is JsonArray changed && changed.Count > 0
                ? $" Files changed: {string.Join(", ", changed.Take(20).Select(item => JsText(item, true)))}." : "";
            var blockers = r["blockers"] is JsonArray blocked && blocked.Count > 0
                ? $" Blockers: {string.Join(" | ", blocked.Take(3).Select(item => Clip(JsText(item, true), 300)))}" : "";
            lines.Add($"- {JsText(r, "status")}: {summary}{files}{blockers}");
        }
        return lines;
    }

    static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    // Work that stopped part way goes back to the lead with its results; the lead names the rest.
    public static string ContinuePrompt(JsonObject options)
    {
        var lines = ResultLines(options["results"] as JsonArray);
        var reason = Truthy(options, "reason") ? JsText(options, "reason") : "a step did not finish";
        var parts = new List<string>
        {
            $"Autonomous review of project {JsText(options, "name")}. The current MegaTask stopped before the goal was met ({reason}).",
            $"Goal: {(Truthy(options, "goal") ? JsText(options, "goal") : "not recorded")}",
            "Recorded results:"
        };
        if (lines.Count > 0) parts.AddRange(lines);
        else parts.Add("- none recorded");
        parts.Add("Inspect the project folder (files, git log, git diff) to see what is already done. Do not edit files.");
        parts.Add("Put in next the complete remaining work, naming what is already done so the crew does not redo it. Planwharf prepares it and you split it into parts again, so a step that was too big becomes several smaller ones. If a blocker needs the owner (an approval, a key, a decision), say so in next.");
        parts.Add(LeadInstructions());
        return string.Join("\n", parts);
    }

    const int DOC_LIMIT = 12000, DOCS_LIMIT = 30000;

    // The project documents a goal points to: a path written in the goal (or its file name), or a numbered
    // sheet or job ("sheet 42") matching exactly one numbered Markdown file. Only listed files are returned.
    public static List<string> NamedDocuments(string? goal, IEnumerable<string>? files)
    {
        var text = goal ?? "";
        var list = (files ?? Enumerable.Empty<string>()).Select(file => file.Replace('\\', '/')).ToList();
        var found = new List<string>();
        void Add(string file) { if (!found.Contains(file) && found.Count < 4) found.Add(file); }
        foreach (var file in list)
        {
            var name = file.Split('/')[^1];
            if (text.Contains(file, StringComparison.Ordinal) || (Regex.IsMatch(name, @"\.(md|txt)$", RegexOptions.IgnoreCase) && name.Length > 6 && text.Contains(name, StringComparison.Ordinal))) Add(file);
        }
        foreach (Match match in Regex.Matches(text, @"\b(?:sheet|job|foglio|lavoro)\s+(\d{1,3})\b", RegexOptions.IgnoreCase))
        {
            var pattern = new Regex($@"(^|/)0*{match.Groups[1].Value}-[^/]*\.md$", RegexOptions.IgnoreCase);
            var sheets = list.Where(file => pattern.IsMatch(file)).ToList();
            if (sheets.Count == 1) Add(sheets[0]);
        }
        return found;
    }

    // The text of those documents, clipped, for the split question.
    public static JsonArray ReadDocuments(string folder, IEnumerable<string> names, Func<string, string?> read)
    {
        var room = DOCS_LIMIT;
        var output = new JsonArray();
        foreach (var name in names)
        {
            if (room <= 0) break;
            string? text;
            try { text = read(Path.Combine(folder, name)); } catch { continue; }
            if (string.IsNullOrWhiteSpace(text)) continue;
            var limit = Math.Min(DOC_LIMIT, room);
            output.Add((JsonNode)new JsonObject { ["path"] = name, ["text"] = text.Length > limit ? text[..limit] : text, ["clipped"] = text.Length > limit });
            room -= Math.Min(text.Length, limit);
        }
        return output;
    }

    public static string NudgePrompt(JsonObject options)
    {
        var name = options["name"]?.GetValue<string>() ?? "";
        var alert = options["alert"] as JsonObject ?? new JsonObject();
        var title = alert["title"]?.GetValue<string>() ?? "";
        var message = alert["message"]?.GetValue<string>() ?? "";

        return string.Join("\n",
            $"Supervisor alert for project {name}: {title}. {message}",
            $"Goal: {(Truthy(options, "goal") ? JsText(options, "goal") : "not recorded")}",
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
        parts.Add("You may inspect the project to judge the slices; do not change anything.");
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
            if (TryFinite(m, "left", out var leftVal))
            {
                quota = ", " + JsNumber(leftVal) + "% quota left";
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

        parts.Add($"Your crew can work in parallel: up to {MAX_PARTS} workers at the same time, all in the same project folder. You may give up to {MAX_SPLIT_PARTS} parts: the rest start as earlier ones finish.");
        parts.Add("Size the work by what the goal and the documents it names ask for, not by the length of the goal line: a goal that points to a sheet or plan listing several modules, screens or files is a large job. Read those documents before you decide.");
        parts.Add("Split the work into parts where that saves time or keeps each part small enough for one worker to finish: a worker given too much stops part way. Parts that run at the same time must touch different files: two workers on one file overwrite each other. Only a truly small job (one change in a few files) stays one part.");
        parts.Add("For each part give: title; detail (complete instructions, the worker sees nothing else); files (the files or folders it may change, relative to the project folder; nothing outside them); dependsOn (indexes of earlier parts that must finish first); size (light, normal or heavy); model (one id allowed to build).");
        parts.Add(PlainLines.SplitLineAsk);
        parts.Add("Files that every part would touch (project files such as .csproj or package.json, shared configuration) belong to one last integration part that depends on the others. A part may also change the files of the parts it depends on, once they are finished.");
        parts.Add("Each model has a weight: light, default or premium. Planwharf enforces it: a light part goes to a light model, a normal part to a default or light model, a heavy part to any model; a part given to a heavier model is moved to a lighter one when there is one. Strong models working at the same time drain their company's shared quota fast: keep premium models for heavy parts.");
        parts.Add("Spread the parts over different models and companies: each company has its own quota, and many parts on one company drain it and leave the others idle. Models of the same company are different models too. Planwharf moves a part to a less busy model of the same weight when one model or company has more than its share.");
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

        if (options["documents"] is JsonArray documents)
        {
            foreach (var doc in documents.OfType<JsonObject>())
                parts.Add($"Document the goal names, {JsText(doc, "path")}{(Truthy(doc, "clipped") ? " (start only; read the rest in the project folder)" : "")}:\n<<<\n{JsText(doc, "text")}\n>>>");
        }
        parts.Add("You may read any project file to size the work (the project folder is open to you); do not change anything.");
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

        var summary = parsed.TryGetPropertyValue("summary", out var sNode) && sNode is JsonValue sv && sv.TryGetValue<string>(out var sStr) && !string.IsNullOrWhiteSpace(sStr)
            ? PlainLines.Clip(PlainLines.Sentences(sStr, 2), PlainLines.SummaryMax)
            : "";

        var result = new JsonObject
        {
            ["done"] = done,
            ["next"] = done ? null : (next is null ? null : (JsonNode)JsonValue.Create(next)),
            ["assumptions"] = assumptionsArr
        };
        if (summary.Length > 0) result["summary"] = summary;
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
        if (allowed is null) throw new InvalidOperationException("Cannot read properties of null (reading '0')");

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

    // After the weights, no model and no company takes more than its share of the parts while another
    // model of an allowed weight has room. The lead's choice stays when it is within its share.
    public static JsonObject SpreadParts(JsonArray? parts, JsonArray? builders)
    {
        var list = (builders ?? new JsonArray()).OfType<JsonObject>().Where(model => model["id"] is JsonValue id && id.TryGetValue<string>(out var text) && text.Length > 0).ToList();
        static string Id(JsonObject model) => model["id"]!.GetValue<string>();
        static string CompanyOf(JsonObject model) => model["provider"] is JsonValue provider && provider.TryGetValue<string>(out var text) && text.Length > 0 ? text : Id(model);
        static double Left(JsonObject model) => model["left"] is JsonValue left && left.GetValueKind() == JsonValueKind.Number ? left.GetValue<double>() : 50;
        var modelLoad = new Dictionary<string, int>();
        var companyLoad = new Dictionary<string, int>();
        int Get(Dictionary<string, int> map, string key) => map.TryGetValue(key, out var value) ? value : 0;
        var moved = new JsonArray();
        var outParts = new JsonArray();
        var all = (parts ?? new JsonArray()).OfType<JsonObject>().ToList();
        var total = parts?.Count ?? 0;
        foreach (var part in all)
        {
            var rawSize = part["size"] is JsonValue sizeValue && sizeValue.TryGetValue<string>(out var sizeText) ? sizeText : "";
            var size = SIZES.Contains(rawSize) ? rawSize : "normal";
            var named = part["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var modelText) ? modelText : null;
            var allowed = list.Where(model => model["tier"] is JsonValue tier && tier.TryGetValue<string>(out var tierText) && ALLOWED_TIERS[size].Contains(tierText)).ToList();
            var current = allowed.FirstOrDefault(model => Id(model) == named);
            var chosen = named;
            if (allowed.Count > 1)
            {
                var companies = allowed.Select(CompanyOf).Distinct().Count();
                var modelShare = (int)Math.Ceiling((double)total / allowed.Count);
                var companyShare = (int)Math.Ceiling((double)total / companies);
                bool Fits(JsonObject model) => Get(modelLoad, Id(model)) < modelShare && Get(companyLoad, CompanyOf(model)) < companyShare;
                if (current is null || !Fits(current))
                {
                    var ranked = allowed.OrderBy(model => Get(companyLoad, CompanyOf(model))).ThenBy(model => Get(modelLoad, Id(model))).ThenByDescending(Left).ToList();
                    chosen = Id(ranked.FirstOrDefault(Fits) ?? ranked[0]);
                }
            }
            var key = chosen ?? "undefined";
            modelLoad[key] = Get(modelLoad, key) + 1;
            var picked = list.FirstOrDefault(model => Id(model) == chosen);
            if (picked is not null) companyLoad[CompanyOf(picked)] = Get(companyLoad, CompanyOf(picked)) + 1;
            if (chosen != named)
            {
                var move = new JsonObject { ["title"] = part["title"]?.DeepClone(), ["size"] = size };
                if (part.ContainsKey("model")) move["from"] = part["model"]?.DeepClone();
                move["to"] = chosen;
                move["spread"] = true;
                moved.Add((JsonNode)move);
                var changed = (JsonObject)part.DeepClone();
                changed["model"] = chosen;
                outParts.Add((JsonNode)changed);
            }
            else outParts.Add(part.DeepClone());
        }
        return new JsonObject { ["parts"] = outParts, ["moved"] = moved };
    }

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
                if (pNode is null) throw new InvalidOperationException("Cannot read properties of null (reading 'size')");
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

                        var ranked = options.Select((model, index) => (model, index)).ToList();
                        ranked.Sort((a, b) =>
                        {
                            var aId = a.model["id"]?.GetValue<string>() ?? "";
                            var bId = b.model["id"]?.GetValue<string>() ?? "";
                            load.TryGetValue(aId, out var aLoad);
                            load.TryGetValue(bId, out var bLoad);
                            var diff = aLoad - bLoad;
                            if (diff != 0) return diff;

                            var aFinite = TryFinite(a.model, "left", out var aVal);
                            var bFinite = TryFinite(b.model, "left", out var bVal);
                            var finiteDiff = (aFinite ? 0 : 1) - (bFinite ? 0 : 1);
                            if (finiteDiff != 0) return finiteDiff;
                            if (!aFinite) aVal = 0;
                            if (!bFinite) bVal = 0;
                            var quota = bVal.CompareTo(aVal);
                            return quota != 0 ? quota : a.index.CompareTo(b.index);
                        });

                        chosen = ranked[0].model["id"]!.GetValue<string>();
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
        if (parsed["parts"] is not JsonArray rawParts || rawParts.Count == 0 || rawParts.Count > MAX_SPLIT_PARTS) return null;
        if (!options.TryGetPropertyValue("verifiers", out var verifiersNode))
            throw new InvalidOperationException("Cannot read properties of undefined (reading 'length')");
        if (verifiersNode is null) throw new InvalidOperationException("Cannot read properties of null (reading 'length')");

        // A model the lead may not use, or an unknown size, does not throw the split away.
        var verifierOrder = (verifiersNode as JsonArray)?.Select(item => item?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
        if (verifierOrder.Count == 0) return null;
        var named = parsed["verify"] is JsonValue verifyValue && verifyValue.TryGetValue<string>(out var verifyText) ? verifyText : null;
        var verify = named is not null && verifierIds.Contains(named) ? named : verifierOrder[0];

        var parts = new List<JsonObject>();
        for (var index = 0; index < rawParts.Count; index++)
        {
            if (rawParts[index] is not JsonObject part) return null;
            var title = part["title"]?.GetValue<string>();
            var detail = part["detail"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(detail)) return null;

            var namedModel = part["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var modelText) ? modelText : null;
            var model = namedModel is not null && builderIds.Contains(namedModel) ? namedModel : "";
            var namedSize = part["size"] is JsonValue sizeValue && sizeValue.TryGetValue<string>(out var sizeText) ? sizeText : null;
            var size = namedSize is not null && SIZES.Contains(namedSize) ? namedSize : "normal";

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

            var line = part.TryGetPropertyValue("line", out var lNode) && lNode is JsonValue lv && lv.TryGetValue<string>(out var lStr) && !string.IsNullOrWhiteSpace(lStr)
                ? PlainLines.ShortLine(lStr)
                : "";

            var row = new JsonObject
            {
                ["title"] = title.Trim() is var t && t.Length > 200 ? t.Substring(0, 200) : t,
                ["detail"] = detail.Trim() is var dt && dt.Length > 12000 ? dt.Substring(0, 12000) : dt,
                ["files"] = filesJsonArr,
                ["dependsOn"] = depsJsonArr,
                ["size"] = size,
                ["model"] = model
            };
            if (line.Length > 0) row["line"] = line;
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

    // Number.parseInt(text, 10) || 0. Leading JavaScript whitespace is skipped and trailing junk is ignored.
    static int ParseIntOrZero(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var index = 0;
        while (index < text.Length && IsJsWhitespace(text[index])) index++;
        if (index >= text.Length) return 0;
        var sign = 1;
        if (text[index] == '+') index++;
        else if (text[index] == '-') { sign = -1; index++; }
        if (index >= text.Length || text[index] is < '0' or > '9') return 0;
        long value = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9')
        {
            value = value * 10 + (text[index] - '0');
            if (value > int.MaxValue) return sign < 0 ? int.MinValue : int.MaxValue;
            index++;
        }
        var parsed = sign < 0 ? -value : value;
        return parsed == 0 ? 0 : (int)parsed;
    }

    static bool IsJsWhitespace(char character) => character is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff' || character is >= '\u2000' and <= '\u200a';

    static string? TryString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool TruthyNode(JsonNode? node)
    {
        if (node is null) return false;
        if (node is not JsonValue value) return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(value) is var number && number != 0 && !double.IsNaN(number),
            _ => true
        };
    }

    static bool JsonSame(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is not JsonValue a || right is not JsonValue b || a.GetValueKind() != b.GetValueKind()) return false;
        return a.GetValueKind() switch
        {
            JsonValueKind.String => a.GetValue<string>() == b.GetValue<string>(),
            JsonValueKind.Number => Number(a) == Number(b),
            _ => true
        };
    }

    static bool Includes(JsonObject owner, string key, JsonNode? item, string method)
    {
        if (!owner.TryGetPropertyValue(key, out var node)) throw new InvalidOperationException($"Cannot read properties of undefined (reading '{method}')");
        if (node is null) throw new InvalidOperationException($"Cannot read properties of null (reading '{method}')");
        if (node is JsonArray array) return array.Any(entry => JsonSame(entry, item));
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            return value.GetValue<string>().Contains(item is null ? "null" : JsText(item, true), StringComparison.Ordinal);
        throw new InvalidOperationException($"{JsText(node, true)}.{method} is not a function");
    }

    static bool IncludesMissing(JsonObject owner, string key)
    {
        if (!owner.TryGetPropertyValue(key, out var node)) throw new InvalidOperationException("Cannot read properties of undefined (reading 'includes')");
        if (node is null) throw new InvalidOperationException("Cannot read properties of null (reading 'includes')");
        if (node is JsonArray) return false;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            return value.GetValue<string>().Contains("undefined", StringComparison.Ordinal);
        throw new InvalidOperationException("includes is not a function");
    }

    static int CollectionLength(JsonNode node)
    {
        if (node is JsonArray array) return array.Count;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) return value.GetValue<string>().Length;
        throw new InvalidOperationException("Cannot read properties of " + JsText(node, true) + " (reading 'length')");
    }

    static string JsText(JsonObject owner, string key)
    {
        var present = owner.TryGetPropertyValue(key, out var node);
        return JsText(node, present);
    }

    static string JsText(JsonNode? node, bool present)
    {
        if (!present) return "undefined";
        if (node is null) return "null";
        if (node is JsonValue value)
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => JsNumber(Number(value)),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToJsonString()
            };
        }
        if (node is JsonArray array) return string.Join(",", array.Select(item => item is null ? "" : JsText(item, true)));
        return "[object Object]";
    }

    static bool Truthy(JsonObject owner, string key)
    {
        var present = owner.TryGetPropertyValue(key, out var node);
        if (!present || node is null) return false;
        if (node is not JsonValue value) return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(value) is var number && number != 0 && !double.IsNaN(number),
            _ => true
        };
    }

    static bool TryFinite(JsonObject owner, string key, out double number)
    {
        number = 0;
        if (!owner.TryGetPropertyValue(key, out var node) || node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue<double>(out var parsed)) number = parsed;
        else if (value.TryGetValue<int>(out var integer)) number = integer;
        else if (value.TryGetValue<long>(out var wide)) number = wide;
        else return false;
        return double.IsFinite(number);
    }

    static double Number(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static string JsNumber(double number)
    {
        if (double.IsNaN(number)) return "NaN";
        if (double.IsPositiveInfinity(number)) return "Infinity";
        if (double.IsNegativeInfinity(number)) return "-Infinity";
        if (number == 0) return "0";
        return number == Math.Truncate(number) && Math.Abs(number) < 1e21
            ? number.ToString("0", CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
    }

}
