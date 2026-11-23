using Planwharf.Engine;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record ProgressStep(string Status, string Title, string Model);
public sealed record ProgressPanel(string Status, string Next, IReadOnlyList<ProgressStep> Steps, IReadOnlyList<(string At, string Text)> Recent, string Folder)
{
    // .planwharf/plan.md in the project folder. The Progress panel opens it.
    public string WorkPlan { get; init; } = "";
    // "About 25 minutes left (ends around 17:40) · rough", or empty when nothing is running or waiting.
    public string TimeLeft { get; init; } = "";
}
public sealed record QuotaWindow(string Name, int LeftPct, string Resets);
public sealed record QuotaPlan(string Label, string? Note, IReadOnlyList<QuotaWindow> Windows);
public sealed record UsagePanel(IReadOnlyList<QuotaPlan> Plans, IReadOnlyList<(string Model, string Text)> Lead, IReadOnlyList<(string Model, string Text)> Crew, IReadOnlyList<(string Model, string Text)>? Outcomes = null)
{
    public IReadOnlyList<(string Model, string Text)> Outcomes { get; init; } = Outcomes ?? [];
}
public sealed record ChangedFile(string Kind, string Path);
public sealed record FilesPanel(string? Message, string Branch, IReadOnlyList<ChangedFile> Files, int Total, string CheckedAt);
public sealed record PullCheck(string Name, string State);
public sealed record PullStep(string Title, string Model);
public sealed record PullWork(string Goal, string Started, string Finished, string Lead, IReadOnlyList<PullStep> Steps);
public sealed record PullRequestRow(int Number, string Title, string Branch, string Url, string State, string Opened, string Updated, string When, string Duration, bool Conflicts, IReadOnlyList<PullCheck> Checks, bool ByPlanwharf, bool CanFix, string FixStatus, string FixText, PullWork? Work)
{
    // A fix in progress: who works on it, for how long, at which stage, and what it did last.
    public PullFixProgress? Progress { get; init; }
}

public sealed record PullFixProgress(string Model, string Elapsed, string Stage, string Activity);
public sealed record FailedCheck(string Name, int Count);
public sealed record PullSummary(int Merged, string Median, int NeededFix, IReadOnlyList<FailedCheck> FailedChecks);
public sealed record PullRequestPanel(string? Message, bool Missing, PullSummary Summary, IReadOnlyList<PullRequestRow> Items, bool More, string State, string Author);

// The Progress, Usage and Files panels, read from the snapshot the way the chat page reads them.
public static class PanelViews
{
    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static double? N(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static string Clock(string at, TimeZoneInfo? zone = null) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? TimeZoneInfo.ConvertTime(when, zone ?? TimeZoneInfo.Local).ToString("HH:mm", Strings.Culture) : "";

    public static ProgressPanel Progress(JsonNode snapshot, string projectId, string status, TimeZoneInfo? zone = null)
    {
        var row = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => S(item["id"]) == projectId);
        var labels = (snapshot["models"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(model => S(model["id"]), model => S(model["label"]));
        string Label(string id) => labels.TryGetValue(id, out var label) && label.Length > 0 ? label : id.Length > 0 ? id : Strings.T("not assigned");
        var steps = (row?["subTasks"] as JsonArray ?? row?["megaTask"]?["subTasks"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(sub => new ProgressStep(S(sub["status"]) is { Length: > 0 } s ? s.ToLowerInvariant() : Strings.T("planned"), S(sub["title"]) is var t && t.Length > 140 ? t[..140] : t,
                Label(S(sub["assignedModelId"]) is { Length: > 0 } a ? a : S(sub["modelId"])))).ToList();
        var recent = (snapshot["execution"]?["timeline"] as JsonArray ?? []).OfType<JsonObject>().Where(e => S(e["projectId"]) == projectId)
            .TakeLast(15).Reverse().Select(e => (Clock(S(e["createdAt"]), zone), S(e["summary"]) is var text && text.Length > 200 ? text[..200] : text)).ToList();
        var folder = S(row?["folder"]);
        var subs = (row?["subTasks"] as JsonArray ?? row?["megaTask"]?["subTasks"] as JsonArray ?? []).OfType<JsonObject>();
        var open = subs.Any(sub => S(sub["status"]).ToUpperInvariant() is "RUNNING" or "PENDING" or "WORKING" or "WAITING" || S(sub["status"]).StartsWith("WAITING", StringComparison.OrdinalIgnoreCase));
        return new ProgressPanel(status, S(row?["next"]), steps, recent, folder)
        {
            WorkPlan = folder.Length == 0 ? "" : Path.Combine(folder, ".planwharf", "plan.md"),
            TimeLeft = open ? ChatViews.TimeLeft(row?["timeLeft"], zone) : ""
        };
    }

    static string WindowName(string name) => name switch
    {
        "5-hour" => Strings.T("5 hours"), "weekly" => Strings.T("Week"), "" => Strings.T("Limit"),
        _ => char.ToUpperInvariant(name.Replace('_', ' ').Replace('-', ' ')[0]) + name.Replace('_', ' ').Replace('-', ' ')[1..]
    };

    static string Resets(string at, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) || when <= now) return "";
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var sameDay = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date;
        return Strings.T("resets ") + (sameDay ? "" : local.ToString("ddd d MMM ", Strings.Culture)) + local.ToString("HH:mm", Strings.Culture);
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
                return new QuotaPlan(label, S(plan["reason"]) is { Length: > 0 } reason ? reason : Strings.T("Not reported by this provider"), []);
            if (windows.Count == 0) windows.Add(new QuotaWindow("", (int)Math.Round(N(plan["leftPct"])!.Value), ""));
            return new QuotaPlan(label, null, windows);
        }).ToList();
        static string Time(double? ms) => ms is not { } value ? "—" : value >= 60000 ? Strings.Format("{0} min", Math.Round(value / 60000)) : Strings.Format("{0} s", Math.Round(value / 1000));
        IReadOnlyList<(string, string)> Rows(string key) => (snapshot["aiUsage"]?[key] as JsonArray ?? []).OfType<JsonObject>()
            .Select(row => (S(row["model"]), $"{N(row["calls"]) ?? 0} · {Time(N(row["durationMs"]))} · {(N(row["outputTokens"]) is { } tokens ? tokens.ToString("#,0", Strings.Culture) + Strings.T(" out") : Strings.T("not reported"))}")).ToList();
        IReadOnlyList<(string, string)> OutcomeRows()
        {
            var outcomes = (snapshot["stepOutcomes"] as JsonArray ?? snapshot["aiUsage"]?["stepOutcomes"] as JsonArray ?? []).OfType<JsonObject>();
            return outcomes.Select(row =>
            {
                var model = S(row["model"]);
                var steps = (int)(N(row["steps"]) ?? 0);
                var accepted = (int)(N(row["accepted"]) ?? 0);
                var fixedCount = (int)(N(row["fixed"]) ?? 0);
                return (model, Strings.Format("{0} steps · {1} accepted · {2} fixed by Verify", steps, accepted, fixedCount));
            }).ToList();
        }
        return new UsagePanel(plans, Rows("orchestrator"), Rows("workers"), OutcomeRows());
    }

    static readonly Dictionary<string, string> Kinds = new() { ["M"] = "changed", ["A"] = "added", ["D"] = "deleted", ["R"] = "renamed", ["??"] = "new", ["U"] = "conflict" };

    public static FilesPanel Files(JsonNode? repository, string folder, string? error, TimeZoneInfo? zone = null)
    {
        if (folder.Length == 0) return new FilesPanel(Strings.T("This project has no folder yet. Set it in Settings."), "", [], 0, "");
        if (error != null) return new FilesPanel(error, "", [], 0, "");
        var local = repository?["local"];
        if (repository == null) return new FilesPanel(Strings.T("Reading the folder\u2026"), "", [], 0, "");
        if (!B(local?["exists"])) return new FilesPanel(Strings.Format("The folder {0} does not exist.", folder), "", [], 0, "");
        if (!B(local?["git"])) return new FilesPanel(Strings.T("This folder is not tracked by Git, so Planwharf cannot list what changed."), "", [], 0, "");
        var files = (local!["changes"] as JsonArray ?? []).Select(line => S(line)).Where(line => line.Length > 0).Select(line =>
        {
            var code = line.Length >= 2 ? line[..2].Trim() : line.Trim();
            if (code.Length == 0) code = "?";
            var kind = Kinds.TryGetValue(code, out var k) ? k : Kinds.TryGetValue(code[..1], out var first) ? first : "changed";
            return new ChangedFile(kind, line.Length > 3 ? line[3..] : "");
        }).ToList();
        return new FilesPanel(null, S(local["branch"]), files, N(local["uncommitted"]) is { } total ? (int)total : files.Count, Clock(S(repository["checkedAt"]), zone));
    }

    static string When(string at, TimeZoneInfo? zone)
    {
        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return "";
        return TimeZoneInfo.ConvertTime(parsed, zone ?? TimeZoneInfo.Local).ToString("d MMM HH:mm", Strings.Culture);
    }

    public static string Span(double? ms)
    {
        if (ms is not { } value || value < 0 || double.IsNaN(value)) return "—";
        if (value < 60000) return Strings.T("under a minute");
        var minutes = Math.Round(value / 60000);
        if (minutes < 90) return Strings.Format("{0} min", minutes);
        var hours = Math.Round(minutes / 60);
        if (hours < 48) return Strings.Format("{0} h", hours);
        return Strings.Format("{0} d", Math.Round(hours / 24));
    }

    static PullRequestRow Row(JsonObject pr, TimeZoneInfo? zone)
    {
        var state = S(pr["state"]) is "merged" or "closed" ? S(pr["state"]) : "open";
        var checks = (pr["checks"] as JsonArray ?? []).OfType<JsonObject>().Select(check => new PullCheck(S(check["name"]), S(check["state"]) is "green" or "red" or "running" ? S(check["state"]) : "running")).ToList();
        var workNode = pr["work"] as JsonObject;
        PullWork? work = workNode == null ? null : new PullWork(S(workNode["goal"]), When(S(workNode["startedAt"]), zone), When(S(workNode["completedAt"]), zone), S(workNode["lead"]),
            (workNode["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(step => new PullStep(S(step["title"]), S(step["model"]))).ToList());
        var when = state == "merged" ? When(S(pr["mergedAt"]), zone) : state == "closed" ? When(S(pr["closedAt"]), zone) : When(S(pr["updatedAt"]), zone);
        return new PullRequestRow(pr["number"] is JsonValue number && number.TryGetValue<int>(out var n) ? n : 0, S(pr["title"]), S(pr["branch"]), S(pr["url"]), state,
            When(S(pr["openedAt"]), zone), When(S(pr["updatedAt"]), zone), when, N(pr["durationMs"]) is { } duration ? Span(duration) : "",
            B(pr["conflicts"]), checks, B(pr["byPlanwharf"]), B(pr["canFix"]), S(pr["fixStatus"]), S(pr["fixText"]), work)
        {
            Progress = pr["fixProgress"] is JsonObject live ? new PullFixProgress(S(live["model"]),
                DateTimeOffset.TryParse(S(live["startedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) ? Span((DateTimeOffset.UtcNow - started).TotalMilliseconds) : "",
                S(live["stage"]), S(live["activity"])) : null
        };
    }

    // The list the panel draws. An unknown chip keeps the open list instead of hiding every row.
    public static IReadOnlyList<PullRequestRow> Matching(IReadOnlyList<PullRequestRow> rows, string state, string author)
    {
        var wanted = state is "merged" or "closed" ? state : "open";
        return rows.Where(row => row.State == wanted && author switch
        {
            "planwharf" => row.ByPlanwharf,
            "others" => !row.ByPlanwharf,
            _ => true
        }).ToList();
    }

    public static PullRequestPanel PullRequests(JsonNode? payload, TimeZoneInfo? zone = null)
    {
        if (payload == null) return new PullRequestPanel(Strings.T("Checking pull requests…"), false, new PullSummary(0, "—", 0, []), [], false, "open", "all");
        var state = S(payload["state"]) is "merged" or "closed" or "open" ? S(payload["state"]) : "open";
        var author = S(payload["author"]) is "planwharf" or "others" or "all" ? S(payload["author"]) : "all";
        var summaryNode = payload["summary"] as JsonObject;
        var failed = (summaryNode?["failedChecks"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => new FailedCheck(S(item["name"]), N(item["count"]) is { } count ? (int)count : 0)).Where(item => item.Name.Length > 0).ToList();
        var summary = new PullSummary(N(summaryNode?["merged"]) is { } merged ? (int)merged : 0, Span(N(summaryNode?["medianMergeMs"])), N(summaryNode?["neededFix"]) is { } fixes ? (int)fixes : 0, failed);
        var items = (payload["items"] as JsonArray ?? []).OfType<JsonObject>().Select(pr => Row(pr, zone)).ToList();
        if (B(payload["missing"])) return new PullRequestPanel(Strings.T("GitHub CLI (gh) is not installed. Install it from https://cli.github.com/ and sign in with gh auth login. The rest of Planwharf keeps working."), true, summary, [], false, state, author);
        if (payload["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var fine) && !fine) return new PullRequestPanel(S(payload["reason"]) is { Length: > 0 } reason ? reason : Strings.T("Could not read pull requests."), false, summary, [], false, state, author);
        // Items stay unfiltered: the panel applies Open, Merged, Closed and who opened them.
        return new PullRequestPanel(null, false, summary, items, B(payload["more"]), state, author);
    }
}
