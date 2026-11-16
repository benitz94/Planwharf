using System.Globalization;
using Avalonia;
using Planwharf.Native.Core;

namespace Planwharf.Native;

static class Program
{
    // An update finishes before any window opens: the new copy replaces the installed folder, then starts it.
    [STAThread]
    public static void Main(string[] args)
    {
        if (FinishUpdate(args) is int code)
        {
            Environment.ExitCode = code;
            return;
        }
        try { NativeUpdater.CleanStages(NativeUpdater.InstallFolder); }
        catch (IOException) { /* a stage still in use is removed on the next start */ }
        catch (UnauthorizedAccessException) { }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Null when this is a normal start. Otherwise the process only finishes an update and must not open a window.
    public static int? FinishUpdate(string[] args)
    {
        if (args.Length == 0 || args[0] != NativeUpdater.FinishFlag) return null;
        if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            return 2;
        return NativeUpdater.Finish(args[1], pid);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
