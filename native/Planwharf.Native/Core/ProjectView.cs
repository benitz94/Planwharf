using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

public sealed record ProjectItem(string Id, string Name, string Status);
public sealed record ChatMessage(bool FromUser, string Who, string Text, DateTimeOffset At);
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
    public string Id { get; init; } = "";
}
public enum CrewAction { None, LetTheCrewDoIt, StopTheCrew, Approve, Resume, StopWork }
public sealed record ProjectView(string Id, string Name, string Status, IReadOnlyList<ChatMessage> Messages, WorkView? Work,
    CrewAction Action, string ActionText, string Hint, string? NextGoal, string? MegaTaskId);

// Reads the server's snapshot the way the chat page does: same labels, same next action, same stop sentences.
public static class ProjectViews
{
    static readonly Regex Suggested = new(@"^\s*\**Suggested next goal:\**\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    const string VerifyPrefix = "Verify the work independently";

    static readonly Dictionary<string, string> StopLabels = new()
    {
        ["GOAL_REACHED"] = "Goal reached", ["LEAD_UNCONFIRMED"] = "Finished: check the result", ["UNREADABLE_LEAD_DECISION"] = "Finished: check the result",
        ["MEGATASK_LIMIT"] = "Stopped: work limit reached", ["NO_LEAD_AVAILABLE"] = "Stopped: no lead available", ["LEAD_FAILED"] = "Stopped: the lead could not answer",
        ["START_UNAVAILABLE"] = "Stopped: could not start", ["START_FAILED"] = "Stopped: could not start", ["NOTHING_STARTED"] = "Stopped: no work started",
        ["APPROVAL_REFUSED"] = "Stopped: work not approved", ["AUTONOMY_ERROR"] = "Stopped: error", ["TIME_LIMIT"] = "Stopped: time limit reached"
    };
    static readonly Dictionary<string, string> StopSentences = new()
    {
        ["LEAD_UNCONFIRMED"] = "Work finished, but the lead did not confirm the goal. Check the result in the project folder.",
        ["UNREADABLE_LEAD_DECISION"] = "Work finished, but the lead did not confirm the goal. Check the result in the project folder.",
        ["MEGATASK_LIMIT"] = "The crew stopped after 5 pieces of work in a row. Check the result, then press Let the crew do it to go on.",
        ["NO_LEAD_AVAILABLE"] = "The crew stopped: no lead is available. Choose a lead with quota in Team.",
        ["LEAD_FAILED"] = "The crew stopped: the lead could not answer", ["START_UNAVAILABLE"] = "The crew could not start the work.",
        ["START_FAILED"] = "The crew could not start the work", ["NOTHING_STARTED"] = "The crew started no work",
        ["APPROVAL_REFUSED"] = "The work could not be approved", ["AUTONOMY_ERROR"] = "The crew stopped on an error",
        ["TIME_LIMIT"] = "The crew stopped: the time limit was reached."
    };

    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static string StopSentence(string reason)
    {
        var code = reason.Split(':')[0];
        var detail = reason.Length > code.Length ? reason[(code.Length + 1)..].Trim() : "";
        var sentence = StopSentences.GetValueOrDefault(code, "The crew stopped");
        return detail.Length > 0 ? $"{sentence.TrimEnd('.')}: {detail}" : sentence.EndsWith('.') ? sentence : sentence + ".";
    }

    public static string? SuggestedGoal(string answer) => Suggested.Match(answer) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    public static IReadOnlyList<ProjectItem> Projects(JsonNode snapshot) =>
        (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().Select(row => new ProjectItem(S(row["id"]), S(row["name"]), StatusOf(snapshot, row))).ToList();

    static string StatusOf(JsonNode snapshot, JsonObject row) => ChatViews.Status(snapshot, row).Text;

    public static ProjectView? Project(JsonNode snapshot, string? projectId)
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
            messages.Add(new ChatMessage(true, "You", S(ask["question"]), at));
            if (S(ask["answer"]).Length > 0) messages.Add(new ChatMessage(false, Label(S(ask["orchestratorId"])) is { Length: > 0 } lead ? lead : "Lead", S(ask["answer"]), at));
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
                S(sub["title"]).StartsWith(VerifyPrefix) ? "Check the work and fix what fails" : S(sub["title"]) is { Length: > 120 } long_ ? long_[..120] : S(sub["title"]))).ToList();
            var results = (row["results"] as JsonArray ?? []).OfType<JsonObject>().Where(result => !B(result["modelChange"])).Select(result => S(result["summary"])).Where(text => text.Length > 0).ToList();
            work = new WorkView(S(mega["title"]), S(mega["status"]).ToUpperInvariant(), steps.Count(step => step.Status == "done"), steps.Count, steps, results)
            {
                Row = row, Id = S(mega["id"]), CanResume = B(row["canResume"]),
                NextAction = new[] { S(mega["nextAction"]), S(row["next"]), S(row["label"]) }.FirstOrDefault(text => text.Length > 0) ?? "",
                StartedAt = S(mega["approvedAt"]) is { Length: > 0 } approved ? approved : S(mega["createdAt"]), CompletedAt = S(mega["completedAt"]),
                Waiting = subs.Select(sub => S(sub["status"]).ToUpperInvariant() == "PENDING" ? S(sub["waitingFor"]) : "").FirstOrDefault(text => text.Length > 0) ?? ""
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

        (CrewAction action, string text, string hint) = working ? (CrewAction.StopTheCrew, "Stop the crew", "The crew is working. You can keep chatting with the lead and change models at any time.")
            : S(row["status"]) == "needs_approval" ? (CrewAction.Approve, "Approve and start", "The lead proposed some work. Approve it, or tell the lead what to change.")
            : B(row["canStop"]) ? (CrewAction.StopWork, "Stop work", "Work in progress. You can keep chatting with the lead.")
            : B(row["canResume"]) ? (CrewAction.Resume, "Resume", "Work was stopped. Resume continues from the saved point, or drop it to start something new.")
            : (CrewAction.LetTheCrewDoIt, "Let the crew do it",
                stopReason.Length > 0 && stopReason != "GOAL_REACHED" ? StopSentence(stopReason)
                : goal.Length == 0 && nextGoal == null ? "Talk with the lead about what to build: it suggests a goal, then press Let the crew do it."
                : "Ready. The crew will plan, build, check and stop when the goal is reached.");
        return new ProjectView(id, S(row["name"]), StatusOf(snapshot, row), messages, work, action, text, hint, nextGoal, S(row["megaTaskId"]) is { Length: > 0 } mt ? mt : null);
    }
}
