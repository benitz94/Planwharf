using Planwharf.Engine;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

public sealed record ProjectItem(string Id, string Name, string Status);
public sealed record ChatMessage(bool FromUser, string Who, string Text, DateTimeOffset At);
public sealed record StepResult(string Role, string Model, string Summary);
public sealed record WorkStep(string Id, string Role, string Model, string Status, string ModelId, string Title = "");
public sealed record WorkView(string Title, string Status, int Done, int Total, IReadOnlyList<WorkStep> Steps, IReadOnlyList<string> Results)
{
    // The project row as the server sent it, for the summary, usage and activity.
    public JsonObject? Row { get; init; }
    public string NextAction { get; init; } = "";
    public bool CanResume { get; init; }
    public string StartedAt { get; init; } = "";
    public string CompletedAt { get; init; } = "";
    public string Waiting { get; init; } = "";
    public IReadOnlyList<StepResult> StepResults { get; init; } = [];
    public string Id { get; init; } = "";
}
public enum CrewAction { None, LetTheCrewDoIt, StopTheCrew, Approve, Resume, StopWork }
public sealed record ProjectView(string Id, string Name, string Status, IReadOnlyList<ChatMessage> Messages, WorkView? Work,
    CrewAction Action, string ActionText, string Hint, string? NextGoal, string? MegaTaskId)
{
    // Shown under "The crew is working" while a step is running or waiting. Empty otherwise.
    public string TimeLeft { get; init; } = "";
}

// Reads the server's snapshot the way the chat page does: same labels, same next action, same stop sentences.
public static class ProjectViews
{
    static readonly Regex Suggested = new(@"^\s*\**Suggested next goal:\**\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    const string VerifyPrefix = "Verify the work independently";

    static Dictionary<string, string> StopLabels => new()
    {
        ["GOAL_REACHED"] = Strings.T("Goal reached"), ["LEAD_UNCONFIRMED"] = Strings.T("Finished: check the result"), ["UNREADABLE_LEAD_DECISION"] = Strings.T("Finished: check the result"),
        ["MEGATASK_LIMIT"] = Strings.T("Stopped: work limit reached"), ["REPEATED_FAILURE"] = Strings.T("Stopped: the same work failed three times"), ["NO_LEAD_AVAILABLE"] = Strings.T("Stopped: no lead available"), ["LEAD_FAILED"] = Strings.T("Stopped: the lead could not answer"),
        ["START_UNAVAILABLE"] = Strings.T("Stopped: could not start"), ["START_FAILED"] = Strings.T("Stopped: could not start"), ["NOTHING_STARTED"] = Strings.T("Stopped: no work started"),
        ["APPROVAL_REFUSED"] = Strings.T("Stopped: work not approved"), ["AUTONOMY_ERROR"] = Strings.T("Stopped: error"), ["TIME_LIMIT"] = Strings.T("Stopped: time limit reached")
    };
    static Dictionary<string, string> StopSentences => new()
    {
        ["LEAD_UNCONFIRMED"] = Strings.T("Work finished, but the lead did not confirm the goal. Check the result in the project folder."),
        ["UNREADABLE_LEAD_DECISION"] = Strings.T("Work finished, but the lead did not confirm the goal. Check the result in the project folder."),
        ["MEGATASK_LIMIT"] = Strings.T("The crew stopped after 20 pieces of work in a row. Check the result, then press Let the crew do it to go on."), ["REPEATED_FAILURE"] = Strings.T("The same work failed three times. Planwharf stopped instead of retrying it. Fix the cause, then press Let the crew do it."),
        ["NO_LEAD_AVAILABLE"] = Strings.T("The crew stopped: no lead is available. Choose a lead with quota in Team."),
        ["LEAD_FAILED"] = Strings.T("The crew stopped: the lead could not answer"), ["START_UNAVAILABLE"] = Strings.T("The crew could not start the work."),
        ["START_FAILED"] = Strings.T("The crew could not start the work"), ["NOTHING_STARTED"] = Strings.T("The crew started no work"),
        ["APPROVAL_REFUSED"] = Strings.T("The work could not be approved"), ["AUTONOMY_ERROR"] = Strings.T("The crew stopped on an error"),
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

    public static string? SuggestedGoal(string answer) => Suggested.Match(answer) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    // The project used last comes first, so a message is not sent to a project the user left long ago.
    // Used means a message to its lead or work of its crew; only opening a project does not move it.
    public static IReadOnlyList<ProjectItem> Projects(JsonNode snapshot) =>
        (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>()
            .Select((row, index) => (row, index, used: LastUsed(snapshot, S(row["id"]))))
            .OrderByDescending(item => item.used).ThenBy(item => item.index)
            .Select(item => new ProjectItem(S(item.row["id"]), S(item.row["name"]), StatusOf(snapshot, item.row))).ToList();

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
            var steps = subs.Select(sub => new WorkStep(S(sub["id"]),
                S(sub["type"]) == "AUDIT" ? "Review" : S(sub["type"]) == "RESEARCH" ? "Research" : S(sub["title"]).StartsWith(VerifyPrefix) ? "Verify" : "Build",
                Label(S(sub["assignedModelId"])),
                S(sub["status"]).ToUpperInvariant() switch { "PENDING" => S(sub["waitingFor"]).Length > 0 ? "waiting for quota" : "waiting", "RUNNING" => "working", "COMPLETE" => "done", var other => other.ToLowerInvariant() },
                S(sub["assignedModelId"]),
                S(sub["title"]).StartsWith(VerifyPrefix) ? Strings.T("Check the work and fix what fails") : S(sub["title"]) is { Length: > 120 } long_ ? long_[..120] : S(sub["title"]))).ToList();
            var results = (row["results"] as JsonArray ?? []).OfType<JsonObject>().Where(result => !B(result["modelChange"])).Select(result => S(result["summary"])).Where(text => text.Length > 0).ToList();
            work = new WorkView(S(mega["title"]), S(mega["status"]).ToUpperInvariant(), steps.Count(step => step.Status == "done"), steps.Count, steps, results)
            {
                Row = row, Id = S(mega["id"]), CanResume = B(row["canResume"]),
                NextAction = new[] { S(mega["nextAction"]), S(row["next"]), S(row["label"]) }.FirstOrDefault(text => text.Length > 0) ?? "",
                StartedAt = S(mega["approvedAt"]) is { Length: > 0 } approved ? approved : S(mega["createdAt"]), CompletedAt = S(mega["completedAt"]),
                Waiting = subs.Select(sub => S(sub["status"]).ToUpperInvariant() == "PENDING" ? S(sub["waitingFor"]) : "").FirstOrDefault(text => text.Length > 0) ?? "",
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
        // Only a goal the lead suggested is offered to the crew: a plain question is not work to do.
        var nextGoal = lastAsk == null ? null : SuggestedGoal(S(lastAsk["answer"]));
        if (nextGoal != null && (nextGoal == goal || S(lastAsk?["question"]).Trim() == goal)) nextGoal = null;
        var suggestedAt = lastAsk != null && DateTimeOffset.TryParse(S(lastAsk["at"]), out var parsedAsk) ? parsedAsk : (DateTimeOffset?)null;
        if (nextGoal != null && !TeamViews.SuggestionIsCurrent(suggestedAt, work?.StartedAt)) nextGoal = null;

        // Between two works the crew is still on: the lead checks the result and decides the next step.
        var running = work != null && work.Status is "RUNNING" or "READY_FOR_APPROVAL" || B(row["canStop"]);
        (CrewAction action, string text, string hint) = working ? (CrewAction.StopTheCrew, Strings.T("Stop the crew"), running
                ? Strings.T("The crew is working. You can keep chatting with the lead and change models at any time.")
                : Strings.T("The work is done. The lead is checking the result and deciding the next step; Stop the crew if it should not go on."))
            : S(row["status"]) == "needs_approval" ? (CrewAction.Approve, Strings.T("Approve and start"), Strings.T("The lead proposed some work. Approve it, or tell the lead what to change."))
            : B(row["canStop"]) ? (CrewAction.StopWork, Strings.T("Stop work"), Strings.T("Work in progress. You can keep chatting with the lead."))
            : B(row["canResume"]) ? (CrewAction.Resume, Strings.T("Resume"), Strings.T("Work was stopped. Resume continues from the saved point, or drop it to start something new."))
            : (CrewAction.LetTheCrewDoIt, Strings.T("Let the crew do it"),
                stopReason.Length > 0 && stopReason != "GOAL_REACHED" ? StopSentence(stopReason)
                : goal.Length == 0 && nextGoal == null ? Strings.T("Talk with the lead about what to build: it suggests a goal, then press Let the crew do it.")
                : Strings.T("Ready. The crew will plan, build, check and stop when the goal is reached."));
        var timeLeft = work != null && stepsStillOpen(work) ? ChatViews.TimeLeft(row["timeLeft"], zone) : "";
        return new ProjectView(id, S(row["name"]), StatusOf(snapshot, row), messages, work, action, text, hint, nextGoal, S(row["megaTaskId"]) is { Length: > 0 } mt ? mt : null) { TimeLeft = timeLeft };
    }

    static bool stepsStillOpen(WorkView work) => work.Steps.Any(step => step.Status is "working" or "waiting" or "waiting for quota");
}
