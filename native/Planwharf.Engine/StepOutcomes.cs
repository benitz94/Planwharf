// Port of engine/step-outcomes.cjs.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class StepOutcomes
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "Build", "Verify", "Review", "Research"
    };

    private static readonly Dictionary<string, string> CanonicalRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["build"] = "Build",
        ["builder"] = "Build",
        ["verify"] = "Verify",
        ["verifier"] = "Verify",
        ["review"] = "Review",
        ["reviewer"] = "Review",
        ["audit"] = "Review",
        ["research"] = "Research",
        ["researcher"] = "Research"
    };

    private static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
    {
        "flagship", "mid", "light"
    };

    public static string CanonicalRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "Build";
        var key = role.Trim().ToLowerInvariant();
        if (CanonicalRoles.TryGetValue(key, out var canonical)) return canonical;
        return Roles.Contains(role) ? role : "Build";
    }

    public static string CanonicalClass(string? cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return "mid";
        var key = cls.Trim().ToLowerInvariant();
        if (key is "premium" or "flagship") return "flagship";
        if (key is "default" or "mid") return "mid";
        if (key is "light") return "light";
        return Classes.Contains(cls) ? cls : "mid";
    }

    public static (string Company, string Class) ResolveModelMeta(JsonNode? model)
    {
        if (model is JsonObject obj)
        {
            var company = obj["companyId"]?.GetValue<string>() ??
                          obj["company"]?.GetValue<string>() ??
                          obj["providerId"]?.GetValue<string>() ??
                          obj["provider"]?.GetValue<string>() ?? "unknown";

            var cls = obj["class"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cls) || cls == "mid")
            {
                var id = (obj["id"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>() ?? obj["cli"]?.GetValue<string>() ?? "").ToLowerInvariant();
                if (id.Contains("opus") || id.Contains("luna") || id.Contains("business") || id.Contains("pro")) cls = "flagship";
                else if (id.Contains("haiku") || id.Contains("mini") || id.Contains("flash-lite") || id.Contains("sol")) cls = "light";
            }
            return (company, CanonicalClass(cls));
        }

        var idStr = (model?.ToString() ?? "").ToLowerInvariant();
        string comp = "unknown";
        if (idStr.Contains("claude") || idStr.Contains("sonnet") || idStr.Contains("opus") || idStr.Contains("haiku") || idStr.Contains("fable")) comp = "claude";
        else if (idStr.Contains("gpt") || idStr.Contains("codex") || idStr.Contains("luna") || idStr.Contains("sol") || idStr.Contains("astra")) comp = "codex";
        else if (idStr.Contains("grok")) comp = "grok";
        else if (idStr.Contains("gemini") || idStr.Contains("antigravity")) comp = "antigravity";

        string c = "mid";
        if (idStr.Contains("opus") || idStr.Contains("luna") || idStr.Contains("business") || idStr.Contains("pro")) c = "flagship";
        else if (idStr.Contains("haiku") || idStr.Contains("mini") || idStr.Contains("flash-lite") || idStr.Contains("sol")) c = "light";

        return (comp, CanonicalClass(c));
    }

    public static string? StripPathsAndCode(string? value)
    {
        if (value == null) return null;
        var text = value;
        text = Regex.Replace(text, @"```[\s\S]*?```", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"`[^`]*`", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\{[^{}]*\}", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"[A-Za-z]:[\\/][^\s'""]+", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\\\\[^\s'""]+", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"(?:^|\s)\/[^\s'""]+", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"(?:^|\s)(?:\.{1,2}[\\/]|[a-zA-Z0-9_.-]+[\\/])[^\s'""]+", "", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"(?:^|\s)[a-zA-Z0-9_.-]+\.(?:cjs|mjs|js|jsx|ts|tsx|cs|py|rb|go|rs|cpp|c|h|java|php|sh|bat|cmd|ps1|json|ya?ml|toml|xml|html?|css|sql)(?=[\s.,:;!?]|$)", "", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static JsonObject? StepOutcomeRecord(JsonObject? input)
    {
        if (input == null) return null;

        string modelId = "";
        var modelNode = input["model"];
        if (modelNode is JsonObject mObj)
        {
            modelId = (mObj["id"]?.GetValue<string>() ?? mObj["name"]?.GetValue<string>() ?? mObj["cli"]?.GetValue<string>() ?? "").Trim();
        }
        else if (modelNode != null)
        {
            modelId = modelNode.ToString().Trim();
        }

        if (string.IsNullOrEmpty(modelId) || modelId == "[object Object]") return null;

        var resolvedRole = CanonicalRole(input["role"]?.GetValue<string>());
        var inferred = ResolveModelMeta(modelNode);
        var inputCls = input["class"]?.GetValue<string>();
        var resolvedClass = !string.IsNullOrEmpty(inputCls) ? CanonicalClass(inputCls) : inferred.Class;
        var inputCompany = input["company"]?.GetValue<string>()?.Trim();
        var resolvedCompany = !string.IsNullOrEmpty(inputCompany) ? inputCompany : inferred.Company;

        var rawKind = input["kind"]?.GetValue<string>() ?? "implementation";
        var resolvedKind = StripPathsAndCode(rawKind.Trim());
        if (string.IsNullOrEmpty(resolvedKind)) resolvedKind = "implementation";

        JsonNode? resolvedSize = "normal";
        if (input.ContainsKey("size") && input["size"] != null)
        {
            if (double.TryParse(input["size"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sVal) && double.IsFinite(sVal))
                resolvedSize = Math.Max(0, sVal);
            else
            {
                var sStr = input["size"]!.ToString();
                if (sStr.Length > 40) sStr = sStr[..40];
                var stripped = StripPathsAndCode(sStr);
                resolvedSize = !string.IsNullOrEmpty(stripped) ? stripped : "normal";
            }
        }

        bool isSwitch = input["switch"]?.GetValue<bool>() == true || input["modelChange"]?.GetValue<bool>() == true;
        string? refusalReason = null;
        if (input["refusal"] != null)
        {
            var rStr = input["refusal"]!.ToString();
            if (rStr.Length > 200) rStr = rStr[..200];
            var stripped = StripPathsAndCode(rStr);
            refusalReason = !string.IsNullOrEmpty(stripped) ? stripped : null;
        }

        bool isAccepted = false;
        int fixedCount = 0;
        if (!isSwitch && refusalReason == null)
        {
            if (input["accepted"] is JsonValue aVal && aVal.TryGetValue<bool>(out var bVal))
            {
                isAccepted = bVal;
                if (double.TryParse(input["fixed"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fVal) && double.IsFinite(fVal))
                    fixedCount = Math.Max(0, (int)Math.Round(fVal));
                else
                    fixedCount = isAccepted ? 0 : 1;
            }
            else if (double.TryParse(input["fixed"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fVal) && double.IsFinite(fVal))
            {
                fixedCount = Math.Max(0, (int)Math.Round(fVal));
                isAccepted = fixedCount == 0;
            }
            else
            {
                isAccepted = true;
                fixedCount = 0;
            }
        }

        int reported = 0;
        int passed = 0;
        if (input["tests"] is JsonArray tArr)
        {
            reported = tArr.Count;
            passed = tArr.Count(t => t?["status"]?.GetValue<string>() is "PASSED" or "passed");
        }
        else if (input["tests"] is JsonObject tObj)
        {
            if (double.TryParse(tObj["reported"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rVal) && double.IsFinite(rVal))
                reported = Math.Max(0, (int)Math.Round(rVal));
            if (double.TryParse(tObj["passed"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pVal) && double.IsFinite(pVal))
                passed = Math.Max(0, (int)Math.Round(pVal));
        }
        else
        {
            if (double.TryParse(input["testsReported"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rVal) && double.IsFinite(rVal))
                reported = Math.Max(0, (int)Math.Round(rVal));
            if (double.TryParse(input["testsPassed"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pVal) && double.IsFinite(pVal))
                passed = Math.Max(0, (int)Math.Round(pVal));
        }
        reported = Math.Max(reported, passed);

        long? duration = null;
        if (double.TryParse(input["durationMs"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dVal) && double.IsFinite(dVal))
        {
            duration = Math.Max(0, (long)Math.Round(dVal));
        }
        else if (DateTimeOffset.TryParse(input["startedAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sDto) &&
                 DateTimeOffset.TryParse(input["endedAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var eDto))
        {
            duration = Math.Max(0, eDto.ToUnixTimeMilliseconds() - sDto.ToUnixTimeMilliseconds());
        }

        JsonObject? quotaInfo = null;
        if (input["quota"] is JsonObject qObj)
        {
            double? inTokens = null, outTokens = null;
            var inNode = qObj["inputTokens"] ?? qObj["input"];
            var outNode = qObj["outputTokens"] ?? qObj["output"];
            if (double.TryParse(inNode?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var inV) && double.IsFinite(inV))
                inTokens = Math.Max(0, Math.Round(inV));
            if (double.TryParse(outNode?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var outV) && double.IsFinite(outV))
                outTokens = Math.Max(0, Math.Round(outV));

            if (inTokens != null || outTokens != null)
            {
                quotaInfo = new JsonObject
                {
                    ["inputTokens"] = inTokens != null ? JsonValue.Create(inTokens.Value) : null,
                    ["outputTokens"] = outTokens != null ? JsonValue.Create(outTokens.Value) : null
                };
                if (double.TryParse(qObj["cachedInput"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cV) && double.IsFinite(cV))
                    quotaInfo["cachedInput"] = Math.Max(0, Math.Round(cV));
            }
        }

        var testsNode = new JsonObject
        {
            ["reported"] = reported,
            ["passed"] = passed
        };

        var record = new JsonObject
        {
            ["model"] = modelId,
            ["company"] = resolvedCompany,
            ["class"] = resolvedClass,
            ["role"] = resolvedRole,
            ["kind"] = resolvedKind,
            ["size"] = resolvedSize?.DeepClone(),
            ["accepted"] = isAccepted,
            ["fixed"] = fixedCount,
            ["fixedByVerify"] = fixedCount > 0,
            ["testsReported"] = reported,
            ["testsPassed"] = passed,
            ["tests"] = testsNode,
            ["refusal"] = refusalReason,
            ["switch"] = isSwitch,
            ["durationMs"] = duration != null ? JsonValue.Create(duration.Value) : null,
            ["quota"] = quotaInfo?.DeepClone()
        };

        return record;
    }

    public static string OutcomesFile(string dir) => Path.Combine(dir, "step-outcomes.jsonl");

    public static JsonObject? AppendStepOutcome(string dir, JsonObject record)
    {
        if (string.IsNullOrEmpty(dir) || record == null) return null;
        var clean = StepOutcomeRecord(record);
        if (clean == null) return null;
        try
        {
            Directory.CreateDirectory(dir);
            var file = OutcomesFile(dir);
            var bytes = Encoding.UTF8.GetBytes(clean.ToJsonString() + "\n");
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.None);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                    return clean;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (Environment.TickCount64 >= deadline) return null;
                    Thread.Sleep(15);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<JsonObject> ReadStepOutcomes(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return Array.Empty<JsonObject>();
        var file = OutcomesFile(dir);
        if (!File.Exists(file)) return Array.Empty<JsonObject>();

        var list = new List<JsonObject>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? raw;
            while ((raw = reader.ReadLine()) != null)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    var parsed = JsonNode.Parse(line) as JsonObject;
                    var clean = StepOutcomeRecord(parsed);
                    if (clean != null) list.Add(clean);
                }
                catch { }
            }
        }
        catch
        {
            return Array.Empty<JsonObject>();
        }
        return list;
    }

    public static IReadOnlyList<JsonObject> OutcomesSummary(IReadOnlyList<JsonObject>? records)
    {
        var map = new Dictionary<string, (int Steps, int Accepted, int Fixed)>(StringComparer.Ordinal);
        foreach (var item in records ?? Array.Empty<JsonObject>())
        {
            if (item == null) continue;
            var id = item["model"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            var current = map.TryGetValue(id, out var c) ? c : (0, 0, 0);
            current.Item1++;
            if (item["accepted"]?.GetValue<bool>() == true) current.Item2++;
            if (item["fixedByVerify"]?.GetValue<bool>() == true || (item["fixed"]?.GetValue<int>() ?? 0) > 0) current.Item3++;
            map[id] = current;
        }

        return map.Select(pair => new JsonObject
        {
            ["model"] = pair.Key,
            ["steps"] = pair.Value.Steps,
            ["accepted"] = pair.Value.Accepted,
            ["fixed"] = pair.Value.Fixed
        }).ToList();
    }

    public static string SummaryText(JsonObject? row)
    {
        if (row == null) return "";
        var steps = row["steps"]?.GetValue<int>() ?? 0;
        var accepted = row["accepted"]?.GetValue<int>() ?? 0;
        var fixedCount = row["fixed"]?.GetValue<int>() ?? 0;
        return $"{steps} steps \u00b7 {accepted} accepted as they were \u00b7 {fixedCount} fixed by Verify";
    }
}
