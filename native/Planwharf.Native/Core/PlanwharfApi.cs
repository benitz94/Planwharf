using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

// The local server's HTTP API, the same one the chat page uses.
public sealed class PlanwharfApi(string address)
{
    readonly HttpClient http = new() { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(15) };

    public async Task<JsonNode> GetAsync(string path) => JsonNode.Parse(await http.GetStringAsync(path)) ?? new JsonObject();

    public async Task<JsonNode?> PostAsync(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(json?["error"]?.GetValue<string>() ?? json?["reason"]?.GetValue<string>() ?? $"Request failed ({(int)response.StatusCode}).");
        return json;
    }

    public Task<JsonNode> SnapshotAsync(bool readOnly) => GetAsync(readOnly ? "/api/orchestrator?readOnly=1" : "/api/orchestrator");
    public Task<JsonNode?> AskAsync(string projectId, string question) => PostAsync("/api/orchestrator/ask", new { projectId, question });
    public Task<JsonNode?> SetModeAsync(string mode) => PostAsync("/api/orchestrator/mode", new { mode });
    public Task<JsonNode?> SetAutonomyAsync(string projectId, bool enabled) => PostAsync("/api/orchestrator/autonomy", new { projectId, enabled });
    public Task<JsonNode?> StopAsync(string projectId) => PostAsync("/api/orchestrator/stop", new { projectId });
    public Task<JsonNode?> ResumeAsync(string projectId) => PostAsync("/api/orchestrator/resume", new { projectId });
    public Task<JsonNode?> ApproveAsync(string projectId, string megaTaskId) => PostAsync("/api/orchestrator/approve", new { projectId, megaTaskId });

    // The goal agreed in the chat becomes the project's goal before the crew starts.
    public async Task SaveGoalAsync(string projectId, string goal)
    {
        var plan = await GetAsync("/api/plan");
        await PostAsync("/api/revisions", new { version = plan["version"]?.GetValue<string>(), projectId, draft = new { title = "Goal from the chat", direction = goal, state = "Not started.", closed = Array.Empty<string>(), open = Array.Empty<string>() } });
    }
}
