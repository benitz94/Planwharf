using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class ModelToolsTests
{
    [AvaloniaFact]
    public void Every_shown_team_card_has_a_tools_fold_and_refreshes_its_settings()
    {
        var state = SampleState.Working();
        state["projectToolSettings"] = JsonNode.Parse("""{ "weather": { "default": { "browser": true }, "models": {} } }""");
        var window = new MainWindow("v0.0.1");
        window.Show();
        try
        {
            window.Show(state);
            window.OpenSide("team");
            List<Expander> Folds() => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window)
                .OfType<Expander>().Where(fold => fold.Header as string == "Tools").ToList();
            var count = TeamViews.Team(state).Models.Count(model => !model.NotOffered);
            Assert.Equal(count, Folds().Count);
            Assert.NotEmpty(Folds());
            foreach (var fold in Folds())
            {
                var boxes = ((StackPanel)fold.Content!).Children.OfType<CheckBox>().ToList();
                Assert.True(boxes[0].IsChecked);
                Assert.True(boxes[2].IsChecked);
            }
            state["projectToolSettings"]!["weather"]!["default"]!["browser"] = false;
            window.Show(state);
            Assert.All(Folds(), fold => Assert.False(((StackPanel)fold.Content!).Children.OfType<CheckBox>().ElementAt(2).IsChecked));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Fold_reads_the_override_and_saves_changes_and_inheritance()
    {
        var snapshot = JsonNode.Parse("""{ "projectToolSettings": { "p": { "default": { "browser": true }, "models": { "a": { "memory": true } } } } }""");
        var saved = new List<IDictionary<string, bool>?>();
        var view = ModelToolsView.Read(snapshot, "p", "a");
        Assert.False(view.SameAsProject);
        var fold = ModelToolsEditor.Create(view, tools => { saved.Add(tools); return Task.CompletedTask; });
        Assert.Equal("Tools", fold.Header);
        var boxes = ((StackPanel)fold.Content!).Children.OfType<CheckBox>().ToList();
        Assert.Equal(5, boxes.Count);
        Assert.False(boxes[2].IsChecked);
        Assert.True(boxes[4].IsChecked);
        boxes[2].IsChecked = true;
        boxes[2].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert.True(saved.Single()!["browser"]);
        Assert.True(saved.Single()!["memory"]);
        boxes[0].IsChecked = true;
        boxes[0].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert.Null(saved.Last());
        Assert.All(boxes.Skip(1), box => Assert.False(box.IsEnabled));
        var inherited = ModelToolsView.Read(snapshot, "p", "b");
        Assert.True(inherited.SameAsProject);
        Assert.True(inherited.Tools["browser"]);
        Assert.False(inherited.Tools["memory"]);
        var legacy = ModelToolsView.Read(JsonNode.Parse("""{ "projectTools": { "p": { "hooks": true } } }"""), "p", "b");
        Assert.True(legacy.SameAsProject);
        Assert.True(legacy.Tools["hooks"]);
    }
}
