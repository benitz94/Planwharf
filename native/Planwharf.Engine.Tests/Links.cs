using System.Diagnostics;

namespace Planwharf.Engine.Tests;

// Links for the tests that check a walk does not follow them, on every system.
// Windows: a junction for a folder (no special rights needed), a symbolic link for a file when the
// account may create one. Elsewhere: symbolic links. Arguments are passed one by one, never joined.
static class Links
{
    public static void Folder(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("mklink /J failed: " + error.Result + output.Result);
    }

    // False only on Windows when the account may not create symbolic links (no Developer Mode).
    public static bool TryFile(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException) { return false; }
    }
}
