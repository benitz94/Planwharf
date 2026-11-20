// Port of engine/project-checks.cjs.
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Planwharf runs the project's own tests before the lead judges a finished MegaTask, so the
// verdict rests on a fresh run instead of a worker's claim, and the lead spends no time or
// tokens running them. Only a recognised test entry point is run; nothing is guessed.
public static class ProjectChecks
{
    public const int TimeoutMs = 10 * 60 * 1000;
    const int MaxDepth = 4, ReaderTail = 20_000, ResultTail = 3_000;

    // Node's readdir order is unspecified. Ordinal names keep the 200-file stop the same on every disk.
    static readonly Regex TestFile = new(
        @"(^|[\\/])(test|tests|__tests__)[\\/].+\.(c?js|mjs)$|\.(test|spec)\.(c?js|mjs)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex NoTestSpecified = new("no test specified", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record CheckSpec(string Command, string[] Args, string Label);
    public sealed record CheckResult(string Label, int? ExitCode, bool TimedOut, double Seconds, string Output);

    // Returns full paths. Walks no deeper than 4 and does not enter a directory once more than 200 files are already found.
    public static string[] TestFiles(string folder)
    {
        var found = new List<string>();
        Walk(folder, 0);
        return found.ToArray();

        void Walk(string dir, int depth)
        {
            if (depth > MaxDepth || found.Count > 200) return;
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return; }
            Array.Sort(entries, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                var full = Path.Combine(dir, entry.Name);
                var directory = (entry.Attributes & FileAttributes.Directory) != 0 && (entry.Attributes & FileAttributes.ReparsePoint) == 0;
                if (directory) Walk(full, depth + 1);
                else if (TestFile.IsMatch(Path.GetRelativePath(folder, full))) found.Add(full);
            }
        }
    }

    // Returns the command, argument list, and label, or null when the project has no recognised tests.
    public static CheckSpec? TestCommand(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        try
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "package.json")));
            // Node keeps a leading BOM and JSON.parse rejects it. System.Text.Json would skip it.
            if (text.Length > 0 && text[0] == '\uFEFF') throw new System.Text.Json.JsonException("BOM");
            if (JsonNode.Parse(text) is JsonObject package
                && package["scripts"] is JsonObject scripts
                && scripts["test"] is JsonValue test
                && test.TryGetValue<string>(out var script)
                && script.Trim().Length > 0
                && !NoTestSpecified.IsMatch(script))
            {
                return OperatingSystem.IsWindows()
                    ? new CheckSpec(WindowsShell(), ["/d", "/s", "/c", "npm test"], "npm test")
                    : new CheckSpec("npm", ["test"], "npm test");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            // No usable package script. Fall through to test files.
        }
        return TestFiles(folder).Length > 0
            ? new CheckSpec(Environment.ProcessPath ?? "", ["--test"], "node --test")
            : null;
    }

    // An empty ComSpec is missing. A shell path with spaces is kept and is not a joined command line.
    static string WindowsShell()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrEmpty(comSpec) ? "cmd.exe" : comSpec;
    }

    // The reader keeps 20000 characters and the result keeps 3000. A cut value starts with "...".
    public static string Tail(string? text, int chars = ResultTail)
    {
        var value = string.IsNullOrEmpty(text) ? "" : text;
        return value.Length > chars ? "..." + value[^(chars)..] : value;
    }

    public sealed class CheckOptions
    {
        public IReadOnlyDictionary<string, string>? Env { get; init; }
        public int? TimeoutMs { get; init; }
        // Milliseconds, the same clock as Date.now. Tests pass a fixed clock. The default reads the system clock.
        public Func<long>? Now { get; init; }
    }

    public sealed class CheckRequest
    {
        public required string Command { get; init; }
        public required string[] Args { get; init; }
        public required string WorkingDirectory { get; init; }
        public required IReadOnlyDictionary<string, string> Environment { get; init; }
        public required int TimeoutMs { get; init; }
    }

    // The starter owns the child. Tests supply one and do not launch npm. Output, Close, and Error are already set.
    public sealed class CheckEvents
    {
        public Action<string> Output { get; set; } = static _ => { };
        public Action<int?> Close { get; set; } = static _ => { };
        public Action<string> Error { get; set; } = static _ => { };
        public Action Kill { get; set; } = static () => { };
    }

    public delegate void CheckStarter(CheckRequest request, CheckEvents events);

    // Resolves to null when no tests are found, otherwise the label, exit code, timeout, seconds, and tailed output.
    public static Task<CheckResult?> RunChecks(string? folder, CheckOptions? options = null, CheckStarter? starter = null)
    {
        var spec = TestCommand(folder);
        if (spec is null || string.IsNullOrEmpty(folder)) return Task.FromResult<CheckResult?>(null);
        starter ??= DefaultStarter;
        var now = options?.Now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var started = now();
        var timeoutMs = options?.TimeoutMs ?? TimeoutMs;
        if (timeoutMs < 0) timeoutMs = 0;
        var environment = ChildEnvironment(options?.Env);
        var request = new CheckRequest
        {
            Command = spec.Command,
            Args = spec.Args,
            WorkingDirectory = folder,
            Environment = environment,
            TimeoutMs = timeoutMs
        };
        var gate = new object();
        var output = "";
        var timedOut = false;
        var done = false;
        var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<CheckResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new CheckEvents();

        void Add(string chunk)
        {
            lock (gate) output = Tail(output + chunk, ReaderTail);
        }
        void Finish(int? exitCode)
        {
            string snapshot;
            bool timed;
            lock (gate)
            {
                if (done) return;
                done = true;
                snapshot = Tail(output);
                timed = timedOut;
            }
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
            var seconds = Math.Floor((now() - started) / 100.0 + 0.5) / 10.0;
            completion.TrySetResult(new CheckResult(spec.Label, exitCode, timed, seconds, snapshot));
        }

        events.Output = Add;
        events.Close = Finish;
        events.Error = message => { Add("\n" + message); Finish(null); };
        starter(request, events);
        var arm = false;
        lock (gate) arm = !done;
        if (arm)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(timeoutMs, cancel.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                OnTimeout();
            });
        }
        return completion.Task;

        void OnTimeout()
        {
            Action kill;
            lock (gate)
            {
                if (done) return;
                timedOut = true;
                kill = events.Kill;
            }
            try { kill(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException) { }
        }
    }

    // A parent test runner's context would turn the child run into its subtest, which exits 0.
    static Dictionary<string, string> ChildEnvironment(IReadOnlyDictionary<string, string>? overlay)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key) env[key] = entry.Value as string ?? "";
        }
        if (overlay is not null)
        {
            foreach (var pair in overlay) env[pair.Key] = pair.Value;
        }
        env["CI"] = "1";
        env.Remove("NODE_TEST_CONTEXT");
        return env;
    }

    static void DefaultStarter(CheckRequest request, CheckEvents events)
    {
        var process = new Process();
        process.StartInfo.FileName = request.Command;
        process.StartInfo.WorkingDirectory = request.WorkingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.Environment.Clear();
        foreach (var pair in request.Environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        foreach (var arg in request.Args) process.StartInfo.ArgumentList.Add(arg);
        process.EnableRaisingEvents = true;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("spawn failed");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            events.Error(error.Message);
            process.Dispose();
            return;
        }
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        var stdout = Pump(process.StandardOutput.BaseStream, events.Output);
        var stderr = Pump(process.StandardError.BaseStream, events.Output);
        var closed = 0;
        void OnExit(object? sender, EventArgs args)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { Task.WaitAll(stdout, stderr); } catch (Exception error) when (error is AggregateException or ObjectDisposedException) { }
            try { events.Close(process.ExitCode); }
            finally { process.Dispose(); }
        }
        events.Kill = () => KillChild(process);
        process.Exited += OnExit;
        // Exited can fire before the handler is attached when the child is already gone.
        if (process.HasExited) OnExit(null, EventArgs.Empty);
    }

    static async Task Pump(Stream stream, Action<string> onOutput)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) return;
                onOutput(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed because the child exited or was killed.
        }
    }

    // Windows kills the tree. Elsewhere the child itself gets SIGKILL. A failed kill is a race with exit.
    static void KillChild(Process process)
    {
        try
        {
            if (process.HasExited) return;
            if (!OperatingSystem.IsWindows())
            {
                process.Kill();
                return;
            }
            var info = new ProcessStartInfo("taskkill")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("/PID");
            info.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("/T");
            info.ArgumentList.Add("/F");
            using var killer = Process.Start(info);
            if (killer is null) return;
            var discardOut = killer.StandardOutput.ReadToEndAsync();
            var discardErr = killer.StandardError.ReadToEndAsync();
            killer.WaitForExit();
            Task.WaitAll(discardOut, discardErr);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or AggregateException)
        {
            // The child already exited, or taskkill could not run.
        }
    }
}
