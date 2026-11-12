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
}
public sealed record ProviderView(string Id, string Label, bool Enabled, int Models);
public sealed record TeamView(string? LeadId, IReadOnlyList<TeamModel> Models)
{
    public IReadOnlyList<ProviderView> Providers { get; init; } = [];
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
                RetrySoon = model["unavailableAt"] != null
            };
        }).ToList();
        var providers = (snapshot["providers"] as JsonArray ?? []).OfType<JsonObject>().Select(provider => new ProviderView(S(provider["id"]),
            S(provider["label"]) is { Length: > 0 } label ? label : S(provider["id"]), provider["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on,
            (provider["models"] as JsonArray)?.Count ?? 0)).ToList();
        return new TeamView(lead, models) { Providers = providers };
    }

    // The role a step needs, and the models of the team that have it (as the web step menu).
    public static IReadOnlyList<TeamModel> ModelsFor(TeamView team, string role) => team.Models.Where(model => model.Available && model.Enabled && role switch
    {
        "Verify" => model.Verify || model.Build,
        "Review" => model.Review,
        _ => model.Build
    }).ToList();

    // What is missing before the crew can start, in the order the user should fix it.
    public static IReadOnlyList<string> Missing(JsonNode snapshot, string? projectId)
    {
        var missing = new List<string>();
        var team = Team(snapshot);
        var row = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => S(item["id"]) == projectId);
        if (row != null && S(row["folder"]).Length == 0) missing.Add("Set the project folder in Settings.");
        if (team.LeadId == null) missing.Add("Choose a lead in Team.");
        if (!team.Models.Any(model => model.Build && model.Available && model.Enabled)) missing.Add("Give at least one model the Build role in Team.");
        return missing;
    }
}
