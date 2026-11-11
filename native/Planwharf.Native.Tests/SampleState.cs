using System.Text.Json.Nodes;

namespace Planwharf.Native.Tests;

// A sample of the server's state: one project with a chat and finished work, one idle project.
static class SampleState
{
    public static JsonNode Finished(string? stopReason = "GOAL_REACHED", bool enabled = true) => JsonNode.Parse($$"""
    {
      "mode": "auto",
      "autonomy": { "weather": { "enabled": {{(enabled ? "true" : "false")}}, "stopReason": {{(stopReason == null ? "null" : "\"" + stopReason + "\"")}} } },
      "models": [ { "id": "opus", "label": "Opus 5.5" }, { "id": "astra", "label": "GPT-6 Astra" }, { "id": "grok", "label": "Grok 4.7" } ],
      "asks": [ { "at": "2026-11-11T15:40:00Z", "projectId": "weather", "orchestratorId": "grok",
        "question": "I want a 7-day forecast page with charts.",
        "answer": "Good plan: the app already has **24 files** and `src/api.js` fetches the data.\n\nSuggested next goal: Add a 7-day forecast page with charts and tests." } ],
      "projects": [
        { "id": "weather", "name": "Weather dashboard", "label": "Done", "status": "complete", "goal": "Add a 7-day forecast page with charts and tests.",
          "megaTaskId": "MT-1", "megaTask": { "id": "MT-1", "title": "Add a 7-day forecast page with charts and tests.", "status": "COMPLETE" },
          "subTasks": [
            { "id": "s1", "title": "Implement the whole job.", "status": "COMPLETE", "assignedModelId": "opus" },
            { "id": "s2", "title": "Verify the work independently against the job.", "status": "COMPLETE", "assignedModelId": "astra" } ],
          "results": [ { "subTaskId": "s1", "status": "COMPLETE", "summary": "Added the page and 9 tests." },
                       { "subTaskId": "s2", "status": "BLOCKED", "summary": "Model changed.", "modelChange": true },
                       { "subTaskId": "s2", "status": "COMPLETE", "summary": "Covered time zones; 47 tests pass." } ] },
        { "id": "notes", "name": "Recipe notes app", "label": "Ready", "status": "idle", "goal": "" } ]
    }
    """)!;
}
