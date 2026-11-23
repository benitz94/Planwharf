// Port of engine/time-left.cjs.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public record TimeEstimate(int Minutes, string? EndsAt, string Sure, bool LongerThanUsual);

public static class TimeLeft
{
    public const int WorkingFloor = 2;
    public const int HistoryLimit = 500;

    private static readonly Dictionary<string, int> DefaultMinutes = new(StringComparer.Ordinal)
    {
        ["Build"] = 20,
        ["Verify"] = 10,
        ["Review"] = 20,
        ["Research"] = 20
    };

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

    public static string? CanonicalRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var key = role.Trim().ToLowerInvariant();
        if (CanonicalRoles.TryGetValue(key, out var canonical)) return canonical;
        return Roles.Contains(role) ? role : null;
    }

    public static string RoleOf(JsonObject? step)
    {
        if (step == null) return "Build";
        var r = step["role"]?.GetValue<string>();
        var canonical = CanonicalRole(r);
        if (canonical != null) return canonical;
        if (!string.IsNullOrEmpty(r)) return r;

        var type = step["type"]?.GetValue<string>();
        if (type == "AUDIT") return "Review";
        if (type == "RESEARCH") return "Research";

        var title = step["title"]?.GetValue<string>() ?? "";
        if (title.StartsWith("Verify the work independently")) return "Verify";

        return "Build";
    }

    private static string StateOf(string? status)
    {
        var text = (status ?? "").Trim().ToUpperInvariant();
        if (text is "RUNNING" or "WORKING" or "IN_PROGRESS") return "working";
        if (text is "PENDING" or "WAITING" || text.StartsWith("WAITING")) return "waiting";
        if (text is "COMPLETE" or "DONE" or "SUCCEEDED" or "FINISHED") return "done";
        return "other";
    }

    private static double? Median(List<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return (sorted.Count % 2 != 0) ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public static double TypicalMinutes(JsonObject step, IReadOnlyList<JsonObject>? history)
    {
        var role = RoleOf(step);
        var model = step["model"]?.GetValue<string>() ?? step["modelId"]?.GetValue<string>() ?? "";
        var hist = history ?? Array.Empty<JsonObject>();

        var rows = hist.Where(row =>
            row["role"]?.GetValue<string>() == role &&
            double.TryParse(row["minutes"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            double.IsFinite(m)).ToList();

        var sameModel = rows.Where(row => (row["model"]?.GetValue<string>() ?? "") == model).ToList();
        var targetList = sameModel.Count > 0 ? sameModel : rows;

        var vals = targetList.Select(row => double.Parse(row["minutes"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
        var found = Median(vals);
        return found ?? (DefaultMinutes.TryGetValue(role, out var d) ? d : 20.0);
    }

    private static (int Minutes, bool Longer, string Role) StepMinutes(JsonObject step, IReadOnlyList<JsonObject> history, string? now)
    {
        var typical = TypicalMinutes(step, history);
        var role = RoleOf(step);
        if (StateOf(step["status"]?.GetValue<string>()) != "working")
        {
            return (Math.Max(0, (int)Math.Round(typical)), false, role);
        }

        double started = DateTimeOffset.TryParse(step["startedAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sDto)
            ? sDto.ToUnixTimeMilliseconds() : double.NaN;
        double at = DateTimeOffset.TryParse(now, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var aDto)
            ? aDto.ToUnixTimeMilliseconds() : double.NaN;

        double spent = (double.IsFinite(started) && double.IsFinite(at)) ? Math.Max(0, (at - started) / 60000.0) : 0.0;
        if (spent > typical)
        {
            return (WorkingFloor, true, role);
        }
        return (Math.Max(WorkingFloor, (int)Math.Round(typical - spent)), false, role);
    }

    public static TimeEstimate? Estimate(IReadOnlyList<JsonObject>? history, IReadOnlyList<JsonObject>? steps, string? now = null)
    {
        var rows = history ?? Array.Empty<JsonObject>();
        var stepList = (steps ?? Array.Empty<JsonObject>()).Select((s, idx) =>
        {
            var clone = (JsonObject)s.DeepClone();
            if (string.IsNullOrEmpty(clone["id"]?.GetValue<string>()))
                clone["id"] = "step-" + idx;
            return clone;
        }).ToList();

        var byId = stepList.ToDictionary(s => s["id"]!.GetValue<string>(), s => s, StringComparer.Ordinal);
        var remaining = stepList.Where(s => StateOf(s["status"]?.GetValue<string>()) is "working" or "waiting").ToList();
        if (remaining.Count == 0) return null;

        var each = remaining.ToDictionary(s => s["id"]!.GetValue<string>(), s => StepMinutes(s, rows, now), StringComparer.Ordinal);
        var memo = new Dictionary<string, int>(StringComparer.Ordinal);

        int Finish(JsonObject step, HashSet<string> stack)
        {
            var id = step["id"]!.GetValue<string>();
            if (memo.TryGetValue(id, out var mVal)) return mVal;
            if (stack.Contains(id)) return each[id].Minutes;

            stack.Add(id);
            int wait = 0;
            if (StateOf(step["status"]?.GetValue<string>()) != "working")
            {
                if (step["dependencies"] is JsonArray deps)
                {
                    foreach (var depNode in deps)
                    {
                        var depId = depNode?.GetValue<string>();
                        if (!string.IsNullOrEmpty(depId) && byId.TryGetValue(depId, out var dep) && depId != id && each.ContainsKey(depId))
                        {
                            wait = Math.Max(wait, Finish(dep, stack));
                        }
                    }
                }
            }
            stack.Remove(id);

            int val = wait + each[id].Minutes;
            memo[id] = val;
            return val;
        }

        int minutes = remaining.Max(s => Finish(s, new HashSet<string>(StringComparer.Ordinal)));
        var rolesUsed = each.Values.Select(v => v.Role).Distinct(StringComparer.Ordinal).ToList();
        var counts = rolesUsed.Select(r => rows.Count(h => h["role"]?.GetValue<string>() == r &&
            double.TryParse(h["minutes"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            double.IsFinite(m))).ToList();

        string sure = counts.Any(c => c < 5) ? "rough" : $"based on {counts.Sum()} past steps";

        string? endsAt = null;
        if (DateTimeOffset.TryParse(now, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var nowDto))
        {
            endsAt = nowDto.AddMinutes(minutes).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        bool longerThanUsual = each.Values.Any(v => v.Longer);
        return new TimeEstimate(minutes, endsAt, sure, longerThanUsual);
    }

    public static JsonObject? StepRecord(JsonObject? input)
    {
        if (input == null) return null;
        if (input["modelChange"]?.GetValue<bool>() == true) return null;

        var startedAt = input["startedAt"]?.GetValue<string>();
        var endedAt = input["endedAt"]?.GetValue<string>();
        if (!DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sDto) ||
            !DateTimeOffset.TryParse(endedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var eDto))
            return null;

        var r = input["role"]?.GetValue<string>();
        var name = CanonicalRole(r) ?? (Roles.Contains(r ?? "") ? r : RoleOf(new JsonObject { ["role"] = r, ["title"] = "", ["type"] = "" }));
        if (name == null || !Roles.Contains(name)) return null;

        double filesNum = 0;
        if (double.TryParse(input["files"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fVal) && double.IsFinite(fVal))
            filesNum = Math.Max(0, Math.Round(fVal));

        double minutes = Math.Max(0, Math.Round((eDto.ToUnixTimeMilliseconds() - sDto.ToUnixTimeMilliseconds()) / 6000.0) / 10.0);
        var status = input["status"]?.GetValue<string>();
        var outcome = (status is "COMPLETE" or "done") ? "done" : "failed";

        var line = new JsonObject
        {
            ["role"] = name,
            ["model"] = input["model"]?.GetValue<string>() ?? "",
            ["files"] = filesNum,
            ["minutes"] = minutes,
            ["outcome"] = outcome
        };

        if (input.ContainsKey("size") && input["size"] != null)
        {
            if (double.TryParse(input["size"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sVal) && double.IsFinite(sVal))
                line["size"] = sVal;
            else
            {
                var sStr = input["size"]!.ToString();
                line["size"] = sStr.Length > 40 ? sStr[..40] : sStr;
            }
        }

        return line;
    }

    private static JsonObject? KeepLine(JsonObject? record)
    {
        if (record == null) return null;
        var role = CanonicalRole(record["role"]?.GetValue<string>());
        if (role == null || !Roles.Contains(role)) return null;

        if (!double.TryParse(record["minutes"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
            !double.IsFinite(minutes) || minutes < 0)
            return null;

        double filesNum = 0;
        if (double.TryParse(record["files"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fVal) && double.IsFinite(fVal))
            filesNum = Math.Max(0, Math.Round(fVal));

        var outcome = record["outcome"]?.GetValue<string>() == "done" ? "done" : "failed";

        var line = new JsonObject
        {
            ["role"] = role,
            ["model"] = record["model"]?.GetValue<string>() ?? "",
            ["files"] = filesNum,
            ["minutes"] = minutes,
            ["outcome"] = outcome
        };

        if (record.ContainsKey("size") && record["size"] != null)
        {
            if (double.TryParse(record["size"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sVal) && double.IsFinite(sVal))
                line["size"] = sVal;
            else
            {
                var sStr = record["size"]!.ToString();
                line["size"] = sStr.Length > 40 ? sStr[..40] : sStr;
            }
        }

        return line;
    }

    public static IReadOnlyList<JsonObject> ParseHistory(string? text)
    {
        var rows = new List<JsonObject>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            try
            {
                var parsed = JsonNode.Parse(trimmed) as JsonObject;
                if (parsed == null) continue;
                var clean = KeepLine(parsed);
                if (clean != null) rows.Add(clean);
            }
            catch { }
        }

        if (rows.Count > HistoryLimit)
            return rows.Skip(rows.Count - HistoryLimit).ToList();
        return rows;
    }

    public static string HistoryText(IReadOnlyList<JsonObject>? records)
    {
        var list = (records ?? Array.Empty<JsonObject>())
            .Select(KeepLine)
            .Where(r => r != null)
            .Cast<JsonObject>()
            .ToList();

        if (list.Count > HistoryLimit)
            list = list.Skip(list.Count - HistoryLimit).ToList();

        if (list.Count == 0) return "";
        return string.Join("\n", list.Select(r => r.ToJsonString())) + "\n";
    }
}
