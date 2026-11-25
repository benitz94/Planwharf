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
        Directory.Delete(directory, true);
    }











}
