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
    public void The_lead_menu_lists_the_best_lead_first()
    {
        var state = State("a", true, true);
        state["leadOrder"] = new JsonArray("c", "a");
        var leads = TeamViews.Leads(TeamViews.Team(state));
        // B is not available, so it cannot lead; C is ranked above A.
        Assert.Equal(["c", "a"], leads.Select(model => model.Id));
    }

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
        Assert.Equal(["Choose a lead in Team.", "Give at least one model the Build role in Team.", "Set the project folder in Settings.", TeamViews.NoGoalText],
            TeamViews.Missing(State(null, false, false), "p1", goalReady: false));
        Assert.Empty(TeamViews.Missing(State("a", true, true), "p1"));
    }

    [Fact]
    public void Splits_the_team_from_the_bench_and_reads_the_ai_tools()
    {
        var team = TeamViews.Team(JsonNode.Parse("""
        { "models": [
            { "id": "a", "label": "A", "enabled": true, "roles": { "worker": true }, "roleEligibility": { "reviewer": false } },
            { "id": "b", "label": "B", "enabled": true, "roles": {}, "available": false, "unavailableAt": "2026-11-12T09:00:00Z" },
            { "id": "c", "label": "C", "enabled": false, "roles": { "worker": true } } ],
          "providers": [ { "id": "claude", "label": "Claude Code", "models": [ {}, {} ] }, { "id": "grok", "label": "Grok", "enabled": false, "models": [] } ] }
        """)!);
        Assert.False(team.Models[0].Bench); Assert.False(team.Models[0].CanReview); Assert.True(team.Models[0].CanBuild);
        Assert.True(team.Models[1].Bench); Assert.True(team.Models[1].RetrySoon);
        Assert.False(team.Models[2].Enabled);
        // A model that is off is never offered for a step.
        Assert.Equal(["a"], TeamViews.ModelsFor(team, "Build").Select(model => model.Id));
        Assert.Equal([new ProviderView("claude", "Claude Code", true, 2), new ProviderView("grok", "Grok", false, 0)], team.Providers);
    }

    [Fact]
    public void Asks_for_the_OK_only_for_tools_that_cannot_build_without_it()
    {
        var team = TeamViews.Team(JsonNode.Parse("""
        { "orchestratorId": "agy",
          "models": [
            { "id": "opus", "label": "Opus", "provider": "claude", "adapter": "claude", "enabled": true, "roles": { "worker": true } },
            { "id": "sonnet", "label": "Sonnet", "provider": "claude", "adapter": "claude", "enabled": true, "roles": { "verifier": true } },
            { "id": "grok", "label": "Grok", "provider": "grok", "adapter": "grok", "enabled": true, "alwaysApprove": true, "roles": { "worker": true } },
            { "id": "luna", "label": "Luna", "provider": "codex", "adapter": "codex", "enabled": true, "roles": { "worker": true } },
            { "id": "agy", "label": "Gemini", "provider": "antigravity", "adapter": "antigravity", "enabled": true, "roles": { "orchestrator": true } },
            { "id": "off", "label": "Off", "provider": "grok2", "adapter": "grok", "enabled": true, "available": false, "roles": { "worker": true } } ],
          "providers": [ { "id": "claude", "label": "Claude Code" }, { "id": "grok", "label": "Grok", "policy": { "alwaysApprove": true } }, { "id": "codex", "label": "Codex" }, { "id": "antigravity", "label": "Antigravity" } ] }
        """)!);
        // Claude Code once, for both its models; Grok already said yes; Codex works in its sandbox; unavailable models do not count.
        Assert.Equal(["claude"], TeamViews.ToolsNeedingOk(team).Select(tool => tool.Id));
        Assert.Equal(["antigravity"], TeamViews.ToolsNeedingOk(team, forLead: true).Select(tool => tool.Id));
        Assert.True(team.Providers.Single(provider => provider.Id == "grok").WorksUnasked);
        Assert.StartsWith("Claude Code will work in the project folder without asking each time: it edits files", TeamViews.OkQuestion(TeamViews.ToolsNeedingOk(team)));
        Assert.Contains("they edit", TeamViews.OkQuestion([new ProviderView("claude", "Claude Code", true, 1), new ProviderView("grok", "Grok", true, 1)]));
    }

    [Fact]
    public void A_tool_that_is_not_installed_is_never_offered()
    {
        var team = TeamViews.Team(JsonNode.Parse("""
        { "models": [ { "id": "luna", "provider": "codex", "adapter": "codex", "enabled": true, "available": false, "notInstalled": true,
                        "unavailableReason": "Codex is not installed on this computer.", "roles": { "worker": true } } ],
          "providers": [ { "id": "codex", "label": "ChatGPT", "installed": false, "installHint": "Codex is not installed on this computer. Install it with npm install -g @openai/codex, then run codex once to sign in.", "models": [ {} ] },
                         { "id": "claude", "label": "Claude Code", "models": [] } ] }
        """)!);
        Assert.False(team.Providers[0].Installed);
        Assert.Contains("npm install -g @openai/codex", team.Providers[0].InstallHint);
        Assert.True(team.Providers[1].Installed);
        Assert.Empty(TeamViews.ModelsFor(team, "Build"));
        Assert.Empty(TeamViews.ToolsNeedingOk(team));
        Assert.Contains("Give at least one model the Build role in Team.", TeamViews.Missing(JsonNode.Parse("""{ "orchestratorId": "x", "models": [ { "id": "luna", "available": false, "roles": { "worker": true } } ] }""")!, null));
    }

    [Fact]
    public void Reads_who_takes_over_as_lead()
    {
        var team = TeamViews.Team(JsonNode.Parse("""{ "orchestratorId": "a", "leadBackups": ["c", "b"], "models": [ { "id": "a", "label": "A" }, { "id": "b", "label": "B" }, { "id": "c", "label": "C" } ] }""")!);
        Assert.Equal(["C", "B"], team.LeadBackups);
    }
}
