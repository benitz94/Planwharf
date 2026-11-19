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

    [AvaloniaFact]
    public void Unchecking_same_as_project_copies_effective_set_and_enables_editing()
    {
        var snapshot = JsonNode.Parse("""{ "projectToolSettings": { "p": { "default": { "browser": true, "hooks": true }, "models": {} } } }""");
        var saved = new List<IDictionary<string, bool>?>();
        var view = ModelToolsView.Read(snapshot, "p", "worker");
        Assert.True(view.SameAsProject);
        Assert.True(view.Tools["browser"]);
        Assert.True(view.Tools["hooks"]);
        Assert.False(view.Tools["mcp"]);
        Assert.False(view.Tools["memory"]);

        var fold = ModelToolsEditor.Create(view, tools => { saved.Add(tools); return Task.CompletedTask; });
        var boxes = ((StackPanel)fold.Content!).Children.OfType<CheckBox>().ToList();
        Assert.True(boxes[0].IsChecked);
        Assert.All(boxes.Skip(1), box => Assert.False(box.IsEnabled));

        boxes[0].IsChecked = false;
        boxes[0].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

        Assert.All(boxes.Skip(1), box => Assert.True(box.IsEnabled));
        Assert.NotNull(saved.Single());
        Assert.True(saved.Single()!["browser"]);
        Assert.True(saved.Single()!["hooks"]);
        Assert.False(saved.Single()!["mcp"]);
        Assert.False(saved.Single()!["memory"]);

        boxes[2].IsChecked = false;
        boxes[2].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert.Equal(2, saved.Count);
        Assert.False(saved.Last()!["browser"]);
        Assert.True(saved.Last()!["hooks"]);
    }

    [Fact]
    public void ModelToolsView_Read_handles_null_empty_and_partial_snapshots()
    {
        var fromNull = ModelToolsView.Read(null, "p", "m");
        Assert.True(fromNull.SameAsProject);
        Assert.All(ModelToolsView.Names, name => Assert.False(fromNull.Tools[name]));

        var fromEmpty = ModelToolsView.Read(new JsonObject(), "p", "m");
        Assert.True(fromEmpty.SameAsProject);
        Assert.All(ModelToolsView.Names, name => Assert.False(fromEmpty.Tools[name]));

        var partial = ModelToolsView.Read(JsonNode.Parse("""{ "projectToolSettings": { "p": { "default": { "browser": true }, "models": { "m": { "memory": true } } } } }"""), "p", "m");
        Assert.False(partial.SameAsProject);
        Assert.True(partial.Tools["memory"]);
        Assert.False(partial.Tools["browser"]);
        Assert.False(partial.Tools["mcp"]);
        Assert.False(partial.Tools["hooks"]);
    }

    [Fact]
    public void ChatViews_Thread_attaches_model_id_to_ask_items()
    {
        var snapshot = JsonNode.Parse("""
        {
            "asks": [
                { "at": "2026-11-16T12:00:00Z", "question": "Check page", "answer": "I need browser", "toolRequest": "browser", "projectId": "p", "orchestratorId": "claude-lead" },
                { "at": "2026-11-16T12:05:00Z", "question": "Check code", "answer": "All fine", "projectId": "p" }
            ],
            "execution": { "timeline": [] }
        }
        """);
        var thread = ChatViews.Thread(snapshot!, "p", id => id);
        var askWithModel = Assert.IsType<AskItem>(thread[0]);
        Assert.Equal("claude-lead", askWithModel.ModelId);
        Assert.Equal("browser", askWithModel.ToolRequest);

        var askWithoutModel = Assert.IsType<AskItem>(thread[1]);
        Assert.Equal("", askWithoutModel.ModelId);
    }

    [Fact]
    public void Read_ignores_a_null_or_array_override_and_non_boolean_flags()
    {
        var snapshot = JsonNode.Parse("""
        {
          "projectTools": { "p": { "hooks": true } },
          "projectToolSettings": {
            "p": { "default": null, "models": { "m": null, "list": [], "empty": {}, "text": "browser" } },
            "q": { "default": { "browser": "true", "memory": true }, "models": { "m": { "mcp": 1, "hooks": true } } }
          }
        }
        """);
        var inherited = ModelToolsView.Read(snapshot, "p", "m");
        Assert.True(inherited.SameAsProject);
        Assert.True(inherited.Tools["hooks"]);
        Assert.False(inherited.Tools["browser"]);

        var listed = ModelToolsView.Read(snapshot, "p", "list");
        Assert.True(listed.SameAsProject);
        Assert.True(listed.Tools["hooks"]);

        var empty = ModelToolsView.Read(snapshot, "p", "empty");
        Assert.False(empty.SameAsProject);
        Assert.All(ModelToolsView.Names, name => Assert.False(empty.Tools[name]));

        var text = ModelToolsView.Read(snapshot, "p", "text");
        Assert.True(text.SameAsProject);

        var partial = ModelToolsView.Read(snapshot, "q", "m");
        Assert.False(partial.SameAsProject);
        Assert.True(partial.Tools["hooks"]);
        Assert.False(partial.Tools["mcp"]);
        Assert.False(partial.Tools["memory"]);
        Assert.False(partial.Tools["browser"]);

        var sibling = ModelToolsView.Read(snapshot, "q", "other");
        Assert.True(sibling.SameAsProject);
        Assert.True(sibling.Tools["memory"]);
        Assert.False(sibling.Tools["browser"]);
    }

    [Fact]
    public void Chat_thread_ignores_a_non_string_lead_id_on_a_tool_request()
    {
        var snapshot = JsonNode.Parse("""
        { "asks": [ { "at": "2026-11-16T12:00:00Z", "question": "Check", "answer": "Need it", "toolRequest": "browser", "projectId": "p", "orchestratorId": 4 } ],
          "execution": { "timeline": [] } }
        """);
        var ask = Assert.IsType<AskItem>(ChatViews.Thread(snapshot!, "p", id => id)[0]);
        Assert.Equal("", ask.ModelId);
        Assert.Equal("browser", ask.ToolRequest);
    }
}
