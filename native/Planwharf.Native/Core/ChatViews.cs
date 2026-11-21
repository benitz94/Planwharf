using Planwharf.Engine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

public sealed record ProjectStatusView(string Text, string Kind);
public abstract record ThreadItem(DateTimeOffset At);
public sealed record AskItem(DateTimeOffset At, string Question, string Answer, string Who, string Elapsed, bool Failed, string Thinking, string ToolRequest) : ThreadItem(At) { public string ModelId { get; init; } = ""; }
public sealed record EventItem(DateTimeOffset At, string Kind, string Text) : ThreadItem(At);
public enum CommandKind { None, StopNow, StopIn, OpenPanel, Bench }
public sealed record LocalCommand(CommandKind Kind, int Minutes = 0, string Panel = "", string Model = "");

// The chat as the chat page shows it: project status, who is working, the thread with events, news, local commands.
public static class ChatViews
{
    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    static DateTimeOffset T(string at) => DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : DateTimeOffset.MinValue;

    static Dictionary<string, string> StopLabels => new()
    {
        ["GOAL_REACHED"] = Strings.T("Goal reached"), ["LEAD_UNCONFIRMED"] = Strings.T("Finished: check the result"), ["UNREADABLE_LEAD_DECISION"] = Strings.T("Finished: check the result"),
        ["MEGATASK_LIMIT"] = Strings.T("Stopped: work limit reached"), ["NO_LEAD_AVAILABLE"] = Strings.T("Stopped: no lead available"), ["LEAD_FAILED"] = Strings.T("Stopped: the lead could not answer"),
        ["START_UNAVAILABLE"] = Strings.T("Stopped: could not start"), ["START_FAILED"] = Strings.T("Stopped: could not start"), ["NOTHING_STARTED"] = Strings.T("Stopped: no work started"),
        ["APPROVAL_REFUSED"] = Strings.T("Stopped: work not approved"), ["AUTONOMY_ERROR"] = Strings.T("Stopped: error"), ["TIME_LIMIT"] = Strings.T("Stopped: time limit reached")
    };

    public static ProjectStatusView Status(JsonNode snapshot, JsonObject row)
    {
        var auto = snapshot["autonomy"]?[S(row["id"])];
        var stop = S(auto?["stopReason"]);
        if (B(auto?["enabled"]) && stop == "GOAL_REACHED") return new(Strings.T("Goal reached"), "done");
        if (B(auto?["enabled"]) && stop.Length > 0) return new(StopLabels.GetValueOrDefault(stop, Strings.T("Stopped: ") + stop.Split(':')[0].Replace('_', ' ').ToLowerInvariant()), "attention");
        if (B(auto?["enabled"])) return new(Strings.T("Crew working"), "running");
        var status = S(row["status"]) is { Length: > 0 } s ? s : "idle";
        if (Regex.IsMatch(status, "running|watching|dispatch")) return new(Strings.T("Working"), "running");
        if (Regex.IsMatch(status, "blocked|failed|needs|relink|checkpoint")) return new(S(row["label"]) is { Length: > 0 } label ? label : Strings.T("Needs you"), "attention");
        if (status.Contains("complete")) return new(Strings.T("Done"), "done");
        return new(Strings.T("Ready"), "");
    }

    // Who works on a project right now: a live crew process. The lead answering is added by the window.
    public static (string Who, DateTimeOffset? Since)? Working(JsonObject row, Func<string, string> modelLabel)
    {
        var part = (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => B(item["alive"]));
        if (part == null) return null;
        var who = modelLabel(S(part["modelId"])) is { Length: > 0 } label ? label : S(part["label"]) is { Length: > 0 } l ? l : S(row["workerLabel"]) is { Length: > 0 } w ? w : Strings.T("The crew");
        return (who, S(part["startedAt"]) is { Length: > 0 } at ? T(at) : null);
    }

    public static string WorkingText(string who, DateTimeOffset? since, DateTimeOffset now) =>
        Strings.Format("{0} is working{1}", who, (since is { } start ? " · " + (WorkDetails.Duration((now - start).TotalMilliseconds) is { Length: > 0 } d ? d : "1 s") : ""));

    // "About 25 minutes left (ends around 17:40) · rough". Empty when the engine has no estimate.
    public static string TimeLeft(JsonNode? node, TimeZoneInfo? zone = null)
    {
        if (node is not JsonObject obj) return "";
        double? minutes = null;
        if (obj["minutes"] is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) minutes = number;
            else if (value.TryGetValue<int>(out var whole)) minutes = whole;
            else if (value.TryGetValue<string>(out var text) && double.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)) minutes = parsed;
        }
        if (minutes is not { } left) return "";
        var rounded = (int)Math.Round(left);
        if (rounded < 0) return "";
        var about = rounded == 1 ? Strings.T("About 1 minute left") : Strings.Format("About {0} minutes left", rounded);
        var ends = PanelViews.Clock(S(obj["endsAt"]), zone);
        var when = ends.Length > 0 ? Strings.Format(" (ends around {0})", ends) : "";
        var sure = S(obj["sure"]);
        var confidence = sure.Length > 0 ? " · " + Strings.T(sure) : "";
        var longer = obj["longerThanUsual"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        return longer ? Strings.T("Longer than usual \u00B7 ") + char.ToLowerInvariant(about[0]) + about[1..] + when + confidence : about + when + confidence;
    }

    static readonly Regex Shown = new("QUOTA_STOPPED|PR_FIX_(STARTED|COMPLETED|FAILED)|LEAD_SWITCHED|PLAN_REVISION|MEGATASK_(PROPOSED|APPROVED|ASSIGNED|COMPLETED|FAILED|STOPPED|BLOCKED)|SUBTASK_(STARTED|COMPLETED|FAILED|BLOCKED|REASSIGNED)|CHECKPOINT|GOAL|AUTONOMY");
    static readonly Regex News = new("QUOTA_STOPPED|PR_FIX_(COMPLETED|FAILED)|LEAD_SWITCHED|SUBTASK_(COMPLETED|FAILED|BLOCKED)|MEGATASK_(PROPOSED|COMPLETED|FAILED|BLOCKED|STOPPED)|GOAL|AUTONOMY");

    // Plain words: no internal states, no repeated titles, no long verify prefix.
    public static (string Kind, string Text) Event(string type, string summary)
    {
        var text = Regex.Replace(summary, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"Verify the work independently against the job:?\s*", Strings.T("Check: "), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^(.+?): Finished \1\.?$", Strings.T("Step done: $1"));
        text = Regex.Replace(text, @"\.\s*(COMMITTED|COMPLETE|COMPLETED)\.?$", ".");
        text = Regex.Replace(text, @": (\d+) proposed slice\(s\)\.?$", match => Strings.Format(": proposed in {0} step{1}.", match.Groups[1].Value, Strings.CurrentLanguage == "en" && match.Groups[1].Value != "1" ? "s" : ""));
        text = Regex.Replace(text, "^Completed (.+)$", Strings.T("Finished: $1"));
        text = Regex.Replace(text, "^Approved (.+)$", Strings.T("You approved: $1"));
        if (text.Length > 260) text = text[..257] + "…";
        if (Regex.IsMatch(type, "COMPLETED|REACHED|APPROVED")) return ("good", text);
        if (Regex.IsMatch(type, "FAILED|BLOCKED|STOPPED|REFUS")) return ("bad", text);
        if (type.Contains("STARTED")) return ("start", text);
        if (type.Contains("PLAN_REVISION")) return ("plan", Regex.Replace(text, @"^Living Plan R\d+:?\s*", "") is { Length: > 0 } plan ? Strings.T("Plan updated: ") + plan : Strings.T("Plan updated."));
        if (Regex.IsMatch(type, "PROPOSED|ASSIGNED|PLAN|SWITCHED")) return ("plan", text);
        return ("", text);
    }

    // The conversation: messages and work events in time order (the last 60 events).
    public static IReadOnlyList<ThreadItem> Thread(JsonNode snapshot, string projectId, Func<string, string> modelLabel)
    {
        var items = new List<ThreadItem>();
        foreach (var ask in (snapshot["asks"] as JsonArray ?? []).OfType<JsonObject>().Where(ask => S(ask["projectId"]) == projectId))
        {
            var ms = ask["ms"] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : (double?)null;
            var who = modelLabel(S(ask["orchestratorId"])) is { Length: > 0 } label ? label : Strings.T("Lead");
            items.Add(new AskItem(T(S(ask["at"])), S(ask["question"]), S(ask["answer"]), who, ms is { } m ? Strings.Format("{0} s", Math.Max(1, Math.Round(m / 1000))) : "",
                ask["diagnostic"] is JsonObject, S(ask["thinking"]) is var thinking && thinking.Length > 4000 ? thinking[^4000..] : thinking, S(ask["toolRequest"])) { ModelId = S(ask["orchestratorId"]) });
        }
        foreach (var e in (snapshot["execution"]?["timeline"] as JsonArray ?? []).OfType<JsonObject>().Where(e => S(e["projectId"]) == projectId && Shown.IsMatch(S(e["type"]))).TakeLast(60))
        {
            var (kind, text) = Event(S(e["type"]), S(e["summary"]));
            items.Add(new EventItem(T(S(e["createdAt"])), kind, text));
        }
        return items.OrderBy(item => item.At).ToList();
    }

    // The time of the latest news on a project: finished or failed steps, proposals, goals, replies.
    public static DateTimeOffset LastNews(JsonNode snapshot, string projectId)
    {
        var latest = DateTimeOffset.MinValue;
        foreach (var e in (snapshot["execution"]?["timeline"] as JsonArray ?? []).OfType<JsonObject>().Where(e => S(e["projectId"]) == projectId && News.IsMatch(S(e["type"]))))
            if (T(S(e["createdAt"])) > latest) latest = T(S(e["createdAt"]));
        foreach (var ask in (snapshot["asks"] as JsonArray ?? []).OfType<JsonObject>().Where(ask => S(ask["projectId"]) == projectId))
            if (T(S(ask["at"])) > latest) latest = T(S(ask["at"]));
        return latest;
    }

    static readonly Regex StopLater = new(@"^(?:stop|fermati|fermatevi|ferma(?:ti)?(?: tutto)?|stop the crew)\s+(?:in|tra|fra)\s+(\d{1,3})\s*(?:m|min|mins|minute|minutes|minuto|minuti)?\b");
    static readonly Regex StopNow = new(@"^(?:stop|stop now|stop the crew|stop work|fermati|fermatevi|fermati (?:adesso|ora|subito)|fermatevi (?:adesso|ora|subito)|ferma tutto|basta)$");
    static readonly (string Panel, Regex Words)[] PanelWords =
    [
        ("team", new(@"^(open |show |apri |mostra |vedi )?(the |la |il |i )?(team|crew|squadra|bench|panchina)$")),
        ("usage", new(@"^(open |show |apri |mostra |vedi )?(the |i |l')?(usage|quota|tokens?|consumi|utilizzo)$")),
        ("progress", new(@"^(open |show |apri |mostra |vedi )?(the |l')?(progress|status|avanzamento|stato)$")),
        ("files", new(@"^(open |show |apri |mostra |vedi )?(the |i |le )?(files?|changes|changed files|file cambiati|modifiche)$")),
        ("settings", new(@"^(open |show |apri |mostra |vedi )?(the |le )?(settings|options|impostazioni|opzioni)$")),
        ("pulls", new(@"^(open |show |apri |mostra |vedi )?(the |le |i )?(pull requests?|prs?|richieste)$"))
    ];
    static readonly Regex BenchWords = new(@"^(?:bench|put|metti)\s+(.+?)(?:\s+(?:on the bench|in panchina))?$");

    // Short orders Planwharf handles itself, as in the chat page: stopping, opening a panel, benching a model.
    public static LocalCommand Command(string text)
    {
        var clean = Regex.Replace(text.Trim().ToLowerInvariant(), @"[.!?]+$", "");
        if (StopLater.Match(clean) is { Success: true } later)
        {
            var minutes = int.Parse(later.Groups[1].Value, CultureInfo.InvariantCulture);
            return minutes < 1 ? new LocalCommand(CommandKind.StopNow) : new LocalCommand(CommandKind.StopIn, minutes);
        }
        if (StopNow.IsMatch(clean)) return new LocalCommand(CommandKind.StopNow);
        if (clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7) return new LocalCommand(CommandKind.None);
        foreach (var (panel, words) in PanelWords) if (words.IsMatch(clean)) return new LocalCommand(CommandKind.OpenPanel, Panel: panel);
        if (BenchWords.Match(clean) is { Success: true } bench && (clean.StartsWith("bench ") || Regex.IsMatch(clean, "(on the bench|in panchina)$")))
            return new LocalCommand(CommandKind.Bench, Model: Regex.Replace(bench.Groups[1].Value, "^(the |il |la )", ""));
        return new LocalCommand(CommandKind.None);
    }
}
