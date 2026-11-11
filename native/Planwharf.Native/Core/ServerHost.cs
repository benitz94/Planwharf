using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

// Starts the same local Planwharf server the web and desktop versions use, and stops it on exit.
// The engine is not changed: the window only talks to http://127.0.0.1:<port>/.
public sealed class ServerHost : IAsyncDisposable
{
    static readonly Regex AddressLine = new(@"^(http://127\.0\.0\.1:\d+)\s*$", RegexOptions.Multiline);

    Process? process;
    public string? Address { get; private set; }
    public string DataFolder { get; }
    public string LogFile => Path.Combine(DataFolder, "native.log");

    public ServerHost(string? dataFolder = null) => DataFolder = dataFolder ?? DefaultDataFolder();

    // The server prints its address on a line of its own once it listens.
    public static string? ReadAddress(string output) => AddressLine.Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    // Same data folder as the desktop app, so projects are shared: %APPDATA%\Planwharf\data, ~/.config/Planwharf/data,
    // ~/Library/Application Support/Planwharf/data. Use one Planwharf at a time.
    // GetFolderPath answers "" when the folder does not exist yet (a new user on Linux has no ~/.config): create it.
    public static string DefaultDataFolder()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "Planwharf", "data");
    }

    // The app files ship in "app" next to the executable; a development build finds them up the folder tree.
    public static string? FindAppFolder(string start)
    {
        var bundled = Path.Combine(start, "app");
        if (File.Exists(Path.Combine(bundled, "planwharf.cjs"))) return bundled;
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "planwharf.cjs")) && File.Exists(Path.Combine(dir.FullName, "server.cjs"))) return dir.FullName;
        return null;
    }

    // Node ships in "node" next to the executable; otherwise the one installed on the computer.
    public static string FindNode(string start)
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var candidate in new[] { Path.Combine(start, "node", name), Path.Combine(start, "node", "bin", name) })
            if (File.Exists(candidate)) return candidate;
        return name;
    }

    public async Task<string> StartAsync(string version, TimeSpan timeout, CancellationToken cancel = default)
    {
        var baseDir = AppContext.BaseDirectory;
        var app = FindAppFolder(baseDir) ?? throw new InvalidOperationException("Planwharf's app files were not found next to the program.");
        Directory.CreateDirectory(DataFolder);
        var plan = Path.Combine(DataFolder, "plan.json");
        // A first start gets the same starter notebook as the other versions.
        var starter = Path.Combine(app, "fixtures", "starter-notebook.json");
        if (!File.Exists(plan) && File.Exists(starter)) File.Copy(starter, plan);

        var info = new ProcessStartInfo(FindNode(baseDir)) { WorkingDirectory = app, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(app, "planwharf.cjs"));
        info.Environment["PLANWHARF_NO_WINDOW"] = "1";
        info.Environment["PLANWHARF_DESKTOP"] = "1";
        info.Environment["PLANWHARF_DESKTOP_VERSION"] = version;
        info.Environment["PLANWHARF_PLAN"] = plan;

        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new System.Text.StringBuilder();
        var log = new StreamWriter(LogFile, append: true) { AutoFlush = true };
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (output) { log.WriteLine(e.Data); output.AppendLine(e.Data); if (ReadAddress(output.ToString()) is { } address) found.TrySetResult(address); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) log.WriteLine(e.Data); };
        process.Exited += (_, _) => found.TrySetException(new InvalidOperationException($"Planwharf stopped while starting. See {LogFile}."));
        try { process.Start(); }
        catch (Exception error) { throw new InvalidOperationException($"Node.js could not start ({error.Message}). Install Node.js 18 or newer.", error); }
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var finished = await Task.WhenAny(found.Task, Task.Delay(timeout, cancel));
        if (finished != found.Task) throw new TimeoutException($"Planwharf did not start within {timeout.TotalSeconds:0} seconds. See {LogFile}.");
        return Address = await found.Task;
    }

    // Quit as the other versions do: the server saves unfinished work, then stops.
    public async ValueTask DisposeAsync()
    {
        if (process is not { HasExited: false }) return;
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; await http.PostAsync(Address + "/api/app/quit", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")); } catch { }
        if (!process.WaitForExit(10000)) try { process.Kill(entireProcessTree: true); } catch { }
    }
}
