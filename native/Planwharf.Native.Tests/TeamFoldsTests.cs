using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Planwharf.Native.Tests;

// The Team panel is drawn again after every change (a role picked, a new quota reading). What the owner
// opened or closed must stay so, or picking models from the bench is a fight with the panel.
public class TeamFoldsTests
{
    static Button? BenchHead(Window window) => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Button>()
        .FirstOrDefault(button => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(button).OfType<TextBlock>().Any(text => text.Text is { } t && t.Contains("on the bench", StringComparison.Ordinal) && !t.StartsWith("Bench", StringComparison.Ordinal)));

    static bool Open(Button head) => ((Panel)head.Parent!).Children[1].IsVisible;

    [AvaloniaFact]
    public void The_bench_and_the_tools_folds_stay_as_the_owner_left_them_when_the_panel_is_drawn_again()
    {
        var state = SampleState.Working();
        ((System.Text.Json.Nodes.JsonArray)state["models"]!).Add(System.Text.Json.Nodes.JsonNode.Parse("""{ "id": "haiku-bench", "label": "Haiku 5.5", "plan": "max", "enabled": true, "roles": {} }"""));
        var window = new MainWindow("v0.0.1");
        window.Show();
        try
        {
            window.Show(state);
            window.OpenSide("team");
            var head = BenchHead(window);
            Assert.NotNull(head);
            var before = Open(head!);
            head!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(!before, Open(head));

            var tools = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Expander>().First(fold => fold.Header as string == "Tools");
            tools.IsExpanded = true;
            var benchBefore = head;

            // A change that draws the panel again: another reading of the quota.
            state["updatedAt"] = "2026-11-22T10:00:00Z";
            foreach (var model in (System.Text.Json.Nodes.JsonArray)state["models"]!) if (model is System.Text.Json.Nodes.JsonObject m) m["quotaRefreshedAt"] = "2026-11-22T10:00:00Z";
            window.Show(state);
            window.OpenSide("team");

            Assert.NotSame(benchBefore, BenchHead(window));
            Assert.NotSame(tools, Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Expander>().First(fold => fold.Header as string == "Tools"));
            Assert.Equal(!before, Open(BenchHead(window)!));
            Assert.True(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Expander>().First(fold => fold.Header as string == "Tools").IsExpanded);
        }
        finally { window.Close(); }
    }
}
