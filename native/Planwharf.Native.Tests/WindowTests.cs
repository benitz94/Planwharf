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
        window.ShowRepo("weather", "MT-1", new Planwharf.Native.Core.RepoView(true, 6, 0, null));
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
}
