using System.Globalization;
using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record AlertView(string Id, string Title, string Message);
public sealed record RepoView(bool Git, int? Uncommitted, int Ahead, string? Error);

// The parts of the work card that the chat page builds from the snapshot: summary, usage, activity, alerts, git.
// Nothing is estimated: a value Planwharf did not record is said to be not reported.
public static class WorkDetails
{
    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static double? N(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";
    static string RoleOf(JsonObject sub) => S(sub["type"]) == "AUDIT" ? "Review" : S(sub["type"]) == "RESEARCH" ? "Research" : S(sub["title"]).StartsWith("Verify the work independently") ? "Verify" : "Build";

    // As the chat page: 12 s, 4 min, 1.5 h.
    public static string Duration(double? ms) => ms is not { } value || !double.IsFinite(value) || value < 0 ? ""
        : value < 60000 ? $"{Math.Max(1, Math.Round(value / 1000))} s" : value < 3600000 ? $"{Math.Round(value / 60000)} min" : (value / 3600000).ToString("0.0", CultureInfo.InvariantCulture) + " h";

    public static double? Between(string from, string to) =>
        DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) && DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) ? (b - a).TotalMilliseconds : null;

    // The same lines as work-summary.js, which the web and desktop chat use.
    public static IReadOnlyList<string> Summary(JsonObject row, string? stopReason, int? changedFiles, Func<string, string> modelLabel)
    {
        var subs = (row["subTasks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var results = (row["results"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var done = subs.Count(sub => S(sub["status"]) == "COMPLETE");
        var all = subs.Count > 0 && done == subs.Count;
        var reason = (stopReason ?? "").Split(':')[0];
        var lines = new List<string>();
        if (all && reason == "GOAL_REACHED") lines.Add($"All {Plural(subs.Count, "step")} finished and the lead confirmed the goal.");
        else if (all && reason is "LEAD_UNCONFIRMED" or "UNREADABLE_LEAD_DECISION") lines.Add($"All {Plural(subs.Count, "step")} finished, but the lead did not confirm the goal: check the result before you rely on it.");
        else if (all) lines.Add($"All {Plural(subs.Count, "step")} finished.");
        else lines.Add($"{done} of {Plural(subs.Count, "step")} finished.");
        foreach (var sub in subs)
        {
            var from = S(sub["takeover"]?["fromModelId"]) is { Length: > 0 } f ? f : S(sub["fallbackFrom"]);
            var assigned = S(sub["assignedModelId"]);
            var who = (assigned.Length > 0 ? modelLabel(assigned) : "") is { Length: > 0 } label ? label : assigned.Length > 0 ? assigned : "not assigned";
            lines.Add($"{RoleOf(sub)}: {who}{(from.Length > 0 && from != assigned ? $", taking over from {(modelLabel(from) is { Length: > 0 } fl ? fl : from)}" : "")}.");
        }
        var problems = results.Where(result => S(result["status"]) is "FAILED" or "BLOCKED" && result["modelChange"]?.GetValue<bool>() != true).ToList();
        if (problems.Count > 0)
            foreach (var result in problems)
            {
                var sub = subs.FirstOrDefault(item => S(item["id"]) == S(result["subTaskId"]));
                var said = S(result["summary"]).Trim();
                lines.Add($"Problem in {(sub != null ? RoleOf(sub) : "a step")}: {(said.Length > 0 ? said : "no details recorded")}");
            }
        else lines.Add("No problems were reported.");
        var tests = (int)results.Sum(result => N(result["testsReported"]) ?? 0);
        if (tests != 0) lines.Add($"Workers reported {Plural(tests, "test run")}.");
        if (changedFiles > 0) lines.Add($"{Plural(changedFiles.Value, "file")} changed and not yet committed: review them, then commit below.");
        else if (changedFiles == 0) lines.Add("No files are left uncommitted.");
        return lines;
    }

    // What the work used, per model: time, tokens and quota before -> after.
    public static IReadOnlyList<(string Model, string Text)> Usage(JsonNode snapshot, JsonObject row, IReadOnlyDictionary<string, int?> quotaAtStart, Func<string, string> modelLabel, Func<string, int?> quotaLeft)
    {
        var megaId = S(row["megaTask"]?["id"]);
        var records = (snapshot["aiUsage"]?["recent"] as JsonArray ?? []).OfType<JsonObject>().Where(item => S(item["megaTaskId"]) == megaId).ToList();
        var ids = (row["subTasks"] as JsonArray ?? []).OfType<JsonObject>().Select(sub => S(sub["assignedModelId"]))
            .Concat(records.Select(item => S(item["modelId"]))).Where(id => id.Length > 0).Distinct().ToList();
        string Num(double value) => value.ToString("#,0", CultureInfo.InvariantCulture);
        return ids.Select(id =>
        {
            var mine = records.Where(item => S(item["modelId"]) == id).ToList();
            var ms = mine.Sum(item => N(item["durationMs"]) ?? 0);
            var tokens = mine.Select(item => item["tokens"] as JsonObject).Where(item => item != null).Cast<JsonObject>().ToList();
            double? Add(string field) => tokens.Count > 0 && tokens.All(item => N(item[field]) != null) ? tokens.Sum(item => N(item[field])!.Value) : null;
            var (input, output, cached) = (Add("input"), Add("output"), Add("cachedInput"));
            var now = quotaLeft(id);
            var before = quotaAtStart.TryGetValue(id, out var start) ? start : null;
            var quota = before != null && now != null ? $"{before}% → {now}%" : now != null ? $"now {now}% left" : "not reported";
            var tokenText = input == null && output == null ? "tokens not reported"
                : string.Join(" · ", new[] { input != null ? Num(input.Value) + " in" : null, output != null ? Num(output.Value) + " out" : null, cached != null ? Num(cached.Value) + " from cache" : null }.Where(part => part != null));
            var label = modelLabel(id) is { Length: > 0 } l ? l : id;
            return (label, $"{(ms > 0 ? Duration(ms) : "time not reported")} · {tokenText} · quota {quota}");
        }).ToList();
    }

    // What the model at work is doing, in plain words; the raw log only when nothing else is readable.
    public static (string Who, string Text) Activity(JsonObject row, Func<string, string> modelLabel)
    {
        var running = (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(part => part["alive"]?.GetValue<bool>() == true);
        var friendly = new List<(string Text, int Count)>();
        foreach (var text in (running?["activitySession"]?["events"] as JsonArray ?? []).OfType<JsonObject>().Select(e => S(e["friendly"])).Where(t => t.Length > 0 && !t.StartsWith("Unclassified worker activity")))
            if (friendly.Count > 0 && friendly[^1].Text == text) friendly[^1] = (text, friendly[^1].Count + 1); else friendly.Add((text, 1));
        var lines = friendly.Select(item => item.Count > 1 ? $"{item.Text} (x{item.Count})" : item.Text).TakeLast(6).ToList();
        var text2 = lines.Count > 0 ? string.Join("\n", lines) : string.Join("\n", S(row["workerTail"]).Trim().Split('\n').TakeLast(6));
        var who = modelLabel(S(running?["modelId"])) is { Length: > 0 } label ? label : S(row["workerLabel"]) is { Length: > 0 } worker ? worker : "the crew";
        return (who, text2.Trim());
    }

    public static IReadOnlyList<AlertView> Alerts(JsonNode snapshot, string projectId) =>
        ((snapshot["supervisor"]?["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(entry => S(entry["projectId"]) == projectId)?["alerts"] as JsonArray ?? [])
        .OfType<JsonObject>().Where(alert => alert["active"]?.GetValue<bool>() != false && alert["ignored"]?.GetValue<bool>() != true)
        .Select(alert => new AlertView(S(alert["id"]), S(alert["title"]), S(alert["message"]))).ToList();

    public static RepoView Repo(JsonNode? repository)
    {
        var local = repository?["local"];
        if (local?["git"]?.GetValue<bool>() != true) return new RepoView(false, null, 0, null);
        var uncommitted = N(local["uncommitted"]) is { } count ? (int)count : (local["changes"] as JsonArray)?.Count ?? 0;
        return new RepoView(true, uncommitted, (int)(N(repository?["comparison"]?["ahead"]) ?? 0), null);
    }
}
