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

    // Let the crew do it starts only a plan ready and not yet worked on: never the goal already reached.
    [Fact]
    public void A_goal_already_worked_on_is_not_a_plan_ready_to_start()
    {
        var row = new JsonObject { ["goal"] = "Port the five modules.", ["goalStarted"] = false };
        Assert.True(TeamViews.GoalReady(row, null));
        row["goalStarted"] = true;
        Assert.False(TeamViews.GoalReady(row, null));
        Assert.True(TeamViews.GoalReady(row, "Port the next module."), "a new goal suggested by the lead");
        Assert.False(TeamViews.GoalReady(row, "Port the five modules.", DateTimeOffset.Parse("2026-11-11T15:40:00Z"), "2026-11-11T15:41:00Z"), "a suggestion from before the work started");
        Assert.True(TeamViews.GoalReady(row, "Port the next module.", DateTimeOffset.Parse("2026-11-13T12:00:00Z"), "2026-11-11T15:41:00Z"));
        var state = State("a", true, true);
        state["projects"]![0]!["goal"] = "Port the five modules.";
        state["projects"]![0]!["goalStarted"] = true;
        Assert.Equal(TeamViews.NextGoalText, TeamViews.Missing(state, "p1", goalReady: false).Single());
    }

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
        // An old Review step offers Build models. The reviewer flag is not a role.
        Assert.Equal(["a"], TeamViews.ModelsFor(team, "Review").Select(model => model.Id));
    }

    [Fact]
    public void Expired_and_signed_out_models_are_excluded_from_leads_and_step_menus()
    {
        var state = JsonNode.Parse("""
        {
          "orchestratorId": "a",
          "leadOrder": ["a", "b", "c"],
          "models": [
            { "id": "a", "label": "Model A", "plan": "pro", "planStatus": "expired", "enabled": true, "available": true, "roles": { "worker": true, "verifier": true }, "roleEligibility": { "orchestrator": true, "worker": true, "verifier": true } },
            { "id": "b", "label": "Model B", "plan": "plus", "planStatus": "not signed in", "enabled": true, "available": true, "roles": { "worker": true, "verifier": true }, "roleEligibility": { "orchestrator": true, "worker": true, "verifier": true } },
            { "id": "c", "label": "Model C", "plan": "max", "planStatus": "active", "enabled": true, "available": true, "roles": { "worker": true, "verifier": true }, "roleEligibility": { "orchestrator": true, "worker": true, "verifier": true } }
          ]
        }
        """)!;
        var team = TeamViews.Team(state);
        Assert.Equal("expired", team.Models.Single(m => m.Id == "a").PlanStatus);
        Assert.Equal("not signed in", team.Models.Single(m => m.Id == "b").PlanStatus);
        Assert.Equal("active", team.Models.Single(m => m.Id == "c").PlanStatus);

        var leads = TeamViews.Leads(team);
        Assert.Equal(["c"], leads.Select(m => m.Id));

        var builders = TeamViews.ModelsFor(team, "Build");
        Assert.Equal(["c"], builders.Select(m => m.Id));

        var verifiers = TeamViews.ModelsFor(team, "Verify");
        Assert.Equal(["c"], verifiers.Select(m => m.Id));
    }

    [Fact]
    public void A_model_with_only_the_reviewer_role_is_on_the_bench_with_no_review_chip()
    {
        var team = TeamViews.Team(JsonNode.Parse("""
        { "models": [
            { "id": "r", "label": "Only review", "provider": "claude", "adapter": "claude", "providerId": "claude", "enabled": true, "available": true,
              "roles": { "orchestrator": false, "worker": false, "verifier": false, "reviewer": true, "supervisor": false },
              "roleEligibility": { "reviewer": true, "worker": true, "verifier": true, "orchestrator": true } },
            { "id": "b", "label": "Builder", "provider": "codex", "adapter": "codex", "enabled": true, "available": true,
              "roles": { "worker": true } } ],
          "providers": [ { "id": "claude", "label": "Claude Code" } ] }
        """)!);
        var reviewer = team.Models.Single(model => model.Id == "r");
        Assert.True(reviewer.Bench);
        Assert.False(reviewer.Lead);
        Assert.False(reviewer.Build);
        Assert.False(reviewer.Verify);
        Assert.False(reviewer.Review);
        Assert.False(reviewer.CanReview);
        Assert.DoesNotContain("r", TeamViews.ModelsFor(team, "Build").Select(model => model.Id));
        Assert.DoesNotContain("r", TeamViews.ModelsFor(team, "Review").Select(model => model.Id));
        Assert.Equal(["b"], TeamViews.ModelsFor(team, "Review").Select(model => model.Id));
        Assert.Empty(TeamViews.ToolsNeedingOk(team));
    }

    [Fact]
    public void Supervisor_and_reviewer_alone_remain_on_the_bench_and_verifier_is_not_offered_for_review()
    {
        var team = TeamViews.Team(JsonNode.Parse("""
        { "models": [
            { "id": "s", "label": "Supervisor only", "provider": "codex", "adapter": "codex", "enabled": true, "available": true,
              "roles": { "supervisor": true, "reviewer": true } },
            { "id": "v", "label": "Verifier", "provider": "claude", "adapter": "claude", "enabled": true, "available": true,
              "roles": { "verifier": true, "reviewer": true } } ] }
        """)!);
        var supervisor = team.Models.Single(model => model.Id == "s");
        Assert.True(supervisor.Bench);
        Assert.False(supervisor.CanReview);

        var verifier = team.Models.Single(model => model.Id == "v");
        Assert.False(verifier.Bench);
        Assert.False(verifier.CanReview);
        Assert.True(verifier.Verify);
        Assert.False(verifier.Build);
        Assert.Contains("v", TeamViews.ModelsFor(team, "Verify").Select(m => m.Id));
        Assert.DoesNotContain("v", TeamViews.ModelsFor(team, "Review").Select(m => m.Id));
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

public class CompanyLimitTests
{
    [Fact]
    public void Reads_where_a_model_limit_comes_from()
    {
        var team = TeamViews.Team(System.Text.Json.Nodes.JsonNode.Parse("""
        { "models": [
          { "id": "opus", "label": "Opus", "plan": "claude", "enabled": true, "quotaLimit": { "fiveHourPercent": 80, "weeklyPercent": 80, "source": "company", "pool": "claude" } },
          { "id": "old", "label": "Old", "plan": "chatgpt", "enabled": true, "quotaLimit": { "fiveHourPercent": 40, "weeklyPercent": 40, "custom": true } } ] }
        """)!);
        Assert.Equal(("company", "claude", 80), (team.Models[0].LimitSource, team.Models[0].Pool, team.Models[0].LimitFiveHour));
        Assert.Equal(("model", "chatgpt"), (team.Models[1].LimitSource, team.Models[1].Pool));
    }
}
