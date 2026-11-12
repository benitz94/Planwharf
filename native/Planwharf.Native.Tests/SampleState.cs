using System.Text.Json.Nodes;

namespace Planwharf.Native.Tests;

// A sample of the server's state: one project with a chat and finished work, one idle project.
static class SampleState
{
    public static JsonNode Finished(string? stopReason = "GOAL_REACHED", bool enabled = true) => JsonNode.Parse($$"""
    {
      "aiUsage": { "recent": [
        { "megaTaskId": "MT-1", "modelId": "opus", "durationMs": 540000, "tokens": { "input": 182000, "output": 21400, "cachedInput": 140000 } },
        { "megaTaskId": "MT-1", "modelId": "astra", "durationMs": 260000 } ] },
      "mode": "auto",
      "autonomy": { "weather": { "enabled": {{(enabled ? "true" : "false")}}, "stopReason": {{(stopReason == null ? "null" : "\"" + stopReason + "\"")}} } },
      "models": [ { "id": "opus", "label": "Opus 5.5" }, { "id": "astra", "label": "GPT-6 Astra" }, { "id": "grok", "label": "Grok 4.7" } ],
      "asks": [ { "at": "2026-11-11T15:40:00Z", "projectId": "weather", "orchestratorId": "grok",
        "question": "I want a 7-day forecast page with charts.",
        "answer": "Good plan: the app already has **24 files** and `src/api.js` fetches the data.\n\nSuggested next goal: Add a 7-day forecast page with charts and tests." } ],
      "projects": [
        { "id": "weather", "name": "Weather dashboard", "label": "Done", "status": "complete", "goal": "Add a 7-day forecast page with charts and tests.",
          "megaTaskId": "MT-1", "megaTask": { "id": "MT-1", "title": "Add a 7-day forecast page with charts and tests.", "status": "COMPLETE", "approvedAt": "2026-11-11T15:41:00Z", "completedAt": "2026-11-11T15:55:00Z" },
          "subTasks": [
            { "id": "s1", "title": "Implement the whole job.", "status": "COMPLETE", "assignedModelId": "opus" },
            { "id": "s2", "title": "Verify the work independently against the job.", "status": "COMPLETE", "assignedModelId": "astra" } ],
          "results": [ { "subTaskId": "s1", "status": "COMPLETE", "summary": "Added the page and 9 tests.", "testsReported": 1 },
                       { "subTaskId": "s2", "status": "BLOCKED", "summary": "Model changed.", "modelChange": true },
                       { "subTaskId": "s2", "status": "COMPLETE", "summary": "Covered time zones; 47 tests pass." } ] },
        { "id": "notes", "name": "Recipe notes app", "label": "Ready", "status": "idle", "goal": "" } ]
    }
    """)!;

    // The same project while the crew works, with its team: a lead, roles, quota left and a model that is not available.
    public static JsonNode Working()
    {
        var state = Finished(stopReason: null).AsObject();
        state["orchestratorId"] = "grok";
        state["plans"] = JsonNode.Parse("""[ { "id": "max", "readable": true, "leftPct": 71 }, { "id": "plus", "readable": true, "leftPct": 38 } ]""");
        state["models"] = JsonNode.Parse("""
        [ { "id": "opus", "label": "Opus 5.5", "plan": "max", "enabled": true, "roles": { "worker": true } },
          { "id": "astra", "label": "GPT-6 Astra", "plan": "plus", "enabled": true, "roles": { "verifier": true } },
          { "id": "grok", "label": "Grok 4.7", "plan": "supergrok", "enabled": true, "roles": { "orchestrator": true, "worker": true } },
          { "id": "gemini", "label": "Gemini 3.5 Pro", "plan": "google", "enabled": true, "available": false, "unavailableReason": "CLI not installed", "roles": {} },
          { "id": "sonnet", "label": "Sonnet 5.5", "plan": "max", "enabled": true, "roles": {} } ]
        """);
        state["providers"] = JsonNode.Parse("""[ { "id": "claude", "label": "Claude Code", "models": [ {}, {} ] }, { "id": "codex", "label": "Codex", "models": [ {} ] }, { "id": "grok", "label": "Grok", "models": [ {} ] }, { "id": "gemini", "label": "Gemini CLI", "enabled": false, "models": [ {} ] } ]""");
        var project = state["projects"]![0]!.AsObject();
        project["folder"] = "/home/sample/weather";
        project["label"] = "Working"; project["status"] = "running";
        project["megaTask"]!["status"] = "RUNNING";
        project["subTasks"]![1]!["status"] = "RUNNING";
        project["dispatch"] = JsonNode.Parse("""
            [ { "alive": true, "modelId": "astra", "activitySession": { "events": [
            { "friendly": "Reading src/forecast.js" }, { "friendly": "Running npm test" }, { "friendly": "Running npm test" }, { "friendly": "Fixing the time zone in the chart labels" } ] } } ]
            """);
        project["megaTask"]!["approvedAt"] = DateTimeOffset.UtcNow.AddMinutes(-14).ToString("o");
        state["supervisor"] = JsonNode.Parse("""{ "projects": [ { "projectId": "weather", "alerts": [ { "id": "a1", "title": "Two steps edit the same file", "message": "src/forecast.js is changed by Build and Verify." } ] } ] }""");
        project["results"] = new JsonArray(JsonNode.Parse("""{ "subTaskId": "s1", "status": "COMPLETE", "summary": "Added the page and 9 tests." }"""));
        return state;
    }
}
