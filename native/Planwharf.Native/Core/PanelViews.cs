using System.Globalization;
using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record ProgressStep(string Status, string Title, string Model);
public sealed record ProgressPanel(string Status, string Next, IReadOnlyList<ProgressStep> Steps, IReadOnlyList<(string At, string Text)> Recent, string Folder);
public sealed record QuotaWindow(string Name, int LeftPct, string Resets);
public sealed record QuotaPlan(string Label, string? Note, IReadOnlyList<QuotaWindow> Windows);
public sealed record UsagePanel(IReadOnlyList<QuotaPlan> Plans, IReadOnlyList<(string Model, string Text)> Lead, IReadOnlyList<(string Model, string Text)> Crew);
public sealed record ChangedFile(string Kind, string Path);
public sealed record FilesPanel(string? Message, string Branch, IReadOnlyList<ChangedFile> Files, int Total, string CheckedAt);

// The Progress, Usage and Files panels, read from the snapshot the way the chat page reads them.
public static class PanelViews
{
    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static double? N(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static string Clock(string at, TimeZoneInfo? zone = null) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? TimeZoneInfo.ConvertTime(when, zone ?? TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture) : "";

    public static ProgressPanel Progress(JsonNode snapshot, string projectId, string status, TimeZoneInfo? zone = null)
    {
        var row = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => S(item["id"]) == projectId);
        var labels = (snapshot["models"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(model => S(model["id"]), model => S(model["label"]));
        string Label(string id) => labels.TryGetValue(id, out var label) && label.Length > 0 ? label : id.Length > 0 ? id : "not assigned";
        var steps = (row?["subTasks"] as JsonArray ?? row?["megaTask"]?["subTasks"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(sub => new ProgressStep(S(sub["status"]) is { Length: > 0 } s ? s.ToLowerInvariant() : "planned", S(sub["title"]) is var t && t.Length > 140 ? t[..140] : t,
                Label(S(sub["assignedModelId"]) is { Length: > 0 } a ? a : S(sub["modelId"])))).ToList();
        var recent = (snapshot["execution"]?["timeline"] as JsonArray ?? []).OfType<JsonObject>().Where(e => S(e["projectId"]) == projectId)
            .TakeLast(15).Reverse().Select(e => (Clock(S(e["createdAt"]), zone), S(e["summary"]) is var text && text.Length > 200 ? text[..200] : text)).ToList();
        return new ProgressPanel(status, S(row?["next"]), steps, recent, S(row?["folder"]));
    }

    static string WindowName(string name) => name switch
    {
        "5-hour" => "5 hours", "weekly" => "Week", "" => "Limit",
        _ => char.ToUpperInvariant(name.Replace('_', ' ').Replace('-', ' ')[0]) + name.Replace('_', ' ').Replace('-', ' ')[1..]
    };

    static string Resets(string at, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) || when <= now) return "";
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var sameDay = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date;
        return "resets " + (sameDay ? "" : local.ToString("ddd d MMM ", CultureInfo.InvariantCulture)) + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public static UsagePanel Usage(JsonNode snapshot, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var tz = zone ?? TimeZoneInfo.Local;
        var plans = (snapshot["plans"] as JsonArray ?? []).OfType<JsonObject>().Select(plan =>
        {
            var windows = (plan["windows"] as JsonArray ?? []).OfType<JsonObject>().Where(w => N(w["leftPct"]) != null)
                .Select(w => new QuotaWindow(WindowName(S(w["name"])), (int)Math.Round(N(w["leftPct"])!.Value), Resets(S(w["resetAt"]), at, tz))).ToList();
            var label = S(plan["label"]) is { Length: > 0 } l ? l : S(plan["id"]);
            if (!B(plan["readable"]) || windows.Count == 0 && N(plan["leftPct"]) == null)
                return new QuotaPlan(label, S(plan["reason"]) is { Length: > 0 } reason ? reason : "Not reported by this provider", []);
            if (windows.Count == 0) windows.Add(new QuotaWindow("", (int)Math.Round(N(plan["leftPct"])!.Value), ""));
            return new QuotaPlan(label, null, windows);
        }).ToList();
        static string Time(double? ms) => ms is not { } value ? "—" : value >= 60000 ? $"{Math.Round(value / 60000)} min" : $"{Math.Round(value / 1000)} s";
        IReadOnlyList<(string, string)> Rows(string key) => (snapshot["aiUsage"]?[key] as JsonArray ?? []).OfType<JsonObject>()
            .Select(row => (S(row["model"]), $"{N(row["calls"]) ?? 0} · {Time(N(row["durationMs"]))} · {(N(row["outputTokens"]) is { } tokens ? tokens.ToString("#,0", CultureInfo.InvariantCulture) + " out" : "not reported")}")).ToList();
        return new UsagePanel(plans, Rows("orchestrator"), Rows("workers"));
    }

    static readonly Dictionary<string, string> Kinds = new() { ["M"] = "changed", ["A"] = "added", ["D"] = "deleted", ["R"] = "renamed", ["??"] = "new", ["U"] = "conflict" };

    public static FilesPanel Files(JsonNode? repository, string folder, string? error, TimeZoneInfo? zone = null)
    {
        if (folder.Length == 0) return new FilesPanel("This project has no folder yet. Set it in Settings.", "", [], 0, "");
        if (error != null) return new FilesPanel(error, "", [], 0, "");
        var local = repository?["local"];
        if (repository == null) return new FilesPanel("Reading the folder…", "", [], 0, "");
        if (!B(local?["exists"])) return new FilesPanel($"The folder {folder} does not exist.", "", [], 0, "");
        if (!B(local?["git"])) return new FilesPanel("This folder is not tracked by Git, so Planwharf cannot list what changed.", "", [], 0, "");
        var files = (local!["changes"] as JsonArray ?? []).Select(line => S(line)).Where(line => line.Length > 0).Select(line =>
        {
            var code = line.Length >= 2 ? line[..2].Trim() : line.Trim();
            if (code.Length == 0) code = "?";
            var kind = Kinds.TryGetValue(code, out var k) ? k : Kinds.TryGetValue(code[..1], out var first) ? first : "changed";
            return new ChangedFile(kind, line.Length > 3 ? line[3..] : "");
        }).ToList();
        return new FilesPanel(null, S(local["branch"]), files, N(local["uncommitted"]) is { } total ? (int)total : files.Count, Clock(S(repository["checkedAt"]), zone));
    }
}
