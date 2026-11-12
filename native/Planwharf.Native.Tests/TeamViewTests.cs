using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class TeamViewTests
{
    static JsonNode State(string? lead, bool folder, bool build) => JsonNode.Parse($$"""
    { "orchestratorId": {{(lead == null ? "null" : $"\"{lead}\"")}},
      "projects": [{ "id": "p1", "name": "Sample", "folder": "{{(folder ? "/tmp/sample" : "")}}" }],
      "plans": [{ "id": "pro", "readable": true, "leftPct": 62.4 }],
      "models": [
        { "id": "a", "label": "Model A", "plan": "pro", "enabled": true, "roles": { "worker": {{(build ? "true" : "false")}}, "verifier": false } },
        { "id": "b", "label": "Model B", "plan": "max", "enabled": true, "available": false, "unavailableReason": "CLI not found", "roles": { "worker": true, "reviewer": true } },
        { "id": "c", "label": "Model C", "plan": "max", "enabled": true, "roles": { "verifier": true } }
      ] }
    """)!;

    [Fact]
    public void Reads_lead_roles_quota_and_unavailable_models()
    {
        var team = TeamViews.Team(State("a", true, true));
        Assert.Equal("a", team.LeadId);
        var a = team.Models.Single(model => model.Id == "a");
        Assert.True(a.Lead); Assert.True(a.Build); Assert.Equal(62, a.QuotaLeft);
        var b = team.Models.Single(model => model.Id == "b");
        Assert.False(b.Available); Assert.Equal("CLI not found", b.UnavailableReason); Assert.Null(b.QuotaLeft);
    }

    [Fact]
    public void Step_menus_offer_only_available_models_with_the_role()
    {
        var team = TeamViews.Team(State("a", true, true));
        Assert.Equal(["a"], TeamViews.ModelsFor(team, "Build").Select(model => model.Id));
        Assert.Equal(["a", "c"], TeamViews.ModelsFor(team, "Verify").Select(model => model.Id));
        Assert.Empty(TeamViews.ModelsFor(team, "Review"));
    }

    [Fact]
    public void Says_what_is_missing_before_the_crew_can_start()
    {
        Assert.Equal(["Set the project folder in Settings.", "Choose a lead in Team.", "Give at least one model the Build role in Team."],
            TeamViews.Missing(State(null, false, false), "p1"));
        Assert.Empty(TeamViews.Missing(State("a", true, true), "p1"));
    }
}
