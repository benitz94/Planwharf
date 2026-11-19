using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Planwharf.Native.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

[Collection("Localization")]
public sealed class LocalizationTests : IDisposable
{
    readonly string previousLanguage = Strings.CurrentLanguage;
    readonly string previousFile = Prefs.File;
    readonly AlertOptions previousAlerts = Prefs.Alerts;
    readonly string previousTheme = Prefs.Theme, previousInk = Prefs.Ink, previousAccent = Prefs.Accent;
    readonly string directory = Path.Combine(AppContext.BaseDirectory, "localization-" + Guid.NewGuid().ToString("N"));

    public LocalizationTests()
    {
        Directory.CreateDirectory(directory);
        Prefs.File = Path.Combine(directory, "native-window.json");
    }

    public void Dispose()
    {
        Prefs.Save(theme: previousTheme, ink: previousInk, accent: previousAccent, alerts: previousAlerts);
        Prefs.File = previousFile;
        Strings.CurrentLanguage = previousLanguage;
        Directory.Delete(directory, true);
    }

    [Fact]
    public void Language_is_persisted_with_existing_window_preferences()
    {
        Strings.CurrentLanguage = "it";
        Prefs.Save();
        var saved = JsonNode.Parse(File.ReadAllText(Prefs.File));
        Assert.Equal("it", saved!["language"]!.GetValue<string>());
        Assert.NotNull(saved["theme"]);
        Assert.NotNull(saved["alertNotification"]);
        Strings.CurrentLanguage = "en";
        Prefs.Load();
        Assert.Equal("it", Strings.CurrentLanguage);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"language\":\"fr\"}")]
    [InlineData("{broken")]
    [InlineData("{\"language\":17}")]
    [InlineData("{\"language\":null}")]
    public void Older_or_invalid_preferences_default_to_English(string json)
    {
        File.WriteAllText(Prefs.File, json);
        Strings.CurrentLanguage = "it";
        Prefs.Load();
        Assert.Equal("en", Strings.CurrentLanguage);
    }

    [AvaloniaFact]
    public void Italian_settings_and_dialog_render_and_picker_saves_English()
    {
        var opened = new List<Window>();
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((sender, _) => opened.Add(sender));
        Strings.CurrentLanguage = "it";
        var window = new MainWindow("test") { Height = 1800 };
        window.Show();
        var dialog = new ProjectDialog();
        try
        {
            window.Show(SampleState.Working());
            window.OpenSide("settings");
            window.ShowSettings(new JsonObject(), new JsonArray(), new JsonObject());
            window.UpdateLayout();
            var controls = window.GetVisualDescendants().ToList();
            Assert.Contains(controls.OfType<TextBlock>(), text => text.Text == "Lingua");
            Assert.Contains(controls.OfType<Button>(), button => Equals(button.Content, "Salva") || button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Salva"));
            var picker = controls.OfType<ComboBox>().Single(box => box.Name == "LanguagePicker");
            Assert.Equal(1, picker.SelectedIndex);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "native-italian.png"));
            Assert.True(new FileInfo(Path.Combine(AppContext.BaseDirectory, "native-italian.png")).Length > 10000);
            dialog.Show();
            Assert.Equal("Nuovo progetto", dialog.Title);
            using var dialogFrame = dialog.CaptureRenderedFrame();
            Assert.NotNull(dialogFrame);
            picker.SelectedIndex = 0;
            Assert.Equal("en", Strings.CurrentLanguage);
            Assert.Equal("en", JsonNode.Parse(File.ReadAllText(Prefs.File))!["language"]!.GetValue<string>());
        }
        finally { foreach (var openedWindow in opened) openedWindow.Close(); }
    }

    [AvaloniaFact]
    public void Italian_alert_switches_save_the_correct_options()
    {
        Strings.CurrentLanguage = "it";
        Prefs.Save(alerts: new AlertOptions());
        var window = new MainWindow("test");
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.OpenSide("settings");
            window.UpdateLayout();
            var toggles = window.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            toggles.Single(toggle => Equals(toggle.OnContent, "Notifica")).IsChecked = false;
            Assert.Equal(new AlertOptions(false, true, true), Prefs.Alerts);
            toggles.Single(toggle => Equals(toggle.OnContent, "Lampeggio")).IsChecked = false;
            Assert.Equal(new AlertOptions(false, false, true), Prefs.Alerts);
            toggles.Single(toggle => Equals(toggle.OnContent, "Suono")).IsChecked = false;
            Assert.Equal(new AlertOptions(false, false, false), Prefs.Alerts);
            Prefs.Load();
            Assert.Equal(new AlertOptions(false, false, false), Prefs.Alerts);
            Assert.Equal("it", Strings.CurrentLanguage);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Cached_labels_refresh_and_Italian_commands_keep_their_meaning()
    {
        var row = new JsonObject { ["id"] = "sample" };
        var snapshot = JsonNode.Parse("""{"autonomy":{"sample":{"enabled":true,"stopReason":"GOAL_REACHED"}}}""")!;
        Strings.CurrentLanguage = "en";
        Assert.Equal("Goal reached", ChatViews.Status(snapshot, row).Text);
        Strings.CurrentLanguage = "it";
        Assert.Equal("Obiettivo raggiunto", ChatViews.Status(snapshot, row).Text);
        Assert.Contains("non ha confermato", ProjectViews.StopSentence("LEAD_UNCONFIRMED"));
        Assert.Equal(CommandKind.StopNow, ChatViews.Command("ferma tutto").Kind);
        Assert.Equal(CommandKind.StopIn, ChatViews.Command("fermati tra 10 minuti").Kind);
        Assert.Equal("settings", ChatViews.Command("apri impostazioni").Panel);
        Assert.Equal(CommandKind.Bench, ChatViews.Command("metti Opus in panchina").Kind);
        Assert.False(TeamViews.HasGoal(new JsonObject { ["goal"] = PlanwharfApi.NoGoal }));
    }

    [Fact]
    public void Switching_language_preserves_work_actions_model_roles_and_project_content()
    {
        foreach (var snapshot in new[] { SampleState.Working(), SampleState.Finished() })
        {
            Strings.CurrentLanguage = "en";
            var english = ProjectViews.Project(snapshot, "weather")!;
            Strings.CurrentLanguage = "it";
            var italian = ProjectViews.Project(snapshot, "weather")!;
            Assert.Equal(english.Action, italian.Action);
            Assert.Equal(english.NextGoal, italian.NextGoal);
            Assert.Equal(english.Work!.Done, italian.Work!.Done);
            Assert.Equal(english.Work.Total, italian.Work.Total);
            Assert.Equal(english.Work.Steps.Select(step => (step.Id, step.Role, step.Status, step.ModelId)),
                italian.Work.Steps.Select(step => (step.Id, step.Role, step.Status, step.ModelId)));
            Assert.Equal(english.Messages.Select(message => message.Text), italian.Messages.Select(message => message.Text));
            Assert.Equal(english.Work.Results, italian.Work.Results);
        }
    }

    [Theory]
    [InlineData("{\"language\":\" IT \"}", "it")]
    [InlineData("{\"language\":\"it-IT\",\"theme\":\"light\"}", "it")]
    [InlineData("{\"language\":\"en-US\"}", "en")]
    [InlineData("", "en")]
    [InlineData("   ", "en")]
    public void Messy_language_tags_normalize_and_unreadable_files_return_to_English(string json, string expected)
    {
        File.WriteAllText(Prefs.File, json);
        Strings.CurrentLanguage = "it";
        Prefs.Save(theme: "light");
        File.WriteAllText(Prefs.File, json);
        Prefs.Load();
        Assert.Equal(expected, Strings.CurrentLanguage);
        if (expected == "it" && json.Contains("light", StringComparison.Ordinal)) Assert.Equal("light", Prefs.Theme);
    }

    [Fact]
    public void Missing_locked_and_nested_preference_files_recover()
    {
        File.Delete(Prefs.File);
        Strings.CurrentLanguage = "it";
        Prefs.Load();
        Assert.Equal("en", Strings.CurrentLanguage);

        var nested = Path.Combine(directory, "nested", "native-window.json");
        Prefs.File = nested;
        Strings.CurrentLanguage = "it";
        Prefs.Save(theme: "light", alerts: new AlertOptions(false, true, false));
        Assert.True(File.Exists(nested));
        var other = Path.Combine(directory, "other.json");
        Prefs.File = other;
        Strings.CurrentLanguage = "en";
        Prefs.Save(theme: "dark");
        Prefs.File = nested;
        Prefs.Load();
        Assert.Equal("it", Strings.CurrentLanguage);
        Assert.Equal("light", Prefs.Theme);
        Assert.Equal(new AlertOptions(false, true, false), Prefs.Alerts);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(nested)!, "*.tmp"));

        File.SetAttributes(nested, FileAttributes.ReadOnly);
        try
        {
            Strings.CurrentLanguage = "en";
            Prefs.Save();
            Assert.Equal("en", Strings.CurrentLanguage);
            Assert.Equal("it", JsonNode.Parse(File.ReadAllText(nested))!["language"]!.GetValue<string>());
        }
        finally { File.SetAttributes(nested, FileAttributes.Normal); }

        var blocked = Path.Combine(directory, "blocked");
        Directory.CreateDirectory(blocked);
        Prefs.File = blocked;
        Strings.CurrentLanguage = "it";
        Prefs.Load();
        Assert.Equal("en", Strings.CurrentLanguage);
        Strings.CurrentLanguage = "it";
        Prefs.Save();
        Assert.Equal("it", Strings.CurrentLanguage);
        Assert.False(File.Exists(blocked));
    }

    [Fact]
    public void A_broken_reread_restores_every_preference()
    {
        Strings.CurrentLanguage = "it";
        Prefs.Save(theme: "light");
        File.WriteAllText(Prefs.File, "{broken");
        Prefs.Load();
        Assert.Equal("en", Strings.CurrentLanguage);
        Assert.Equal("dark", Prefs.Theme);
        Assert.Equal("white", Prefs.Ink);
        Assert.Equal("green", Prefs.Accent);
    }

    [Fact]
    public async Task Saving_and_loading_together_keeps_a_readable_preference_file()
    {
        Strings.CurrentLanguage = "it";
        Prefs.Save(theme: "light");
        var failures = 0;
        var gate = new Barrier(8);
        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var n = 0; n < 30; n++)
            {
                try
                {
                    if (i % 2 == 0)
                    {
                        Strings.CurrentLanguage = n % 2 == 0 ? "it" : "en";
                        Prefs.Save(theme: n % 2 == 0 ? "light" : "dark");
                    }
                    else Prefs.Load();
                }
                catch { Interlocked.Increment(ref failures); }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        Assert.Equal(0, failures);
        var saved = JsonNode.Parse(File.ReadAllText(Prefs.File))!;
        Assert.Contains(saved["language"]!.GetValue<string>(), new[] { "en", "it" });
        Assert.Contains(saved["theme"]!.GetValue<string>(), new[] { "light", "dark" });
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        Prefs.Load();
        Assert.Contains(Strings.CurrentLanguage, new[] { "en", "it" });
    }

    [Fact]
    public void Italian_usage_takeover_and_durations_use_the_catalog_and_culture()
    {
        Strings.CurrentLanguage = "it";
        Assert.Equal("1,5 h", WorkDetails.Duration(5400000));
        var usage = PanelViews.Usage(JsonNode.Parse("""
        { "plans": [], "aiUsage": { "orchestrator": [ { "model": "Grok 4.7", "calls": 4, "durationMs": 95000, "outputTokens": 5200 } ],
          "workers": [ { "model": "Opus 5.5", "calls": 2, "durationMs": 540000 } ] } }
        """)!);
        Assert.Equal([("Grok 4.7", "4 · 2 min · 5.200 in uscita")], usage.Lead);
        Assert.Equal([("Opus 5.5", "2 · 9 min · non comunicato")], usage.Crew);
        var row = JsonNode.Parse("""{ "subTasks": [ { "id": "s2", "title": "Verify the work independently against the job.", "status": "COMPLETE", "assignedModelId": "astra", "takeover": { "fromModelId": "grok" } } ], "results": [] }""")!.AsObject();
        var lines = WorkDetails.Summary(row, null, null, id => id == "astra" ? "GPT-6 Astra" : id == "grok" ? "Grok 4.7" : "");
        Assert.Contains("Verifica: GPT-6 Astra, subentrando a Grok 4.7.", lines);
    }
}
