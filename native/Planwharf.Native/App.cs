using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Planwharf.Native.Core;

namespace Planwharf.Native;

public sealed class App : Application
{
    public static string Version => "v" + (typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0");

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        // Planwharf's green instead of the system blue for selections and accents.
        Resources["SystemAccentColor"] = Avalonia.Media.Color.Parse("#2f8f62");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = new ServerHost();
            var window = new MainWindow(Version);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await host.DisposeAsync();
            // Also when the system ends the app (logout, shutdown, a kill signal): the server must not stay behind.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
            _ = StartAsync(window, host);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static async Task StartAsync(MainWindow window, ServerHost host)
    {
        try { window.Connect(new PlanwharfApi(await host.StartAsync(Version, TimeSpan.FromSeconds(60)))); }
        catch (Exception error) { window.ShowError(error.Message); }
    }
}
