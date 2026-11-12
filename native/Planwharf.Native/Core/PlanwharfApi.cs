using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

// The local server's HTTP API, the same one the chat page uses.
public sealed class PlanwharfApi(string address)
{
    readonly HttpClient http = new() { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(15) };

    public async Task<JsonNode> GetAsync(string path) => JsonNode.Parse(await http.GetStringAsync(path)) ?? new JsonObject();

    public async Task<JsonNode?> PostAsync(string path, object body)
    {
        // The server accepts exactly "application/json", without a charset.
        using var content = new StringContent(JsonSerializer.Serialize(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(json?["error"]?.GetValue<string>() ?? json?["reason"]?.GetValue<string>() ?? $"Request failed ({(int)response.StatusCode}).");
        return json;
    }

    // With a project, the snapshot shows that project's own team (lead and roles).
    public Task<JsonNode> SnapshotAsync(bool readOnly, string? team = null) =>
        GetAsync((readOnly ? "/api/orchestrator?readOnly=1" : "/api/orchestrator?x=1") + (team != null ? "&team=" + Uri.EscapeDataString(team) : ""));
    public Task<JsonNode?> AskAsync(string projectId, string question) => PostAsync("/api/orchestrator/ask", new { projectId, question });
    public Task<JsonNode?> SetModeAsync(string mode) => PostAsync("/api/orchestrator/mode", new { mode });
    public Task<JsonNode?> SetAutonomyAsync(string projectId, bool enabled) => PostAsync("/api/orchestrator/autonomy", new { projectId, enabled });
    public Task<JsonNode?> StopAsync(string projectId) => PostAsync("/api/orchestrator/stop", new { projectId });
    public Task<JsonNode?> ResumeAsync(string projectId) => PostAsync("/api/orchestrator/resume", new { projectId });
    public Task<JsonNode?> ApproveAsync(string projectId, string megaTaskId) => PostAsync("/api/orchestrator/approve", new { projectId, megaTaskId });

    async Task<string?> PlanVersionAsync() => (await GetAsync("/api/plan"))["version"]?.GetValue<string>();

    public const string NoGoal = "Not set yet: tell the lead in the chat what to build.";

    // A new project needs only a name, and an existing folder or none (then Planwharf creates one): what to build is said later, in the chat.
    public async Task<string?> CreateProjectAsync(string name, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) && (await ProjectsRootAsync()).Length == 0)
            throw new InvalidOperationException(OperatingSystem.IsWindows() ? @"Enter the folder for this project, for example C:\Projects\my-website, or set the projects folder in Settings." : "Enter the folder for this project, for example ~/Projects/my-website, or set the projects folder in Settings.");
        object project = string.IsNullOrWhiteSpace(folder) ? new { name, direction = NoGoal } : new { name, direction = NoGoal, folder };
        var created = await PostAsync("/api/projects", new { version = await PlanVersionAsync(), project, createFolder = string.IsNullOrWhiteSpace(folder) });
        return (created?["plan"]?["projects"] as JsonArray)?.LastOrDefault()?["project"]?["id"]?.GetValue<string>();
    }

    // Where Planwharf creates the folders of new projects that have none.
    public async Task<string> ProjectsRootAsync() => (await GetAsync("/api/settings"))["projectsRoot"]?.GetValue<string>() ?? "";
    public Task<JsonNode?> SetProjectsRootAsync(string folder) => PostAsync("/api/settings", new { projectsRoot = folder });

    public async Task SetFolderAsync(string projectId, string folder) =>
        await PostAsync("/api/place", new { version = await PlanVersionAsync(), project = new { id = projectId, folder } });

    // The lead needs the orchestrator role in this project's team.
    public async Task SetLeadAsync(string projectId, string modelId, bool hasRole)
    {
        if (!hasRole) await PostAsync("/api/models/roles", new { id = modelId, roles = new Dictionary<string, bool> { ["orchestrator"] = true }, projectId });
        await PostAsync("/api/orchestrator/lead", new { id = modelId, projectId });
    }

    public Task<JsonNode?> SetRolesAsync(string projectId, string modelId, IDictionary<string, bool> roles) => PostAsync("/api/models/roles", new { id = modelId, roles, projectId });

    // Changes the model of one step; a working step restarts on it with a handoff.
    public Task<JsonNode?> ChangeStepModelAsync(string projectId, string stepId, string modelId) =>
        PostAsync("/api/orchestrator/crew", new { projectId, assignments = new Dictionary<string, string> { [stepId] = modelId } });

    // The project folder in git: changes not committed, commits not pushed.
    public async Task<JsonNode?> RepositoryAsync(string projectId, bool refresh = false) =>
        (await GetAsync("/api/repository?projectId=" + Uri.EscapeDataString(projectId) + (refresh ? "&refresh=1" : "")))["repository"];
    // The user's own commit, and push when asked: never a force push (the server decides how).
    public Task<JsonNode?> CommitAsync(string projectId, string message, bool push) => PostAsync("/api/repository/commit", new { projectId, message, push });
    public Task<JsonNode?> IgnoreAlertAsync(string projectId, string alertId) => PostAsync("/api/supervisor/ignore", new { projectId, alertId });

    // The goal agreed in the chat becomes the project's goal before the crew starts.
    public async Task SaveGoalAsync(string projectId, string goal)
    {
        var plan = await GetAsync("/api/plan");
        await PostAsync("/api/revisions", new { version = plan["version"]?.GetValue<string>(), projectId, draft = new { title = "Goal from the chat", direction = goal, state = "Not started.", closed = Array.Empty<string>(), open = Array.Empty<string>() } });
    }
}
