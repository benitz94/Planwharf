using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// The Lead button on each model in Team: which models may lead this project.
public class LeadAllowedTests
{
    [Fact]
    public void A_model_switched_off_as_lead_is_not_in_the_lead_menu_but_the_current_lead_stays()
    {
        var state = SampleState.Working();
        foreach (var model in (System.Text.Json.Nodes.JsonArray)state["models"]!)
            if (model is System.Text.Json.Nodes.JsonObject m && (string?)m["id"] == "opus") m["leadAllowed"] = false;
        var team = TeamViews.Team(state);
        Assert.False(team.Models.Single(model => model.Id == "opus").LeadAllowed);
        Assert.True(team.Models.Single(model => model.Id == "astra").LeadAllowed, "on unless switched off");
        Assert.DoesNotContain(TeamViews.Leads(team), model => model.Id == "opus");
        var lead = team.Models.Single(model => model.Lead);
        Assert.Contains(TeamViews.Leads(team), model => model.Id == lead.Id);
    }

    [AvaloniaFact]
    public void Every_model_that_can_lead_shows_a_Lead_button_and_the_lead_cannot_switch_itself_off()
    {
        var state = SampleState.Working();
        var window = new MainWindow("v0.0.1");
        window.Show();
        try
        {
            window.Show(state);
            window.OpenSide("team");
            var buttons = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Where(button => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(button).OfType<TextBlock>().Any(text => text.Text == "Lead")).ToList();
            var team = TeamViews.Team(state);
            Assert.Equal(team.Models.Count(model => model.CanLead && !model.NotOffered && model.PlanStatus is not ("expired" or "not signed in")), buttons.Count);
            Assert.Contains(buttons, button => button.IsChecked == true && !button.IsEnabled);
        }
        finally { window.Close(); }
    }
}
