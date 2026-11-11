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
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("PLANWHARF_NATIVE_SHOT") ?? Path.Combine(AppContext.BaseDirectory, "native-window.png");
        frame!.Save(output);
        Assert.True(new FileInfo(output).Length > 10_000);
    }
}
