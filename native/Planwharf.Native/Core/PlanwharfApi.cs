using Planwharf.Engine;
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
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(json?["error"]?.GetValue<string>() ?? json?["reason"]?.GetValue<string>() ?? Strings.Format("Request failed ({0}).", (int)response.StatusCode));
        return json;
    }

    // With a project, the snapshot shows that project's own team (lead and roles).
    public Task<JsonNode> SnapshotAsync(bool readOnly, string? team = null) =>
        GetAsync((readOnly ? "/api/orchestrator?readOnly=1" : "/api/orchestrator?x=1") + (team != null ? "&team=" + Uri.EscapeDataString(team) : ""));
    // One chat session per project and window, as the chat page sends it.
    public Task<JsonNode?> AskAsync(string projectId, string question, string? chatSessionId = null) => chatSessionId == null
        ? PostAsync("/api/orchestrator/ask", new { projectId, question })
        : PostAsync("/api/orchestrator/ask", new { projectId, question, chatSessionId });
    public Task<JsonNode?> SetModeAsync(string mode) => PostAsync("/api/orchestrator/mode", new { mode });
    // One call for the chat switch. The engine turns Auto, Talk and Plan on together with autonomy.
    public Task<JsonNode?> SetChatModeAsync(string projectId, string mode) => PostAsync("/api/orchestrator/chat-mode", new { projectId, mode });
    public Task<JsonNode?> SetSavingModeAsync(string projectId, string mode) => PostAsync("/api/orchestrator/saving-mode", new { projectId, mode });
    public Task<JsonNode?> SetAutonomyAsync(string projectId, bool enabled) => PostAsync("/api/orchestrator/autonomy", new { projectId, enabled });
    public Task<JsonNode?> StopAsync(string projectId) => PostAsync("/api/orchestrator/stop", new { projectId });
    public Task<JsonNode?> ResumeAsync(string projectId) => PostAsync("/api/orchestrator/resume", new { projectId });
    // Closes stopped work so a new goal can start; the files in the project folder stay as they are.
    public Task<JsonNode?> DropWorkAsync(string projectId) => PostAsync("/api/orchestrator/reset", new { projectId });
    public Task<JsonNode?> ApproveAsync(string projectId, string megaTaskId) => PostAsync("/api/orchestrator/approve", new { projectId, megaTaskId });

    async Task<string?> PlanVersionAsync() => (await GetAsync("/api/plan"))["version"]?.GetValue<string>();

    // Server-recognized sentinel, persisted in the project protocol.
    public const string NoGoal = "Not set yet: tell the lead in the chat what to build.";

    // A new project needs only a name, and an existing folder or none (then Planwharf creates one): what to build is said later, in the chat.
    public async Task<string?> CreateProjectAsync(string name, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) && (await ProjectsRootAsync()).Length == 0)
            throw new InvalidOperationException(OperatingSystem.IsWindows() ? Strings.T("Enter the folder for this project, for example C:\\Projects\\my-website, or set the projects folder in Settings.") : Strings.T("Enter the folder for this project, for example ~/Projects/my-website, or set the projects folder in Settings."));
        object project = string.IsNullOrWhiteSpace(folder) ? new { name, direction = NoGoal } : new { name, direction = NoGoal, folder };
        var created = await PostAsync("/api/projects", new { version = await PlanVersionAsync(), project, createFolder = string.IsNullOrWhiteSpace(folder) });
        return (created?["plan"]?["projects"] as JsonArray)?.LastOrDefault()?["project"]?["id"]?.GetValue<string>();
    }

    // Where Planwharf creates the folders of new projects that have none.
    public async Task<string> ProjectsRootAsync() => (await GetAsync("/api/settings"))["projectsRoot"]?.GetValue<string>() ?? "";
    public Task<JsonNode> SettingsAsync() => GetAsync("/api/settings");
    public Task<JsonNode?> SetKeepWorkPlanAsync(bool keep) => PostAsync("/api/settings", new { keepWorkPlan = keep });
    public Task<JsonNode?> SetProjectsRootAsync(string folder) => PostAsync("/api/settings", new { projectsRoot = folder });

    public Task SetFolderAsync(string projectId, string folder) => SetPlaceAsync(projectId, folder, null);

    // The folder, and the GitHub link when there is one (none removes it).
    public async Task SetPlaceAsync(string projectId, string folder, string? github)
    {
        object project = string.IsNullOrWhiteSpace(github) ? new { id = projectId, folder } : new { id = projectId, folder, github };
        await PostAsync("/api/place", new { version = await PlanVersionAsync(), project });
    }

    // Takes a model out of this project's team: no role at all.
    public Task<JsonNode?> BenchAsync(string projectId, string modelId) => SetRolesAsync(projectId, modelId,
        new[] { "orchestrator", "worker", "verifier", "reviewer", "supervisor" }.ToDictionary(role => role, _ => false));

    // An AI tool signed in on this computer, on or off; off is never used.
    public Task<JsonNode?> SetProviderEnabledAsync(string providerId, bool enabled) => PostAsync("/api/catalog/settings", new { providerId, enabled });
    // The user's OK for one AI tool to edit files and run commands without asking each time.
    public Task<JsonNode?> SetWorksUnaskedAsync(string providerId, bool on) => PostAsync("/api/providers/policy", new { id = providerId, policy = new { alwaysApprove = on } });
    public Task<JsonNode?> RefreshModelsAsync() => PostAsync("/api/models/refresh", new { });
    public Task<JsonNode?> AddAccountAsync(string providerId) => PostAsync("/api/accounts", new { providerId });

    // A model reached through an API. The key stays in an environment variable; only its name is stored.
    public Task<JsonNode?> AddConnectionAsync(string label, string baseUrl, string apiProtocol, string? authEnv, IReadOnlyList<string> modelIds, bool toolsEnabled)
    {
        var id = System.Text.RegularExpressions.Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (id.Length == 0 || !char.IsAsciiLetterLower(id[0])) id = "api-" + id;
        id = id.TrimEnd('-');
        id = id[..Math.Min(60, id.Length)];
        var input = new Dictionary<string, object> { ["id"] = id, ["label"] = label, ["adapter"] = "api", ["baseUrl"] = baseUrl, ["apiProtocol"] = apiProtocol, ["modelIds"] = modelIds, ["toolsEnabled"] = toolsEnabled };
        if (!string.IsNullOrWhiteSpace(authEnv)) input["authEnv"] = authEnv;
        return PostAsync("/api/providers/connections", input);
    }

    // Extra tools for one project's team (mcp, browser, hooks, memory), all off by default.
    public Task<JsonNode?> SetToolsAsync(string projectId, IDictionary<string, bool>? tools, string? modelId = null) => modelId == null
        ? PostAsync("/api/projects/tools", new { projectId, tools })
        : PostAsync("/api/projects/tools", new { projectId, tools, modelId });

    public Task<JsonNode> PhoneSettingsAsync() => GetAsync("/api/settings/phone");
    public Task<JsonNode?> SetPhoneSettingsAsync(object body) => PostAsync("/api/settings/phone", body);
    public Task<JsonNode?> TestPhoneAlertAsync() => PostAsync("/api/settings/phone/test", new { });
    public Task<JsonNode?> ProbeOllamaAsync() => PostAsync("/api/settings/phone/ollama", new { });

    public Task<JsonNode> AutoSwitchAsync() => GetAsync("/api/orchestrator/auto-switch");
    public Task<JsonNode?> SetAutoSwitchAsync(bool enabled, int? fiveHourPercent = null, int? weeklyPercent = null) =>
        fiveHourPercent is { } five && weeklyPercent is { } week ? PostAsync("/api/orchestrator/auto-switch", new { enabled, fiveHourPercent = five, weeklyPercent = week }) : PostAsync("/api/orchestrator/auto-switch", new { enabled });
    // A model's own quota limit, no limit at all, or back to the general one.
    public Task<JsonNode?> SetQuotaLimitAsync(string id, int fiveHourPercent, int weeklyPercent) => PostAsync("/api/models/quota-limit", new { id, fiveHourPercent, weeklyPercent });
    public Task<JsonNode?> SetNoQuotaLimitAsync(string id) => PostAsync("/api/models/quota-limit", new { id, none = true });
    public Task<JsonNode?> ResetQuotaLimitAsync(string id) => PostAsync("/api/models/quota-limit", new { id, reset = true });
    // The limit of a whole company: every model of that quota pool (claude, chatgpt…) that has no limit of its own.
    public Task<JsonNode?> SetCompanyLimitAsync(string pool, int fiveHourPercent, int weeklyPercent) => PostAsync("/api/models/quota-limit", new { pool, fiveHourPercent, weeklyPercent });
    public Task<JsonNode?> SetCompanyNoLimitAsync(string pool) => PostAsync("/api/models/quota-limit", new { pool, none = true });
    public Task<JsonNode?> ResetCompanyLimitAsync(string pool) => PostAsync("/api/models/quota-limit", new { pool, reset = true });

    public Task<JsonNode> DecisionsAsync() => GetAsync("/api/decisions");
    public Task<JsonNode?> AnswerDecisionAsync(string projectId, string decisionId, string answer) =>
        PostAsync("/api/decisions/answer", new { projectId, decisionId, answer });
    public Task<JsonNode?> DelegateDecisionAsync(string projectId, string decisionId) =>
        PostAsync("/api/decisions/delegate", new { projectId, decisionId });
    public Task<JsonNode?> SetDecisionAutoAsync(string projectId, bool enabled) =>
        PostAsync("/api/decisions/auto", new { projectId, enabled });
    public Task<JsonNode?> SetDefaultDecisionAutoAsync(bool defaultEnabled) =>
        PostAsync("/api/decisions/auto", new { defaultEnabled });

    public Task<JsonNode> SkillsAsync() => GetAsync("/api/skills");
    public Task<JsonNode?> OpenSkillsAsync(string cli, string what) => PostAsync("/api/skills/open", new { cli, what });

    // A project to a file and back: the plan, history and saved points; the code stays in its folder.
    public Task<string> ExportProjectAsync(string projectId) => http.GetStringAsync("/api/project-export?projectId=" + Uri.EscapeDataString(projectId));
    public async Task<(string? ProjectId, bool Continue)> ImportProjectAsync(string file)
    {
        JsonNode bundle;
        try { bundle = JsonNode.Parse(file) ?? throw new FormatException(); }
        catch (Exception error) when (error is FormatException or JsonException) { throw new InvalidOperationException(Strings.T("This is not a Planwharf project file.")); }
        var result = await PostAsync("/api/project-import", new { version = await PlanVersionAsync(), bundle });
        // "continue" is the saved point to resume from, or null.
        return (result?["projectId"]?.GetValue<string>(), result?["continue"] is JsonObject || result?["continue"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag);
    }

    public async Task StartOverAsync(string projectId) => await PostAsync("/api/start-over", new { version = await PlanVersionAsync(), projectId });
    public async Task RemoveProjectAsync(string projectId) => await PostAsync("/api/remove-project", new { version = await PlanVersionAsync(), projectId });

    public async Task<JsonNode> UpdatesAsync(bool check) => check ? await PostAsync("/api/app/updates/check", new { }) ?? new JsonObject() : await GetAsync("/api/app/updates");

    // The lead needs the orchestrator role in this project's team.
    // Without a project (no project yet), the lead and roles are the default team's.
    public async Task SetLeadAsync(string? projectId, string modelId, bool hasRole)
    {
        if (!hasRole) await SetRolesAsync(projectId, modelId, new Dictionary<string, bool> { ["orchestrator"] = true });
        await PostAsync("/api/orchestrator/lead", projectId == null ? new { id = modelId } : new { id = modelId, projectId });
    }

    // The Lead button in Team: whether this model may lead the project (the menu and every hand-over).
    public Task<JsonNode?> SetLeadAllowedAsync(string? projectId, string modelId, bool allowed) =>
        PostAsync("/api/orchestrator/lead-allowed", projectId == null ? new { id = modelId, allowed } : new { id = modelId, allowed, projectId });

    public Task<JsonNode?> SetRolesAsync(string? projectId, string modelId, IDictionary<string, bool> roles) =>
        PostAsync("/api/models/roles", projectId == null ? new { id = modelId, roles } : new { id = modelId, roles, projectId });

    // Team's "All" and "None": one role on or off for the models shown.
    public Task<JsonNode?> SetRolesForManyAsync(string? projectId, IReadOnlyList<string> ids, string role, bool enabled) =>
        PostAsync("/api/models/roles/many", projectId == null ? new { ids, roles = new Dictionary<string, bool> { [role] = enabled } } : new { ids, roles = new Dictionary<string, bool> { [role] = enabled }, projectId });

    // Changes the model of one step; a working step restarts on it with a handoff.
    public Task<JsonNode?> ChangeStepModelAsync(string projectId, string stepId, string modelId) =>
        PostAsync("/api/orchestrator/crew", new { projectId, assignments = new Dictionary<string, string> { [stepId] = modelId } });

    // The project folder in git: changes not committed, commits not pushed.
    public async Task<JsonNode?> RepositoryAsync(string projectId, bool refresh = false) =>
        (await GetAsync("/api/repository?projectId=" + Uri.EscapeDataString(projectId) + (refresh ? "&refresh=1" : "")))["repository"];
    // The user's own commit, and push when asked: never a force push (the server decides how).
    public Task<JsonNode?> CommitAsync(string projectId, string message, bool push) => PostAsync("/api/repository/commit", new { projectId, message, push });
    public Task<JsonNode?> IgnoreAlertAsync(string projectId, string alertId) => PostAsync("/api/supervisor/ignore", new { projectId, alertId });

    // Adds an agreed addition as one more Build step before Verify.
    public Task<JsonNode?> AddToWorkAsync(string projectId, string? addition = null) =>
        PostAsync("/api/orchestrator/add-to-work", addition == null ? new { projectId } : (object)new { projectId, addition });

    // The goal agreed in the chat becomes the project's goal before the crew starts.
    public async Task SaveGoalAsync(string projectId, string goal)
    {
        var plan = await GetAsync("/api/plan");
        await PostAsync("/api/revisions", new { version = plan["version"]?.GetValue<string>(), projectId, draft = new { title = Strings.T("Goal from the chat"), direction = goal, state = Strings.T("Not started."), closed = Array.Empty<string>(), open = Array.Empty<string>() } });
    }
}
