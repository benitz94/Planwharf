using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record TeamModel(string Id, string Label, string Provider, int? QuotaLeft, bool Available, string? UnavailableReason,
    bool Lead, bool Build, bool Verify, bool Review, bool CanLead);
public sealed record TeamView(string? LeadId, IReadOnlyList<TeamModel> Models);

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
        var models = (snapshot["models"] as JsonArray ?? []).OfType<JsonObject>().Where(model => B(model["enabled"]) || model["enabled"] == null).Select(model =>
        {
            var roles = model["roles"];
            var label = S(model["label"]) is { Length: > 0 } text ? text : S(model["id"]);
            return new TeamModel(S(model["id"]), label, S(model["plan"]) is { Length: > 0 } plan ? plan : S(model["provider"]), QuotaLeft(snapshot, model),
                model["available"] is not JsonValue available || !available.TryGetValue<bool>(out var ok) || ok, S(model["unavailableReason"]) is { Length: > 0 } why ? why : null,
                S(model["id"]) == lead, B(roles?["worker"]), B(roles?["verifier"]), B(roles?["reviewer"]), model["roleEligibility"]?["orchestrator"] is null || B(model["roleEligibility"]?["orchestrator"]));
        }).ToList();
        return new TeamView(lead, models);
    }

    // The role a step needs, and the models of the team that have it (as the web step menu).
    public static IReadOnlyList<TeamModel> ModelsFor(TeamView team, string role) => team.Models.Where(model => model.Available && role switch
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
        if (!team.Models.Any(model => model.Build && model.Available)) missing.Add("Give at least one model the Build role in Team.");
        return missing;
    }
}
