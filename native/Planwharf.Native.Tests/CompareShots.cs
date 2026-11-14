using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// Pictures of the window against a running Planwharf, to compare it with the web page drawn from the
// same server. Runs only when PLANWHARF_COMPARE_URL is set (for example http://127.0.0.1:47811);
// PLANWHARF_COMPARE_PROJECT picks the project and PLANWHARF_COMPARE_OUT the folder for the pictures.
public class CompareShots
{
    [AvaloniaFact]
    public async Task Draws_every_panel_from_a_running_server()
    {
        var url = Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_URL");
        if (string.IsNullOrEmpty(url)) return;
        var output = Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_OUT") ?? AppContext.BaseDirectory;
        var width = int.TryParse(Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_WIDTH"), out var w) ? w : 1440;
        var height = int.TryParse(Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_HEIGHT"), out var h) ? h : 900;
        // PLANWHARF_COMPARE_THEME=light draws the light theme (and the dark one again afterwards for the other tests).
        var light = Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_THEME") == "light";
        Palette.Apply(!light, null, Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_ACCENT"));
        Avalonia.Application.Current!.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        try { await Draw(url, output, width, height); }
        finally { Palette.Apply(true, null); Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; }
    }

    static async Task Draw(string url, string output, int width, int height)
    {
        var window = new MainWindow(Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_VERSION") ?? "v0.2.42") { Width = width, Height = height };
        window.Show();
        window.Connect(new PlanwharfApi(url));
        await Settle();
        if (Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_PROJECT") is { Length: > 0 } project)
        {
            var list = window.GetVisualDescendants().OfType<ListBox>().First();
            list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, project));
            await Settle();
        }
        Save(window, Path.Combine(output, "native-chat.png"));
        // PLANWHARF_COMPARE_PANELS picks the side panels, for example "team,usage"; all of them by default.
        var panels = Environment.GetEnvironmentVariable("PLANWHARF_COMPARE_PANELS") is { Length: > 0 } picked ? picked.Split(',') : ["team", "usage", "progress", "files", "settings"];
        foreach (var panel in panels)
        {
            window.OpenSide(panel);
            await Settle();
            Save(window, Path.Combine(output, $"native-{panel}.png"));
        }
    }

    static async Task Settle()
    {
        for (var i = 0; i < 25; i++) { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
    }

    static void Save(Window window, string file)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(file);
    }
}
