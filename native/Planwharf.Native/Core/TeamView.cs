using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record TeamModel(string Id, string Label, string Provider, int? QuotaLeft, bool Available, string? UnavailableReason,
    bool Lead, bool Build, bool Verify, bool Review, bool CanLead)
{
    public bool Enabled { get; init; } = true;
    // On the bench: no role at all in this project.
    public bool Bench { get; init; }
    public bool CanBuild { get; init; } = true;
    public bool CanVerify { get; init; } = true;
    public bool CanReview { get; init; } = true;
    // Not available now, and Planwharf tries it again in a few minutes.
    public bool RetrySoon { get; init; }
    public string ProviderId { get; init; } = "";
    public string Adapter { get; init; } = "";
    public bool ProviderEnabled { get; init; } = true;
    // The user's OK to edit files and run commands without asking each time (off until given).
    public bool WorksUnasked { get; init; }
    // What is left on each quota window, the model's limit (its own, the general one, or none), and whether it is under it.
    public int? FiveHourLeft { get; init; }
    public int? WeeklyLeft { get; init; }
    public bool LowQuota { get; init; }
    public bool LimitNone { get; init; }
    public bool LimitCustom { get; init; }
    public int? LimitFiveHour { get; init; }
    public int? LimitWeekly { get; init; }
    // Light, default or premium; and whether the AI tools on this computer no longer offer the model.
    public string Weight { get; init; } = "default";
    public bool NotOffered { get; init; }
}
public sealed record ProviderView(string Id, string Label, bool Enabled, int Models)
{
    public bool WorksUnasked { get; init; }
    // False when the last check found no program for this AI tool on this computer.
    public bool Installed { get; init; } = true;
    public string? InstallHint { get; init; }
}
public sealed record TeamView(string? LeadId, IReadOnlyList<TeamModel> Models)
{
    public IReadOnlyList<string> LeadOrder { get; init; } = [];

    public IReadOnlyList<ProviderView> Providers { get; init; } = [];
    // Who takes over as lead when its quota runs low, in order.
    public IReadOnlyList<string> LeadBackups { get; init; } = [];
}

// The project's team as the chat page shows it: the lead, and who can build, verify or review, with quota left.
public static class TeamViews
{
    static string S(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static int? QuotaLeft(JsonNode snapshot, JsonObject model)
    {
        var pool = S(model["quotaPoolId"]) is { Length: > 0 } id ? id : S(model["plan"]);
        var plan = (snapshot["plans"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => S(item["id"]) == pool);
        if (plan == null || !B(plan["readable"])) return null;
        return plan["leftPct"] is JsonValue left && left.TryGetValue<double>(out var value) ? (int)Math.Round(value) : null;
    }

    public static TeamView Team(JsonNode snapshot)
    {
        var lead = S(snapshot["orchestratorId"]) is { Length: > 0 } id ? id : null;
        // Every model, as the chat page lists them: on the team, or on the bench.
        bool Eligible(JsonObject model, string role) => model["roleEligibility"]?[role] is not JsonValue value || !value.TryGetValue<bool>(out var ok) || ok;
        var models = (snapshot["models"] as JsonArray ?? []).OfType<JsonObject>().Select(model =>
        {
            var roles = model["roles"];
            var label = S(model["label"]) is { Length: > 0 } text ? text : S(model["id"]);
            return new TeamModel(S(model["id"]), label, S(model["plan"]) is { Length: > 0 } plan ? plan : S(model["provider"]), QuotaLeft(snapshot, model),
                model["available"] is not JsonValue available || !available.TryGetValue<bool>(out var ok) || ok, S(model["unavailableReason"]) is { Length: > 0 } why ? why : null,
                S(model["id"]) == lead, B(roles?["worker"]), B(roles?["verifier"]), B(roles?["reviewer"]), Eligible(model, "orchestrator"))
            {
                Enabled = model["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || on,
                Bench = !new[] { "orchestrator", "worker", "verifier", "reviewer", "supervisor" }.Any(role => B(roles?[role])),
                CanBuild = Eligible(model, "worker"), CanVerify = Eligible(model, "verifier"), CanReview = Eligible(model, "reviewer"),
                RetrySoon = model["unavailableAt"] != null,
                ProviderId = S(model["providerId"]) is { Length: > 0 } providerId ? providerId : S(model["provider"]),
                Adapter = S(model["adapter"]) is { Length: > 0 } adapter ? adapter : S(model["provider"]),
                ProviderEnabled = model["providerEnabled"] is not JsonValue pe || !pe.TryGetValue<bool>(out var providerOn) || providerOn,
                WorksUnasked = B(model["alwaysApprove"]),
                FiveHourLeft = Pct(model["quotaWindows"]?["fiveHour"]), WeeklyLeft = Pct(model["quotaWindows"]?["weekly"]),
                LowQuota = B(model["lowQuota"]),
                LimitNone = B(model["quotaLimit"]?["none"]), LimitCustom = B(model["quotaLimit"]?["custom"]),
                LimitFiveHour = Pct(model["quotaLimit"]?["fiveHourPercent"]), LimitWeekly = Pct(model["quotaLimit"]?["weeklyPercent"]),
                Weight = S(model["weight"]) is { Length: > 0 } weight ? weight : "default",
                NotOffered = B(model["catalogExcluded"])
            };
        }).ToList();
        var providers = (snapshot["providers"] as JsonArray ?? []).OfType<JsonObject>().Select(provider => new ProviderView(S(provider["id"]),
            S(provider["label"]) is { Length: > 0 } label ? label : S(provider["id"]), provider["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on,
            (provider["models"] as JsonArray)?.Count ?? 0)
            {
                WorksUnasked = B(provider["policy"]?["alwaysApprove"]),
                Installed = provider["installed"] is not JsonValue installed || !installed.TryGetValue<bool>(out var found) || found,
                InstallHint = S(provider["installHint"]) is { Length: > 0 } hint ? hint : null
            }).ToList();
        var backups = (snapshot["leadBackups"] as JsonArray ?? []).Select(id => S(id)).Where(id => id.Length > 0)
            .Select(id => models.FirstOrDefault(model => model.Id == id)?.Label ?? id).ToList();
        // Every model that can lead now, best first (the lead menu follows it).
        var order = (snapshot["leadOrder"] as JsonArray ?? []).Select(id => S(id)).Where(id => id.Length > 0).ToList();
        return new TeamView(lead, models) { Providers = providers, LeadBackups = backups, LeadOrder = order };
    }

    // The models that can lead, best first as the ranking Planwharf uses to hand over.
    public static List<TeamModel> Leads(TeamView team)
    {
        int Rank(TeamModel model) { var at = team.LeadOrder.ToList().IndexOf(model.Id); return at < 0 ? int.MaxValue : at; }
        return team.Models.Where(model => model.CanLead && model.Available).Select((model, i) => (model, i)).OrderBy(x => Rank(x.model)).ThenBy(x => x.i).Select(x => x.model).ToList();
    }

    static int? Pct(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? (int)Math.Round(number) : null;

    // The role a step needs, and the models of the team that have it (as the web step menu).
    public static IReadOnlyList<TeamModel> ModelsFor(TeamView team, string role) => team.Models.Where(model => model.Available && model.Enabled && role switch
    {
        "Verify" => model.Verify || model.Build,
        "Review" => model.Review,
        _ => model.Build
    }).ToList();

    // Claude Code, Grok and Antigravity cannot build without working unasked; Codex works inside its sandbox.
    static readonly string[] NeedsOk = ["claude", "grok", "antigravity"];

    // The AI tools that need the user's OK before the crew (or, for Antigravity, the lead) can work.
    public static IReadOnlyList<ProviderView> ToolsNeedingOk(TeamView team, bool forLead = false)
    {
        var models = forLead ? team.Models.Where(model => model.Id == team.LeadId)
            : team.Models.Where(model => model.Enabled && model.Available && model.ProviderEnabled && (model.Build || model.Verify || model.Review));
        return models.Where(model => !model.WorksUnasked && (forLead ? model.Adapter == "antigravity" : NeedsOk.Contains(model.Adapter)))
            .Select(model => model.ProviderId).Distinct()
            .Select(id => team.Providers.FirstOrDefault(provider => provider.Id == id) ?? new ProviderView(id, id, true, 0)).ToList();
    }

    public static string OkQuestion(IReadOnlyList<ProviderView> tools)
    {
        var names = tools.Select(tool => tool.Label).ToList();
        var list = names.Count < 2 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        var one = names.Count == 1;
        return $"{list} will work in the project folder without asking each time: {(one ? "it edits" : "they edit")} files and {(one ? "runs" : "run")} commands, such as tests and installs, with network access. Without this {(one ? "it" : "they")} cannot build.\n\nYou can change this at any time in Team, under AI tools.";
    }

    public const string NoGoalText = "Talk with the lead about what to build: it suggests a goal, then press Let the crew do it.";

    // What is missing before the crew can start, in the chat page's order. A goal is ready when the
    // project has one or the lead just suggested one.
    public static IReadOnlyList<string> Missing(JsonNode snapshot, string? projectId, bool goalReady = true)
    {
        var missing = new List<string>();
        var team = Team(snapshot);
        var row = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => S(item["id"]) == projectId);
        if (team.LeadId == null) missing.Add("Choose a lead in Team.");
        if (!team.Models.Any(model => model.Build && model.Available && model.Enabled)) missing.Add("Give at least one model the Build role in Team.");
        if (row != null && S(row["folder"]).Length == 0) missing.Add("Set the project folder in Settings.");
        if (row != null && !goalReady) missing.Add(NoGoalText);
        return missing;
    }

    public static bool HasGoal(JsonObject? row) => S(row?["goal"]).Trim() is { Length: > 0 } goal && goal != PlanwharfApi.NoGoal;
}
