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
        Prefs.Load();
        ApplyLook();
    }

    // The light or dark theme, and the text colour, chosen in Settings.
    public void ApplyLook()
    {
        Palette.Apply(Prefs.Theme != "light", Prefs.Ink, Prefs.Accent);
        RequestedThemeVariant = Palette.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        // Planwharf's main colour instead of the system blue for selections and switches.
        var accent = Palette.ColorOf(Palette.Accent);
        Resources["SystemAccentColor"] = Palette.Dark ? Palette.Mix(accent, Avalonia.Media.Colors.Black, 0.35) : accent;
    }

    // A new look: the window is drawn again with it, on the same project and panel, with the same server.
    public static void Restyle(MainWindow old)
    {
        if (Current is App app) app.ApplyLook();
        else Palette.Apply(Prefs.Theme != "light", Prefs.Ink, Prefs.Accent);
        var fresh = old.Rebuilt();
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = fresh;
        fresh.Show();
        old.Close();
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
