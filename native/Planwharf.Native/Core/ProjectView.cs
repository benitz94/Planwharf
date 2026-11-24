using Planwharf.Engine;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

public sealed record ProjectItem(string Id, string Name, string Status)
{
    // Saved saving mode. The list shows it beside the name; Name itself stays the project name.
    public string SavingMode { get; init; } = SavingModes.Normal;
}
public sealed record ChatMessage(bool FromUser, string Who, string Text, DateTimeOffset At);
public sealed record StepResult(string Role, string Model, string Summary);
public sealed record WorkStep(string Id, string Role, string Model, string Status, string ModelId, string Title = "", string PlainLine = "", string Instruction = "");
public sealed record WorkView(string Title, string Status, int Done, int Total, IReadOnlyList<WorkStep> Steps, IReadOnlyList<string> Results)
{
    public IReadOnlyList<(string Id, string Text, string Suggestion)> OpenDecisions { get; init; } = [];
    // The project row as the server sent it, for the summary, usage and activity.
    public JsonObject? Row { get; init; }
    public string NextAction { get; init; } = "";
    public bool CanResume { get; init; }
    public string StartedAt { get; init; } = "";
    public string CompletedAt { get; init; } = "";
    public string Waiting { get; init; } = "";
    public IReadOnlyList<StepResult> StepResults { get; init; } = [];
    public string Id { get; init; } = "";
    public string PlainSummary { get; init; } = "";
    public string PlainSummaryFrom { get; init; } = "";
}
public enum CrewAction { None, LetTheCrewDoIt, StopTheCrew, Approve, Resume, StopWork }
public sealed record ProjectView(string Id, string Name, string Status, IReadOnlyList<ChatMessage> Messages, WorkView? Work,
    CrewAction Action, string ActionText, string Hint, string? NextGoal, string? MegaTaskId)
{
    // Shown under "The crew is working" while a step is running or waiting. Empty otherwise.
    public string TimeLeft { get; init; } = "";
    // talk, plan or auto. Missing and reserved values are Plan.
    public string ChatMode { get; init; } = "plan";
    // normal, safe or ultra-safe. Missing and unknown values are Normal.
    public string SavingMode { get; init; } = SavingModes.Normal;
    public string? Addition { get; init; }
}

// Reads the server's snapshot the way the chat page does: same labels, same next action, same stop sentences.
public static class ProjectViews
{
    static readonly Regex Suggested = new(@"^\s*\**Suggested next goal:\**\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    static readonly Regex Addition = new(@"^\s*\**Addition to this work:\**\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    const string VerifyPrefix = "Verify the work independently";

    static Dictionary<string, string> StopLabels => new()
    {
        ["GOAL_REACHED"] = Strings.T("Goal reached"), ["LEAD_UNCONFIRMED"] = Strings.T("Finished: check the result"), ["UNREADABLE_LEAD_DECISION"] = Strings.T("Finished: check the result"),
        ["MEGATASK_LIMIT"] = Strings.T("Stopped: work limit reached"), ["REPEATED_FAILURE"] = Strings.T("Stopped: the same work failed three times"), ["NO_LEAD_AVAILABLE"] = Strings.T("Stopped: no lead available"), ["LEAD_FAILED"] = Strings.T("Stopped: the lead could not answer"),
        ["START_UNAVAILABLE"] = Strings.T("Stopped: could not start"), ["START_FAILED"] = Strings.T("Stopped: could not start"), ["NOTHING_STARTED"] = Strings.T("Stopped: no work started"),
        ["APPROVAL_REFUSED"] = Strings.T("Stopped: work not approved"), ["AUTONOMY_ERROR"] = Strings.T("Stopped: error"), ["NO_PROGRESS"] = Strings.T("Stopped: no progress twice"), ["TIME_LIMIT"] = Strings.T("Stopped: time limit reached")
    };
    static Dictionary<string, string> StopSentences => new()
    {
        ["LEAD_UNCONFIRMED"] = Strings.T("Work finished, but the lead did not confirm the goal. Check the result in the project folder."),
        ["UNREADABLE_LEAD_DECISION"] = Strings.T("Work finished, but the lead did not confirm the goal. Check the result in the project folder."),
        ["MEGATASK_LIMIT"] = Strings.T("The crew stopped after 20 pieces of work in a row. Check the result, then press Let the crew do it to go on."), ["REPEATED_FAILURE"] = Strings.T("The same work failed three times. Planwharf stopped instead of retrying it. Fix the cause, then press Let the crew do it."),
        ["NO_LEAD_AVAILABLE"] = Strings.T("The crew stopped: no lead is available. Choose a lead with quota in Team."),
        ["LEAD_FAILED"] = Strings.T("The crew stopped: the lead could not answer"), ["START_UNAVAILABLE"] = Strings.T("The crew could not start the work."),
        ["START_FAILED"] = Strings.T("The crew could not start the work"), ["NOTHING_STARTED"] = Strings.T("The crew started no work"),
        ["APPROVAL_REFUSED"] = Strings.T("The work could not be approved"), ["AUTONOMY_ERROR"] = Strings.T("The crew stopped on an error"), ["NO_PROGRESS"] = Strings.T("The crew made no progress twice in a row. Answer in Decisions how it should continue, and it starts again."),
        ["TIME_LIMIT"] = Strings.T("The crew stopped: the time limit was reached.")
    };

    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static string StopSentence(string reason)
    {
        var code = reason.Split(':')[0];
        var detail = reason.Length > code.Length ? reason[(code.Length + 1)..].Trim() : "";
        var sentence = StopSentences.GetValueOrDefault(code, Strings.T("The crew stopped"));
        return detail.Length > 0 ? $"{sentence.TrimEnd('.')}: {detail}" : sentence.EndsWith('.') ? sentence : sentence + ".";
    }

    // What a worker found and must not decide alone: the lines of its summary that start with "Needs your
    // decision:" (the Verify step is asked to write them so), shown apart so the owner does not miss them.
    public static IReadOnlyList<string> Decisions(IEnumerable<string> results) =>
        results.SelectMany(text => text.Split('\n')).Select(line => line.Trim().TrimStart('-', '•', '*', ' '))
            .Select(line => line.IndexOf("Needs your decision:", StringComparison.OrdinalIgnoreCase) is var at && at >= 0 ? line[(at + "Needs your decision:".Length)..].Trim() : "")
            .Where(line => line.Length > 0).Distinct().ToList();

    public static string? SuggestedGoal(string? answer) => answer != null && Suggested.Match(answer) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
    public static string? AdditionText(string? answer)
    {
        if (answer == null || Addition.Match(answer) is not { Success: true } match) return null;
        var text = match.Groups[1].Value.Trim();
        return text.Length > 0 ? text : null;
    }

    // The project used last comes first, so a message is not sent to a project the user left long ago.
    // Used means a message to its lead or work of its crew; only opening a project does not move it.
    public static IReadOnlyList<ProjectItem> Projects(JsonNode snapshot) =>
        (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>()
            .Select((row, index) => (row, index, used: LastUsed(snapshot, S(row["id"]))))
            .OrderByDescending(item => item.used).ThenBy(item => item.index)
            .Select(item => new ProjectItem(S(item.row["id"]), S(item.row["name"]), StatusOf(snapshot, item.row)) { SavingMode = SavingModes.Normalize(S(item.row["savingMode"])) }).ToList();

    public static DateTimeOffset LastUsed(JsonNode snapshot, string projectId)
    {
        var last = DateTimeOffset.MinValue;
        void Take(JsonNode? value) { if (DateTimeOffset.TryParse(S(value), out var at) && at > last) last = at; }
        foreach (var ask in (snapshot["asks"] as JsonArray ?? []).OfType<JsonObject>().Where(ask => S(ask["projectId"]) == projectId)) Take(ask["at"]);
        foreach (var mega in (snapshot["execution"]?["megaTasks"] as JsonArray ?? []).OfType<JsonObject>().Where(mega => S(mega["projectId"]) == projectId))
        { Take(mega["createdAt"]); Take(mega["approvedAt"]); Take(mega["completedAt"]); }
        return last;
    }

    static string StatusOf(JsonNode snapshot, JsonObject row) => ChatViews.Status(snapshot, row).Text;

    public static ProjectView? Project(JsonNode snapshot, string? projectId, TimeZoneInfo? zone = null)
    {
        var rows = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var row = rows.FirstOrDefault(item => S(item["id"]) == projectId) ?? rows.FirstOrDefault();
        if (row == null) return null;
        var id = S(row["id"]);
        var models = (snapshot["models"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(model => S(model["id"]), model => S(model["label"]).Length > 0 ? S(model["label"]) : S(model["id"]));
        string Label(string modelId) => models.GetValueOrDefault(modelId, modelId);

        var messages = new List<ChatMessage>();
        foreach (var ask in (snapshot["asks"] as JsonArray ?? []).OfType<JsonObject>().Where(ask => S(ask["projectId"]) == id))
        {
            var at = DateTimeOffset.TryParse(S(ask["at"]), out var when) ? when : DateTimeOffset.MinValue;
            messages.Add(new ChatMessage(true, Strings.T("You"), S(ask["question"]), at));
            if (S(ask["answer"]).Length > 0) messages.Add(new ChatMessage(false, Label(S(ask["orchestratorId"])) is { Length: > 0 } lead ? lead : Strings.T("Lead"), S(ask["answer"]), at));
        }

        WorkView? work = null;
        if (row["megaTask"] is JsonObject mega && S(mega["status"]).ToUpperInvariant() != "CANCELLED")
        {
            var subs = (row["subTasks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var steps = subs.Select(sub => {
                var role = S(sub["type"]) == "AUDIT" ? "Review" : S(sub["type"]) == "RESEARCH" ? "Research" : S(sub["title"]).StartsWith(VerifyPrefix) ? "Verify" : "Build";
                var rawTitle = S(sub["title"]);
                var instruction = S(sub["detail"]) is { Length: > 0 } detail ? detail : rawTitle;
                var line = S(sub["plainLine"]);
                if (line.Length == 0)
                {
                    var (fallback, _) = PlainLines.StepLine(sub);
                    line = fallback;
                }
                if (line == "Checks the whole work and runs the tests.") line = Strings.T(line);
                return new WorkStep(S(sub["id"]),
                    role,
                    Label(S(sub["assignedModelId"])),
                    S(sub["status"]).ToUpperInvariant() switch { "PENDING" => S(sub["waitingFor"]).Length > 0 ? "waiting for quota" : "waiting", "RUNNING" => "working", "COMPLETE" => "done", var other => other.ToLowerInvariant() },
                    S(sub["assignedModelId"]),
                    rawTitle.StartsWith(VerifyPrefix) ? Strings.T("Check the work and fix what fails") : rawTitle is { Length: > 120 } long_ ? long_[..120] : rawTitle,
                    line,
                    instruction);
            }).ToList();
            var results = (row["results"] as JsonArray ?? []).OfType<JsonObject>().Where(result => !B(result["modelChange"])).Select(result => S(result["summary"])).Where(text => text.Length > 0).ToList();
            var plainSummary = S(row["plain"]?["summary"]);
            var plainFrom = S(row["plain"]?["summaryFrom"]);
            if (plainSummary.Length == 0)
            {
                var (summary, from) = PlainLines.WorkSummary(mega, row["results"] as JsonArray, row["subTasks"] as JsonArray);
                plainSummary = summary;
                plainFrom = from;
            }
            work = new WorkView(S(mega["title"]), S(mega["status"]).ToUpperInvariant(), steps.Count(step => step.Status == "done"), steps.Count, steps, results)
            {
                OpenDecisions = (row["openDecisions"] as JsonArray ?? []).OfType<JsonObject>().Select(item => (S(item["id"]), S(item["text"]), S(item["suggestion"]))).Where(item => item.Item2.Length > 0).ToList(),
                Row = row, Id = S(mega["id"]), CanResume = B(row["canResume"]),
                NextAction = new[] { S(mega["nextAction"]), S(row["next"]), S(row["label"]) }.FirstOrDefault(text => text.Length > 0) ?? "",
                StartedAt = S(mega["approvedAt"]) is { Length: > 0 } approved ? approved : S(mega["createdAt"]), CompletedAt = S(mega["completedAt"]),
                Waiting = subs.Select(sub => S(sub["status"]).ToUpperInvariant() == "PENDING" ? S(sub["waitingFor"]) : "").FirstOrDefault(text => text.Length > 0) ?? "",
                PlainSummary = plainSummary,
                PlainSummaryFrom = plainFrom,
                // What each step reported, in order, with its role and model: the commit message is made from it.
                StepResults = (row["results"] as JsonArray ?? []).OfType<JsonObject>().Where(result => !B(result["modelChange"]) && S(result["summary"]).Length > 0)
                    .Select(result => steps.FirstOrDefault(step => step.Id == S(result["subTaskId"])) is { } step ? new StepResult(step.Role, step.Model, S(result["summary"])) : null)
                    .OfType<StepResult>().ToList()
            };
        }

        // One clear next action at a time, as in the chat page.
        var auto = snapshot["autonomy"]?[id];
        var stopReason = S(auto?["stopReason"]);
        var working = B(auto?["enabled"]) && stopReason.Length == 0;
        // Planwharf keeps the chat newest first: the lead's latest reply is the one with the latest time, not the last in the list.
        var lastAsk = (snapshot["asks"] as JsonArray ?? []).OfType<JsonObject>().Where(ask => S(ask["projectId"]) == id)
            .OrderBy(ask => DateTimeOffset.TryParse(S(ask["at"]), out var at) ? at : DateTimeOffset.MinValue).LastOrDefault();
        var goal = S(row["goal"]).Trim();
        var chatMode = S(row["chatMode"]).Trim().ToLowerInvariant();
        if (chatMode != "talk") chatMode = "plan";
        var savingMode = SavingModes.Normalize(S(row["savingMode"]));
        // Between two works the crew is still on: the lead checks the result and decides the next step.
        var running = work != null && work.Status is "RUNNING" or "READY_FOR_APPROVAL" || B(row["canStop"]);
        // Only a goal the lead suggested is offered to the crew: a plain question is not work to do.
        // Talk offers neither goal.
        var additionText = chatMode == "talk" || lastAsk == null ? null : AdditionText(S(lastAsk["answer"]));
        var nextGoal = chatMode == "talk" || lastAsk == null ? null : (running ? SuggestedGoal(S(lastAsk["answer"])) : (SuggestedGoal(S(lastAsk["answer"])) ?? additionText));
        if (nextGoal != null && (nextGoal == goal || S(lastAsk?["question"]).Trim() == goal)) nextGoal = null;
        var suggestedAt = lastAsk != null && DateTimeOffset.TryParse(S(lastAsk["at"]), out var parsedAsk) ? parsedAsk : (DateTimeOffset?)null;
        if (nextGoal != null && !TeamViews.SuggestionIsCurrent(suggestedAt, work?.StartedAt)) nextGoal = null;
        var addition = chatMode == "talk" || lastAsk == null || !running ? null : additionText;

        (CrewAction action, string text, string hint) = working ? (CrewAction.StopTheCrew, Strings.T("Stop the crew"), running
                ? Strings.T("The crew is working. You can keep chatting with the lead and change models at any time.")
                : Strings.T("The work is done. The lead is checking the result and deciding the next step; Stop the crew if it should not go on."))
            : S(row["status"]) == "needs_approval" ? (CrewAction.Approve, Strings.T("Approve and start"), Strings.T("The lead proposed some work. Approve it, or tell the lead what to change."))
            : B(row["canStop"]) ? (CrewAction.StopWork, Strings.T("Stop work"), Strings.T("Work in progress. You can keep chatting with the lead."))
            : B(row["canResume"]) ? (CrewAction.Resume, Strings.T("Resume"), (stopReason.Length > 0 && stopReason != "GOAL_REACHED" ? StopSentence(stopReason) + " " : "")
                + Strings.T("Work was stopped. Resume continues from the saved point, or drop it to start something new."))
            : (CrewAction.LetTheCrewDoIt, Strings.T("Let the crew do it"),
                stopReason.Length > 0 && stopReason != "GOAL_REACHED" ? StopSentence(stopReason)
                : goal.Length == 0 && nextGoal == null ? Strings.T("Talk with the lead about what to build: it suggests a goal, then press Let the crew do it.")
                : Strings.T("Ready. The crew will plan, build, check and stop when the goal is reached."));
        // Talk does not offer Use as the goal or Let the crew do it. Stop, approve and resume stay.
        if (chatMode == "talk" && action == CrewAction.LetTheCrewDoIt)
            (action, text, hint) = (CrewAction.None, "", Strings.T("A conversation with the lead. Nothing starts."));
        var timeLeft = work != null && stepsStillOpen(work) ? ChatViews.TimeLeft(row["timeLeft"], zone) : "";
        return new ProjectView(id, S(row["name"]), StatusOf(snapshot, row), messages, work, action, text, hint, nextGoal, S(row["megaTaskId"]) is { Length: > 0 } mt ? mt : null) { TimeLeft = timeLeft, ChatMode = chatMode, SavingMode = savingMode, Addition = addition };
    }

    public static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var plain = Regex.Replace(text, @"```[\s\S]*?```", " ");
        plain = Regex.Replace(plain, @"`([^`]*)`", "$1");
        plain = Regex.Replace(plain, @"\*\*|__", "");
        plain = Regex.Replace(plain, @"^\s*#+\s*", "", RegexOptions.Multiline);
        plain = Regex.Replace(plain, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        var match = Regex.Match(plain, @"[.!?](\s|$)|:\s");
        var end = match.Success ? (plain[match.Index] == ':' ? match.Index : match.Index + 1) : plain.Length;
        return plain[..end].Trim();
    }

    static bool stepsStillOpen(WorkView work) => work.Steps.Any(step => step.Status is "working" or "waiting" or "waiting for quota");
}
