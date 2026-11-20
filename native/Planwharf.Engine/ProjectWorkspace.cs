// Port of engine/project-workspace.cjs.
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Worker launch folders stay inside the linked project, or a worktree this app registered.
public static class ProjectWorkspace
{
    const int RealpathDepth = 40;
    const int GitBuffer = 1024 * 1024;
    const int GitTimeoutMs = 10000;

    public readonly record struct WorkspaceBinding(string Folder, string LinkedFolder);

    public sealed class WorkerJob
    {
        public string? Kind { get; init; }
        // Null means the property was omitted. JavaScript null is not a separate value here.
        public string? Folder { get; init; }
    }

    public sealed class WorkerLaunch
    {
        public string? Folder { get; init; }
        public string? LinkedFolder { get; init; }
        // Null means omitted. An empty string is set and must match the launch folder.
        public string? Cwd { get; init; }
        public string? Kind { get; init; }
        public string? ResultPath { get; init; }
        public string? Result { get; init; }
        public string? ProjectId { get; init; }
        public string? MegaTaskId { get; init; }
        public string? SubTaskId { get; init; }
        public WorkerJob? Job { get; init; }
    }

    public static string? LinkedWorkspace(string? folder)
    {
        if (folder is null || folder.Trim().Length == 0 || !IsAbsolute(folder)) return null;
        try
        {
            var canonical = Canonical(folder);
            if (canonical is null) return null;
            return (File.GetAttributes(canonical) & FileAttributes.Directory) != 0 ? canonical : null;
        }
        catch { return null; }
    }

    // path.relative, then reject an absolute result, "..", and ".." plus the OS separator.
    public static bool Within(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        if (relative == ".") relative = "";
        return relative.Length == 0 || (!IsAbsolute(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public static Dictionary<string, string> GitEnvironment(IReadOnlyDictionary<string, string>? inherited = null)
    {
        var source = inherited ?? ProcessEnvironment();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source)
            if (!pair.Key.ToUpperInvariant().StartsWith("GIT_", StringComparison.Ordinal))
                env[pair.Key] = pair.Value;
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        env["GIT_TERMINAL_PROMPT"] = "0";
        return env;
    }

    static Dictionary<string, string> ProcessEnvironment()
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) copy[key] = value;
        return copy;
    }

    // Node path.isAbsolute for the OS this process is running on.
    static bool IsAbsolute(string path)
    {
        if (path.Length == 0) return false;
        if (!OperatingSystem.IsWindows()) return path[0] == '/';
        var head = path[0];
        if (head is '/' or '\\') return true;
        return path.Length > 2 && IsDrive(head) && path[1] == ':' && path[2] is '/' or '\\';
    }

    static bool IsDrive(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    // fs.realpathSync: walk each segment, resolve a link before applying later "..".
    static string? Canonical(string path) => Canonical(path, 0);

    static string? Canonical(string path, int depth)
    {
        if (depth > RealpathDepth || !TrySplit(path, out var root, out var pending)) return null;
        var done = new List<string>();
        for (var index = 0; index < pending.Count; index++)
        {
            var segment = pending[index];
            if (segment is "." or "") continue;
            if (segment == "..")
            {
                if (done.Count > 0) done.RemoveAt(done.Count - 1);
                continue;
            }
            var parent = Combine(root, done);
            if (!Directory.Exists(parent)) return null;
            var entry = FindEntry(parent, segment);
            if (entry is null) return null;
            if (entry.LinkTarget is not null)
            {
                var target = entry.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !TrySplit(StripExtended(target.FullName), out root, out var targetParts)) return null;
                done.Clear();
                pending.RemoveRange(0, index + 1);
                pending.InsertRange(0, targetParts);
                index = -1;
                if (++depth > RealpathDepth) return null;
                continue;
            }
            done.Add(entry.Name);
        }
        var result = Combine(root, done);
        return Path.Exists(result) ? result : null;
    }

    static string Combine(string root, List<string> parts)
    {
        var path = root;
        foreach (var part in parts) path = Path.Combine(path, part);
        return path;
    }

    static FileSystemInfo? FindEntry(string parent, string segment)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
                if (NameEquals(entry.Name, segment)) return entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var candidate = Path.Combine(parent, segment);
            if (!Path.Exists(candidate)) return null;
            return (File.GetAttributes(candidate) & FileAttributes.Directory) != 0
                ? new DirectoryInfo(candidate) : new FileInfo(candidate);
        }
        return null;
    }

    static string StripExtended(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    static bool NameEquals(string left, string right) => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool TrySplit(string path, out string root, out List<string> segments)
    {
        root = "";
        segments = [];
        if (string.IsNullOrEmpty(path)) return false;
        var text = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            if (text.Length >= 2 && text[1] == ':')
            {
                if (text.Length < 3 || text[2] != '\\') return false;
                root = char.ToUpperInvariant(text[0]) + ":\\";
                AddSegments(text[3..], segments);
                return true;
            }
            if (text.StartsWith("\\\\", StringComparison.Ordinal))
            {
                var parts = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return false;
                root = "\\\\" + parts[0] + "\\" + parts[1] + "\\";
                for (var i = 2; i < parts.Length; i++) segments.Add(parts[i]);
                return true;
            }
            if (text[0] == '\\')
            {
                var drive = Path.GetPathRoot(Environment.CurrentDirectory);
                if (string.IsNullOrEmpty(drive)) return false;
                root = drive.Length >= 2 && drive[1] == ':' ? char.ToUpperInvariant(drive[0]) + drive[1..] : drive;
                if (!root.EndsWith('\\')) root += "\\";
                AddSegments(text[1..], segments);
                return true;
            }
            return false;
        }
        if (text[0] != '/') return false;
        root = "/";
        AddSegments(text[1..], segments);
        return true;
    }

    static void AddSegments(string rest, List<string> segments)
    {
        foreach (var part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            segments.Add(part);
    }

    public static WorkspaceBinding RequireWorkerWorkspace(WorkerLaunch info)
    {
        var research = Research(info);
        var root = LinkedWorkspace(research ? info.Folder : info.LinkedFolder);
        if (root is null) throw new InvalidOperationException("A real linked project folder is required before worker launch.");
        var folder = LinkedWorkspace(info.Folder);
        // Same condition as JavaScript: !folder || folder !== root && (research || !registeredWorktree).
        if (folder is null)
            throw new InvalidOperationException("Worker launch folder must stay inside the linked project folder.");
        if (folder != root && (research || !RegisteredWorktree(root, folder, info)))
            throw new InvalidOperationException("Worker launch folder must stay inside the linked project folder.");
        if (info.Cwd is not null && LinkedWorkspace(info.Cwd) != folder)
            throw new InvalidOperationException("Worker cwd and job folder must match the linked project folder.");
        if (info.Job?.Folder is not null && LinkedWorkspace(info.Job.Folder) != folder)
            throw new InvalidOperationException("Worker cwd and job folder must match the linked project folder.");
        return new WorkspaceBinding(folder, research ? "" : root);
    }

    static bool Research(WorkerLaunch info)
    {
        var kind = info.Job?.Kind;
        if (string.IsNullOrEmpty(kind)) kind = info.Kind;
        return kind == "research";
    }

    static bool RegisteredWorktree(string root, string folder, WorkerLaunch info)
    {
        try
        {
            if (SafeGitWorkspace(root) is null) return false;
            var result = string.IsNullOrEmpty(info.ResultPath) ? info.Result : info.ResultPath;
            if (string.IsNullOrEmpty(result) || !IsAbsolute(result) || !ValidId(info.ProjectId) || !ValidId(info.MegaTaskId) || !ValidId(info.SubTaskId)) return false;
            var dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(result));
            if (string.IsNullOrEmpty(dataRoot)) return false;
            var expected = Path.Combine(dataRoot, "worktrees", info.ProjectId!, info.MegaTaskId!, info.SubTaskId!);
            if (folder != LinkedWorkspace(expected)) return false;
            return GitListsWorktree(root, folder);
        }
        catch { return false; }
    }

    static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);
    static bool ValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    // Inspect only an ordinary repository whose metadata cannot redirect to another project.
    // External Git worktrees and shared object stores require a separate explicit boundary.
    public static string? SafeGitWorkspace(string? folder)
    {
        var root = LinkedWorkspace(folder);
        if (root is null) return null;
        var metadata = Path.Combine(root, ".git");
        try
        {
            if (!PlainDirectory(metadata)) return null;
            var pending = new List<string> { metadata };
            var entries = 0;
            while (pending.Count > 0)
            {
                var current = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    // Excessive metadata is unavailable rather than an unbounded inspection scan.
                    if (++entries > 100000) return null;
                    var file = Path.Combine(current, entry.Name);
                    if (entry.LinkTarget is not null || Canonical(file) is not string canonical || !Within(metadata, canonical)) return null;
                    if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Add(file);
                }
            }
            foreach (var name in new[] { "commondir", "objects/info/alternates", "objects/info/http-alternates" })
                if (Path.Exists(Path.Combine(metadata, name.Replace('/', Path.DirectorySeparatorChar)))) return null;
            var config = File.ReadAllText(Path.Combine(metadata, "config"));
            if (IncludeSection.IsMatch(config) || RedirectSetting.IsMatch(config)) return null;
            return root;
        }
        catch { return null; }
    }

    static readonly Regex IncludeSection = new(@"^\s*\[\s*include(?:if)?(?:\s|\])", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ECMAScript);
    static readonly Regex RedirectSetting = new(@"^\s*(?:worktree|fsmonitor)\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ECMAScript);

    static bool PlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is null && (attributes & FileAttributes.Directory) != 0;
    }

    static bool GitListsWorktree(string root, string folder)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        foreach (var arg in new[] { "worktree", "list", "--porcelain", "-z" }) start.ArgumentList.Add(arg);
        var env = GitEnvironment();
        start.Environment.Clear();
        foreach (var pair in env) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start);
        if (process is null) return false;
        var stdoutTask = Task.Run(() => ReadLimited(process.StandardOutput));
        var stderrTask = Task.Run(() => ReadLimited(process.StandardError));
        if (!process.WaitForExit(GitTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            WaitQuiet(stdoutTask, stderrTask);
            return false;
        }
        process.WaitForExit();
        WaitQuiet(stdoutTask, stderrTask);
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (stdout.Overflow || stderr.Overflow || process.ExitCode != 0) return false;
        foreach (var record in Encoding.UTF8.GetString(stdout.Bytes).Split('\0'))
        {
            if (!record.StartsWith("worktree ", StringComparison.Ordinal)) continue;
            if (LinkedWorkspace(record[9..].Split('\n')[0]) == folder) return true;
        }
        return false;
    }

    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    readonly record struct Captured(byte[] Bytes, bool Overflow);

    // Node maxBuffer counts bytes. Decode as UTF-8, matching spawnSync encoding: 'utf8'.
    static Captured ReadLimited(StreamReader reader)
    {
        try
        {
            using var memory = new MemoryStream();
            var chars = new char[4096];
            var overflow = false;
            int read;
            while ((read = reader.Read(chars, 0, chars.Length)) > 0)
            {
                var bytes = Utf8.GetBytes(chars, 0, read);
                if (!overflow)
                {
                    if (memory.Length + bytes.Length > GitBuffer) overflow = true;
                    else memory.Write(bytes);
                }
            }
            return new Captured(memory.ToArray(), overflow);
        }
        catch { return new Captured([], true); }
    }

    static void WaitQuiet(params Task[] tasks)
    {
        try { Task.WaitAll(tasks, 5000); } catch { }
    }
}
