using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Planwharf.Native.Tests.TestApp))]

namespace Planwharf.Native.Tests;

public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark; Resources["SystemAccentColor"] = Avalonia.Media.Color.Parse("#2f8f62"); }
}

public class WindowTests
{
    // Draws the window with sample data and saves the picture next to the test results.
    [AvaloniaFact]
    public void Draws_the_chat_and_the_finished_work()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        window.Show(SampleState.Finished());
        window.ShowRepo("weather", "MT-1", System.Text.Json.Nodes.JsonNode.Parse("""{ "checkedAt": "2026-11-11T15:56:00Z", "local": { "exists": true, "git": true, "branch": "main", "uncommitted": 6, "changes": [" M src/forecast.js", " M src/chart.js", "?? src/forecast.test.js", " M index.html", "?? docs/forecast.md", "D  src/old-chart.js"] } }""")!);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("PLANWHARF_NATIVE_SHOT") ?? Path.Combine(AppContext.BaseDirectory, "native-window.png");
        frame!.Save(output);
        Assert.True(new FileInfo(output).Length > 10_000);
    }

    // The crew at work, a step's model menu, and the Team panel on the right.
    [AvaloniaFact]
    public void Draws_the_team_panel_and_the_step_menus()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        window.Show(SampleState.Working());
        window.OpenSide("team");
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var shot = Environment.GetEnvironmentVariable("PLANWHARF_NATIVE_SHOT");
        var output = shot != null ? Path.ChangeExtension(shot, null) + "-team.png" : Path.Combine(AppContext.BaseDirectory, "native-team.png");
        frame!.Save(output);
        Assert.True(new FileInfo(output).Length > 10_000);
    }

    [AvaloniaFact]
    public void Draws_the_settings_panel_and_the_new_project_dialog()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        window.Show(SampleState.Working());
        window.OpenSide("settings");
        var dialog = new ProjectDialog();
        dialog.Show();
        var shot = Environment.GetEnvironmentVariable("PLANWHARF_NATIVE_SHOT");
        foreach (var (name, frame) in new[] { ("settings", window.CaptureRenderedFrame()), ("new-project", dialog.CaptureRenderedFrame()) })
        {
            Assert.NotNull(frame);
            var output = shot != null ? Path.ChangeExtension(shot, null) + $"-{name}.png" : Path.Combine(AppContext.BaseDirectory, $"native-{name}.png");
            frame!.Save(output);
            Assert.True(new FileInfo(output).Length > 3_000);
        }
    }

    // Progress, Usage and Changed files, each drawn on the finished sample.
    [AvaloniaFact]
    public void Draws_the_progress_usage_and_files_panels()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        var state = SampleState.Finished();
        state["plans"] = System.Text.Json.Nodes.JsonNode.Parse("""[ { "id": "max", "label": "Claude Max", "readable": true, "leftPct": 71, "windows": [ { "name": "5-hour", "leftPct": 71 }, { "name": "weekly", "leftPct": 18 } ] }, { "id": "grok", "label": "SuperGrok", "readable": false, "reason": "Grok does not report quota." } ]""");
        state["aiUsage"]!["workers"] = System.Text.Json.Nodes.JsonNode.Parse("""[ { "model": "Opus 5.5", "calls": 2, "durationMs": 540000, "outputTokens": 21400 } ]""");
        state["execution"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "timeline": [ { "projectId": "weather", "createdAt": "2026-11-11T15:41:00Z", "summary": "Work approved: Add a 7-day forecast page." }, { "projectId": "weather", "createdAt": "2026-11-11T15:55:00Z", "summary": "The lead confirmed the goal." } ] }""");
        state["projects"]![0]!["folder"] = "/home/sample/weather";
        window.Show(state);
        window.ShowRepo("weather", "MT-1", System.Text.Json.Nodes.JsonNode.Parse("""{ "local": { "exists": true, "git": true, "branch": "main", "changes": [" M src/forecast.js", "?? src/forecast.test.js"] } }""")!);
        var shot = Environment.GetEnvironmentVariable("PLANWHARF_NATIVE_SHOT");
        foreach (var mode in new[] { "progress", "usage", "files" })
        {
            window.OpenSide(mode);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var output = shot != null ? Path.ChangeExtension(shot, null) + $"-{mode}.png" : Path.Combine(AppContext.BaseDirectory, $"native-{mode}.png");
            frame!.Save(output);
            Assert.True(new FileInfo(output).Length > 10_000);
        }
    }
}
