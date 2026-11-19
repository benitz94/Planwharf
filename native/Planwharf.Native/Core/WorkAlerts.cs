using System.Text.Json.Nodes;
using Planwharf.Engine;

namespace Planwharf.Native.Core;

public sealed record AlertOptions(bool Notification = true, bool Flash = true, bool Sound = true)
{
    public static AlertOptions Read(JsonNode? saved) => new(Flag(saved?["alertNotification"]), Flag(saved?["alertFlash"]), Flag(saved?["alertSound"]));

    static bool Flag(JsonNode? node) => node is not JsonValue v || !v.TryGetValue<bool>(out var flag) || flag;
}

public sealed record WorkAlert(string Goal, bool Done, AlertOptions Options)
{
    public string Message => $"{Goal} {(Done ? Strings.T("is done") : Strings.T("needs you"))}";
}

public static class WorkAlerts
{
    static string S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";

    // Compare whole works, never steps. The first snapshot establishes a baseline without replaying old work.
    public static IReadOnlyList<WorkAlert> Decide(JsonNode? previous, JsonNode current, AlertOptions options)
    {
        if (previous == null) return [];
        var alerts = new List<WorkAlert>();
        foreach (var row in (current["projects"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = S(row["id"]);
            var old = (previous["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => S(p["id"]) == id);
            if (old == null) continue;
            var outcome = Outcome(current, row);
            if (outcome.Length == 0) continue;
            if (S(old["megaTask"]?["id"]) == S(row["megaTask"]?["id"]) && Outcome(previous, old) == outcome) continue;
            var goal = S(row["megaTask"]?["title"]);
            if (goal.Length == 0) goal = S(row["goal"]);
            if (goal.Length == 0) goal = S(row["name"]);
            alerts.Add(new WorkAlert(goal, outcome == "done", options));
        }
        return alerts;
    }

    static string Outcome(JsonNode snapshot, JsonObject row)
    {
        var stop = S(snapshot["autonomy"]?[S(row["id"])]?["stopReason"]);
        if (stop == "GOAL_REACHED") return "done";
        if (stop.Length > 0) return "needs you";
        var status = S(row["megaTask"]?["status"]).ToUpperInvariant();
        if (status == "COMPLETE") return "done";
        if (status is "FAILED" or "BLOCKED" or "PAUSED" or "READY_FOR_APPROVAL") return "needs you";
        return "";
    }
}
