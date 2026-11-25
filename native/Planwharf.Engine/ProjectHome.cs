// Port of engine/project-home.cjs.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed record ProjectHomeFileStat(long Size, bool IsFile, bool IsDirectory, bool IsSymbolicLink);

public interface IProjectHomeFiles
{
    Task<string?> RealPathAsync(string path);
    Task<ProjectHomeFileStat> LStatAsync(string path);
    Task<ProjectHomeFileStat> StatAsync(string path);
    Task<byte[]> ReadFileAsync(string path);
    Task<string> ReadLinkAsync(string path);
    Task<byte[]> ReadTailAsync(string path, int maxBytes);
}

public interface IProjectHomeGit
{
    Task<string?> RunGitAsync(string folder, IReadOnlyList<string> args);
}

public sealed class ProjectHomeFiles : IProjectHomeFiles
{
    public Task<string?> RealPathAsync(string path)
    {
        try
        {
            var canonical = ProjectWorkspace.LinkedWorkspace(path);
            if (canonical != null) return Task.FromResult<string?>(canonical);
            if (File.Exists(path) || Directory.Exists(path))
            {
                var full = Path.GetFullPath(path);
                var dir = Path.GetDirectoryName(full);
                var dirCanonical = dir != null ? ProjectWorkspace.LinkedWorkspace(dir) : null;
                if (dirCanonical != null)
                    return Task.FromResult<string?>(Path.Combine(dirCanonical, Path.GetFileName(full)));
                return Task.FromResult<string?>(full);
            }
            return Task.FromResult<string?>(null);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<ProjectHomeFileStat> LStatAsync(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists)
        {
            var isLink = fileInfo.LinkTarget != null || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0;
            var isDir = (fileInfo.Attributes & FileAttributes.Directory) != 0;
            long size = isLink && fileInfo.LinkTarget != null ? Encoding.UTF8.GetByteCount(fileInfo.LinkTarget) : fileInfo.Length;
            return Task.FromResult(new ProjectHomeFileStat(size, !isDir, isDir, isLink));
        }
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Exists)
        {
            var isLink = dirInfo.LinkTarget != null || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0;
            long size = isLink && dirInfo.LinkTarget != null ? Encoding.UTF8.GetByteCount(dirInfo.LinkTarget) : 0;
            return Task.FromResult(new ProjectHomeFileStat(size, false, true, isLink));
        }
        throw new FileNotFoundException("File not found.", path);
    }

    public Task<ProjectHomeFileStat> StatAsync(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists)
        {
            var isDir = (fileInfo.Attributes & FileAttributes.Directory) != 0;
            return Task.FromResult(new ProjectHomeFileStat(fileInfo.Length, !isDir, isDir, false));
        }
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Exists)
        {
            return Task.FromResult(new ProjectHomeFileStat(0, false, true, false));
        }
        throw new FileNotFoundException("File not found.", path);
    }

    public Task<byte[]> ReadFileAsync(string path) => File.ReadAllBytesAsync(path);

    public Task<string> ReadLinkAsync(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget != null) return Task.FromResult(fileInfo.LinkTarget);
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.LinkTarget != null) return Task.FromResult(dirInfo.LinkTarget);
        var resolved = File.ResolveLinkTarget(path, returnFinalTarget: false);
        return Task.FromResult(resolved?.FullName ?? "");
    }

    public async Task<byte[]> ReadTailAsync(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var bytesToRead = (int)Math.Min(length, maxBytes);
        stream.Seek(length - bytesToRead, SeekOrigin.Begin);
        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, bytesToRead - totalRead));
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead < bytesToRead) Array.Resize(ref buffer, totalRead);
        return buffer;
    }
}

public sealed class ProjectHomeOptions
{
    public Func<string>? Now { get; init; }
    public string? PlanFile { get; init; }
    public Func<string, IReadOnlyList<string>, Task<string?>>? Git { get; init; }
    public IProjectHomeGit? GitAdapter { get; init; }
    public Func<string, JsonObject>? Inspect { get; init; }
    public IProjectHomeFiles? Files { get; init; }
}

public static class ProjectHome
{
    private static JsonArray HomeTimeline(JsonArray events, JsonObject durable) =>
        events.Count == 1 && !Execution.Truthy(events[0]?["reference"]?["megaTaskId"])
            ? events.DeepClone().AsArray()
            : ProjectHistory.SummarizeTimeline(events, durable);
    internal static readonly JsonSerializerOptions Json = Execution.Json;
    private const int GitMaxBuffer = 8 * 1024 * 1024;

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task<string?> RunDefaultGitAsync(string folder, IReadOnlyList<string> args)
    {
        using var process = new Process();
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(folder);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var pair in ProjectWorkspace.GitEnvironment()) start.Environment[pair.Key] = pair.Value;
        process.StartInfo = start;
        try
        {
            process.Start();
            using var cts = new CancellationTokenSource(5000);
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, cts.Token);
            var stderrTask = ReadLimitedAsync(process.StandardError, cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var (stdout, outOverflow) = await stdoutTask;
            var (_, errOverflow) = await stderrTask;
            if (outOverflow || errOverflow || process.ExitCode != 0) return null;
            return args.Contains("-z") ? stdout : stdout.Trim();
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
    }

    private static async Task<(string Text, bool Overflow)> ReadLimitedAsync(StreamReader reader, CancellationToken token)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        var totalBytes = 0;
        var overflow = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read == 0) break;
            var byteCount = Encoding.UTF8.GetByteCount(buffer, 0, read);
            totalBytes += byteCount;
            if (totalBytes > GitMaxBuffer)
            {
                overflow = true;
                break;
            }
            sb.Append(buffer, 0, read);
        }
        return (sb.ToString(), overflow);
    }

    internal static async Task<bool> ContainedParentAsync(string root, string file, IProjectHomeFiles files)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) return false;
            var parent = await files.RealPathAsync(dir);
            if (string.IsNullOrEmpty(parent)) return false;
            var relative = Path.GetRelativePath(root, parent);
            if (relative == ".") relative = "";
            return relative == "" || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !relative.StartsWith("../") && !Path.IsPathRooted(relative));
        }
        catch
        {
            return false;
        }
    }

    public static async Task<JsonObject> LocalGitAsync(string? folder, ProjectHomeOptions? options = null)
    {
        var files = options?.Files ?? new ProjectHomeFiles();
        var gitFn = options?.Git ?? (options?.GitAdapter != null ? options.GitAdapter.RunGitAsync : (Func<string, IReadOnlyList<string>, Task<string?>>)RunDefaultGitAsync);
        var planFile = options?.PlanFile;

        var linked = ProjectWorkspace.LinkedWorkspace(folder);
        if (linked == null) return new JsonObject { ["readable"] = false, ["reason"] = "No local folder linked." };
        var root = ProjectWorkspace.SafeGitWorkspace(linked);
        if (root == null) return new JsonObject { ["readable"] = false, ["reason"] = "Contained local Git history unavailable." };
        var gitRoot = await gitFn(root, ["rev-parse", "--show-toplevel"]);
        if (gitRoot == null) return new JsonObject { ["readable"] = false, ["reason"] = "Local Git history unavailable." };

        string? relative = null;
        if (!string.IsNullOrEmpty(planFile))
        {
            var fullPlan = Path.GetFullPath(planFile);
            relative = Path.GetRelativePath(root, fullPlan).Replace('\\', '/');
        }
        const string scope = ".";
        var prefix = Path.GetRelativePath(gitRoot, root).Replace('\\', '/');
        if (prefix == ".") prefix = "";
        string? LocalName(string name)
        {
            if (string.IsNullOrEmpty(prefix)) return name;
            return name.StartsWith(prefix + "/", StringComparison.Ordinal) ? name[(prefix.Length + 1)..] : null;
        }

        // Every Git command runs at the canonical linked folder with an explicit scope.
        var filesArgs = new List<string> { "--", "." };
        if (!string.IsNullOrEmpty(relative) && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            filesArgs.Add($":(exclude,literal){relative}");

        // Submodule worktrees may redirect to foreign Git metadata; inspect only this repository.
        var headTask = gitFn(root, ["rev-parse", "--verify", "HEAD"]);
        var statusTask = gitFn(root, ["status", "--ignore-submodules=all", "--porcelain=v1", "--no-renames", "-uall", .. filesArgs]);
        var diffTask = gitFn(root, ["diff", "--ignore-submodules=all", "--submodule=short", "--relative", "--no-renames", "--no-ext-diff", "--no-textconv", "HEAD", .. filesArgs]);
        await Task.WhenAll(headTask, statusTask, diffTask);
        var head = await headTask;
        var status = await statusTask;
        var diff = await diffTask;

        // Include untracked contents so edits to an already-dirty file are visible.
        var untracked = await gitFn(root, ["ls-files", "--others", "--exclude-standard", "-z", .. filesArgs]);
        string? fingerprint = null;
        if (status != null && diff != null && untracked != null)
        {
            try
            {
                var contents = new JsonArray();
                long bytes = 0;
                var untrackedEntries = untracked.Split('\0');
                if (untrackedEntries.Length > 2000) throw new InvalidOperationException("Too many files for reliable comparison");
                var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                foreach (var name in untrackedEntries.Where(s => !string.IsNullOrEmpty(s)))
                {
                    var file = Path.GetFullPath(Path.Combine(root, name));
                    if (!file.StartsWith(rootSep, StringComparison.Ordinal) || !await ContainedParentAsync(root, file, files))
                        throw new InvalidOperationException("Outside linked folder");
                    var stat = await files.LStatAsync(file);
                    bytes += stat.Size;
                    if (bytes > 8 * 1024 * 1024) throw new InvalidOperationException("Files too large for reliable comparison");
                    var fileHash = stat.IsSymbolicLink ? Hash(await files.ReadLinkAsync(file)) : Hash(await files.ReadFileAsync(file));
                    contents.Add((JsonNode)new JsonArray((JsonNode?)JsonValue.Create(name), (JsonNode?)JsonValue.Create(fileHash)));
                }
                var fpPayload = new JsonArray((JsonNode?)JsonValue.Create(status), (JsonNode?)JsonValue.Create(diff), (JsonNode)contents);
                fingerprint = Hash(fpPayload.ToJsonString(Json));
            }
            catch
            {
                // Unknown is preferable to a fabricated comparison.
            }
        }

        var paths = await gitFn(root, ["status", "--ignore-submodules=all", "--porcelain=v1", "--no-renames", "-z", "-uall", .. filesArgs]);
        var changed = new JsonArray();
        var records = (paths ?? "").Split('\0');
        long detailBytes = 0;
        var rootSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (string.IsNullOrEmpty(record)) continue;
            var statusCode = record.Length >= 2 ? record[..2] : record;
            var name = LocalName(record.Length > 3 ? record[3..] : "");
            string? from = null;
            if (Regex.IsMatch(statusCode, "R|C", RegexOptions.ECMAScript) && index + 1 < records.Length)
            {
                index++;
                from = LocalName(records[index]);
            }
            if (string.IsNullOrEmpty(name)) continue;
            string? fileFingerprint = null;
            try
            {
                var target = Path.GetFullPath(Path.Combine(root, name));
                if (!target.StartsWith(rootSeparator, StringComparison.Ordinal) || !await ContainedParentAsync(root, target, files))
                    throw new InvalidOperationException("Outside linked folder");
                var stat = await files.LStatAsync(target);
                detailBytes += stat.Size;
                if (detailBytes <= 8 * 1024 * 1024)
                    fileFingerprint = stat.IsSymbolicLink ? Hash(await files.ReadLinkAsync(target)) : Hash(await files.ReadFileAsync(target));
            }
            catch
            {
                if (statusCode.Contains('D')) fileFingerprint = "deleted";
            }
            changed.Add((JsonNode)new JsonObject
            {
                ["path"] = name,
                ["from"] = from,
                ["status"] = statusCode.Trim(),
                ["fingerprint"] = fileFingerprint
            });
        }

        int? changedFilesCount = null;
        if (status != null)
        {
            changedFilesCount = status.Split('\n').Select(line => line.TrimEnd('\r')).Count(s => !string.IsNullOrEmpty(s));
        }

        return new JsonObject
        {
            ["readable"] = status != null,
            ["root"] = root,
            ["scope"] = scope,
            ["head"] = head,
            ["changedFiles"] = changedFilesCount,
            ["fingerprint"] = fingerprint,
            ["files"] = changed
        };
    }

    public static JsonObject LocalGit(string? folder, ProjectHomeOptions? options = null) =>
        LocalGitAsync(folder, options).GetAwaiter().GetResult();

    internal static async Task<(JsonArray Actions, JsonArray Reports)> RecentActionsAsync(JsonObject? row, IProjectHomeFiles files)
    {
        var messages = new JsonArray();
        var reports = new JsonArray();
        if (row != null && row["dispatch"] is JsonArray dispatch)
        {
            foreach (var partNode in dispatch)
            {
                if (partNode is not JsonObject part) continue;
                var logPath = part["logPath"]?.GetValue<string>();
                if (string.IsNullOrEmpty(logPath)) continue;
                try
                {
                    var tailBytes = await files.ReadTailAsync(logPath, 1024 * 1024);
                    var text = Encoding.UTF8.GetString(tailBytes);
                    var partLabel = part["label"]?.GetValue<string>();
                    var partModel = part["modelId"]?.GetValue<string>();
                    var workerLabel = !string.IsNullOrEmpty(partLabel) ? partLabel : (!string.IsNullOrEmpty(partModel) ? partModel : "");
                    var actions = Activity.ActivityMessages(text);
                    var slice = actions.Count > 12 ? actions.TakeLast(12) : actions;
                    foreach (var action in slice)
                    {
                        messages.Add((JsonNode)new JsonObject
                        {
                            ["worker"] = workerLabel,
                            ["text"] = action
                        });
                    }
                    var report = Activity.WorkerReport(text);
                    if (!string.IsNullOrEmpty(report))
                    {
                        reports.Add((JsonNode)new JsonObject
                        {
                            ["worker"] = workerLabel,
                            ["text"] = report
                        });
                    }
                }
                catch
                {
                    // Ignore unreadable log paths.
                }
            }
        }
        if (messages.Count == 0 && row != null)
        {
            var workerTail = row["workerTail"]?.GetValue<string>() ?? "";
            var rowLabel = row["workerLabel"]?.GetValue<string>();
            var workerLabel = !string.IsNullOrEmpty(rowLabel) ? rowLabel : "Worker";
            var actions = Activity.ActivityMessages(workerTail);
            var slice = actions.Count > 12 ? actions.TakeLast(12) : actions;
            foreach (var action in slice)
            {
                messages.Add((JsonNode)new JsonObject
                {
                    ["worker"] = workerLabel,
                    ["text"] = action
                });
            }
        }
        return (messages, reports);
    }

    internal static async Task<JsonObject?> ExecutionEvidenceAsync(JsonObject? row, IReadOnlyList<JsonObject> researches, IProjectHomeFiles files)
    {
        if (row == null) return null;
        string? result = null;
        var resultPath = row["resultPath"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(resultPath))
        {
            try
            {
                var stat = await files.StatAsync(resultPath);
                if (stat.IsFile && stat.Size > 0 && stat.Size <= 8 * 1024 * 1024)
                {
                    var pathBytes = Encoding.UTF8.GetBytes(resultPath + "\0");
                    var fileBytes = await files.ReadFileAsync(resultPath);
                    var combined = new byte[pathBytes.Length + fileBytes.Length];
                    Buffer.BlockCopy(pathBytes, 0, combined, 0, pathBytes.Length);
                    Buffer.BlockCopy(fileBytes, 0, combined, pathBytes.Length, fileBytes.Length);
                    result = Hash(combined);
                }
            }
            catch
            {
                // An assigned output path is not evidence of an execution result.
            }
        }

        var (actions, reports) = await RecentActionsAsync(row, files);
        var checkpoint = row["checkpoint"];
        string? checkpointHash = Execution.Truthy(checkpoint) ? Hash(checkpoint!.ToJsonString(Json)) : null;

        var sortedResearch = researches
            .OrderBy(r => r["id"]?.GetValue<string>() ?? "", StringComparer.Ordinal)
            .Select(r =>
            {
                var rAttemptId = r["attemptId"]?.GetValue<string>();
                return (JsonNode)new JsonObject
                {
                    ["id"] = r["id"]?.DeepClone(),
                    ["status"] = r["status"]?.DeepClone(),
                    ["attemptId"] = !string.IsNullOrEmpty(rAttemptId) ? (JsonNode?)JsonValue.Create(rAttemptId) : null,
                    ["reportAvailable"] = r["reportAvailable"] is JsonValue rv && rv.TryGetValue<bool>(out var b) && b
                };
            })
            .ToArray();

        var jobId = row["jobId"]?.GetValue<string>();
        var attemptId = row["attemptId"]?.GetValue<string>();

        return new JsonObject
        {
            ["status"] = row["status"]?.DeepClone(),
            ["jobId"] = !string.IsNullOrEmpty(jobId) ? (JsonNode?)JsonValue.Create(jobId) : null,
            ["jobTitle"] = row["jobTitle"]?.GetValue<string>() ?? "",
            ["activity"] = row["workerActivity"]?.GetValue<string>() ?? "",
            ["actions"] = actions,
            ["reports"] = reports,
            ["attemptId"] = !string.IsNullOrEmpty(attemptId) ? (JsonNode?)JsonValue.Create(attemptId) : null,
            ["checkpoint"] = checkpointHash,
            ["result"] = result,
            ["research"] = new JsonArray(sortedResearch)
        };
    }

    public static async Task<JsonObject> CompareAsync(JsonObject? previous, JsonObject? current, ProjectHomeOptions? options = null)
    {
        var changes = new JsonArray();
        var unavailable = new JsonArray();
        var details = new JsonArray();

        if (previous == null || current == null)
        {
            return new JsonObject
            {
                ["firstVisit"] = true,
                ["changes"] = changes,
                ["unavailable"] = unavailable,
                ["details"] = details
            };
        }

        var gitFn = options?.Git ?? (options?.GitAdapter != null ? options.GitAdapter.RunGitAsync : (Func<string, IReadOnlyList<string>, Task<string?>>)RunDefaultGitAsync);

        var prevRevisionIds = (previous["revisionIds"] as JsonArray ?? []).Select(Execution.Text).ToHashSet(StringComparer.Ordinal);
        var currentRevisionIds = (current["revisionIds"] as JsonArray ?? []).Select(Execution.Text).ToArray();
        var revisions = currentRevisionIds.Count(id => !prevRevisionIds.Contains(id));
        if (revisions > 0)
            changes.Add((JsonNode)JsonValue.Create($"{revisions} new Living Plan revision{(revisions == 1 ? "" : "s")}")!);

        var a = previous["git"] as JsonObject;
        var b = current["git"] as JsonObject;
        var aHead = a?["head"]?.GetValue<string>();
        var bHead = b?["head"]?.GetValue<string>();
        var aRoot = a?["root"]?.GetValue<string>();
        var bRoot = b?["root"]?.GetValue<string>();
        var aScope = a?["scope"]?.GetValue<string>();
        var bScope = b?["scope"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(aHead) && !string.IsNullOrEmpty(bHead) && aRoot == bRoot && aScope == bScope && aHead != bHead)
        {
            var root = bRoot ?? "";
            var scope = !string.IsNullOrEmpty(bScope) ? bScope : ".";
            var ancestor = await gitFn(root, ["merge-base", "--is-ancestor", aHead, bHead]);
            string? count = null;
            if (ancestor != null)
                count = await gitFn(root, ["rev-list", "--count", $"{aHead}..{bHead}", "--", scope]);

            if (count != null && Regex.IsMatch(count, @"^\d+$", RegexOptions.ECMAScript))
            {
                if (int.TryParse(count, out var countNum) && countNum > 0)
                {
                    changes.Add((JsonNode)JsonValue.Create($"{count} commit{(count == "1" ? "" : "s")} added")!);
                    var log = await gitFn(root, ["log", "-100", "--format=%h%x09%s", $"{aHead}..{bHead}", "--", scope]);
                    foreach (var line in (log ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrEmpty(l)))
                    {
                        var split = line.IndexOf('\t');
                        if (split >= 0)
                            details.Add((JsonNode)JsonValue.Create($"Commit {line[..split]}: {line[(split + 1)..]}")!);
                        else
                            details.Add((JsonNode)JsonValue.Create($"Commit {line}")!);
                    }
                    if (countNum > 100)
                        details.Add((JsonNode)JsonValue.Create("Showing the latest 100 commit subjects.")!);
                }
            }
            else
            {
                unavailable.Add((JsonNode)JsonValue.Create("Git history changed; a reliable commit count is unavailable.")!);
            }
        }
        else if (aHead != bHead || aRoot != bRoot || aScope != bScope)
        {
            unavailable.Add((JsonNode)JsonValue.Create("No comparable Git baseline.")!);
        }

        var aFp = a?["fingerprint"]?.GetValue<string>();
        var bFp = b?["fingerprint"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(aFp) && !string.IsNullOrEmpty(bFp) && aRoot == bRoot && aScope == bScope)
        {
            if (aFp != bFp)
            {
                var changedFiles = b?["changedFiles"]?.GetValue<int>() ?? 0;
                changes.Add((JsonNode)JsonValue.Create(changedFiles > 0
                    ? $"{changedFiles} file{(changedFiles == 1 ? "" : "s")} changed locally"
                    : "Local changes cleared")!);
            }
        }
        else if (a?["readable"]?.GetValue<bool>() == true || b?["readable"]?.GetValue<bool>() == true)
        {
            unavailable.Add((JsonNode)JsonValue.Create("Local file comparison unavailable.")!);
        }

        var aFiles = (a?["files"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var bFiles = (b?["files"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        foreach (var file in bFiles)
        {
            var filePath = file["path"]?.GetValue<string>();
            var fileStatus = file["status"]?.GetValue<string>() ?? "";
            var fileFp = file["fingerprint"]?.GetValue<string>();
            var fileFrom = file["from"]?.GetValue<string>();
            var before = aFiles.FirstOrDefault(item => item["path"]?.GetValue<string>() == filePath);
            var beforeStatus = before?["status"]?.GetValue<string>();
            var beforeFp = before?["fingerprint"]?.GetValue<string>();
            if (before == null || beforeStatus != fileStatus || (!string.IsNullOrEmpty(fileFp) && beforeFp != fileFp))
            {
                var verb = fileStatus == "??" || fileStatus.Contains('A') ? "Added locally"
                    : fileStatus.Contains('D') ? "Deleted locally"
                    : fileStatus.Contains('R') ? "Renamed locally"
                    : "Edited locally";
                var fromSuffix = !string.IsNullOrEmpty(fileFrom) ? $" (from {fileFrom})" : "";
                details.Add((JsonNode)JsonValue.Create($"{verb}: {filePath}{fromSuffix}")!);
            }
        }

        var currentRevisions = current["revisions"] as JsonArray;
        var prevRevisions = previous["revisions"] as JsonArray;
        var latest = currentRevisions?.LastOrDefault() as JsonObject;
        var beforeRevision = prevRevisions?.LastOrDefault() as JsonObject;
        if (latest != null && beforeRevision != null &&
            latest["id"]?.ToString() != beforeRevision["id"]?.ToString())
        {
            details.Add((JsonNode)JsonValue.Create($"Living Plan R{latest["id"]}: {latest["title"]}")!);
            if (latest["state"]?.ToString() != beforeRevision["state"]?.ToString())
                details.Add((JsonNode)JsonValue.Create($"Recorded progress: {latest["state"]}")!);
            if (latest["direction"]?.ToString() != beforeRevision["direction"]?.ToString())
                details.Add((JsonNode)JsonValue.Create($"Updated goal: {latest["direction"]}")!);

            var beforeClosed = (beforeRevision["closed"] as JsonArray ?? []).Select(Execution.Text).ToHashSet(StringComparer.Ordinal);
            foreach (var decision in (latest["closed"] as JsonArray ?? []).Select(Execution.Text))
            {
                if (!beforeClosed.Contains(decision))
                    details.Add((JsonNode)JsonValue.Create($"Decision recorded: {decision}")!);
            }
        }

        var oldExec = previous["execution"] as JsonObject;
        var nowExec = current["execution"] as JsonObject;
        if (nowExec != null && oldExec != null)
        {
            var oldStatus = oldExec["status"]?.GetValue<string>();
            var nowStatus = nowExec["status"]?.GetValue<string>();
            var oldJobId = oldExec["jobId"]?.GetValue<string>();
            var nowJobId = nowExec["jobId"]?.GetValue<string>();
            var oldAttemptId = oldExec["attemptId"]?.GetValue<string>();
            var nowAttemptId = nowExec["attemptId"]?.GetValue<string>();

            if (oldStatus != nowStatus || oldJobId != nowJobId || oldAttemptId != nowAttemptId)
                changes.Add((JsonNode)JsonValue.Create($"Project work {(nowStatus == "complete" ? "completed" : "changed")}")!);

            var oldCheckpoint = oldExec["checkpoint"]?.GetValue<string>();
            var nowCheckpoint = nowExec["checkpoint"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(nowCheckpoint) && oldCheckpoint != nowCheckpoint)
                changes.Add((JsonNode)JsonValue.Create("New checkpoint saved")!);

            var oldResult = oldExec["result"]?.GetValue<string>();
            var nowResult = nowExec["result"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(nowResult) && oldResult != nowResult)
                changes.Add((JsonNode)JsonValue.Create(oldResult != null ? "Execution result updated" : "New execution result available")!);

            var oldActions = (oldExec["actions"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(a => $"{a["worker"]?.GetValue<string>()}: {a["text"]?.GetValue<string>()}")
                .ToHashSet(StringComparer.Ordinal);

            var newActions = (nowExec["actions"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(a => !oldActions.Contains($"{a["worker"]?.GetValue<string>()}: {a["text"]?.GetValue<string>()}"))
                .ToArray();

            if (newActions.Length > 0)
                changes.Add((JsonNode)JsonValue.Create("New worker activity recorded")!);

            if (oldStatus != nowStatus || oldJobId != nowJobId || oldResult != nowResult || newActions.Length > 0)
            {
                var nowJobTitle = nowExec["jobTitle"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(nowJobTitle))
                    details.Add((JsonNode)JsonValue.Create($"Work item: {nowJobTitle} ({nowStatus})")!);
                foreach (var action in newActions)
                    details.Add((JsonNode)JsonValue.Create($"{action["worker"]?.GetValue<string>()}: {action["text"]?.GetValue<string>()}")!);
            }

            var oldResearches = (oldExec["research"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            var nowResearches = (nowExec["research"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            foreach (var research in nowResearches)
            {
                var rId = research["id"]?.GetValue<string>();
                var rStatus = research["status"]?.GetValue<string>();
                var rAttemptId = research["attemptId"]?.GetValue<string>();
                var rReport = research["reportAvailable"]?.GetValue<bool>() ?? false;
                var beforeR = oldResearches.FirstOrDefault(r => r["id"]?.GetValue<string>() == rId);
                var bStatus = beforeR?["status"]?.GetValue<string>();
                var bAttemptId = beforeR?["attemptId"]?.GetValue<string>();
                var bReport = beforeR?["reportAvailable"]?.GetValue<bool>() ?? false;
                if (beforeR == null || bStatus != rStatus || bAttemptId != rAttemptId || bReport != rReport)
                    changes.Add((JsonNode)JsonValue.Create($"Research {(rStatus == "complete" ? "completed" : "changed")}")!);
            }
        }
        else if (nowExec != null && oldExec == null)
        {
            unavailable.Add((JsonNode)JsonValue.Create("No comparable execution baseline.")!);
        }

        var prevNotes = (previous["noteIds"] as JsonArray ?? []).Select(Execution.Text).ToHashSet(StringComparer.Ordinal);
        var currentNotes = (current["noteIds"] as JsonArray ?? []).Select(Execution.Text).ToArray();
        var notes = currentNotes.Count(id => !prevNotes.Contains(id));
        if (notes > 0)
            changes.Add((JsonNode)JsonValue.Create($"{notes} new user note(s)")!);

        return new JsonObject
        {
            ["firstVisit"] = false,
            ["changes"] = changes,
            ["unavailable"] = unavailable,
            ["details"] = details
        };
    }

    public static JsonObject Compare(JsonObject? previous, JsonObject? current, ProjectHomeOptions? options = null) =>
        CompareAsync(previous, current, options).GetAwaiter().GetResult();

    private static async Task<JsonObject> ProjectHomeCoreAsync(
        JsonObject project,
        JsonObject snapshot,
        bool baselineOmitted,
        JsonNode? explicitBaseline,
        ProjectHomeOptions? options = null)
    {
        var revisionsArray = project["revisions"] as JsonArray ?? [];
        var revision = revisionsArray.LastOrDefault() as JsonObject ?? new JsonObject();
        var projectInfo = project["project"] as JsonObject ?? new JsonObject();
        var projectId = projectInfo["id"]?.GetValue<string>() ?? "";
        var projectFolder = projectInfo["folder"]?.GetValue<string>();

        var projectsArray = snapshot["projects"] as JsonArray ?? [];
        var row = projectsArray.OfType<JsonObject>().FirstOrDefault(r => r["id"]?.GetValue<string>() == projectId);

        var allResearches = (snapshot["researches"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var researches = allResearches.Where(r => r["projectId"]?.GetValue<string>() == projectId).ToList();

        var local = await LocalGitAsync(projectFolder, options);
        var files = options?.Files ?? new ProjectHomeFiles();
        var evidence = await ExecutionEvidenceAsync(row, researches, files);

        var durable = snapshot["execution"] as JsonObject ?? new JsonObject();
        var allMegaTasks = (durable["megaTasks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var megaTasks = allMegaTasks.Where(mega => mega["projectId"]?.GetValue<string>() == projectId).ToList();

        var rowMegaTaskId = row?["megaTaskId"]?.GetValue<string>();
        var mega = (!string.IsNullOrEmpty(rowMegaTaskId) ? megaTasks.FirstOrDefault(t => t["id"]?.GetValue<string>() == rowMegaTaskId) : null) ?? megaTasks.LastOrDefault();
        var megaId = mega?["id"]?.GetValue<string>();

        var allSubTasks = (durable["subTasks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var subTasks = allSubTasks.Where(sub => sub["megaTaskId"]?.GetValue<string>() == megaId).ToList();

        var megaIds = megaTasks.Select(m => m["id"]?.GetValue<string>()).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        var allResults = (durable["results"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var results = allResults.Where(r => megaIds.Contains(r["megaTaskId"]?.GetValue<string>() ?? "")).ToList();

        var technicalTimeline = (durable["timeline"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(e => e["projectId"]?.GetValue<string>() == projectId)
            .Select(e => (JsonObject)e.DeepClone())
            .ToList();
        var technicalTimelineArray = new JsonArray(technicalTimeline.Select(e => (JsonNode)e).ToArray());
        // A single ungrouped event needs neither aggregation nor sorting. JavaScript's
        // sort never reads createdAt in this case, including older undated events.
        var timeline = HomeTimeline(technicalTimelineArray, durable);

        var resumeTargets = (durable["checkpoints"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(cp =>
            {
                if (cp["projectId"]?.GetValue<string>() != projectId) return false;
                var cpMegaId = cp["megaTaskId"]?.GetValue<string>();
                var cpMega = megaTasks.FirstOrDefault(m => m["id"]?.GetValue<string>() == cpMegaId);
                var status = cpMega?["status"]?.GetValue<string>();
                return status is "PAUSED" or "FAILED";
            })
            .Select(cp =>
            {
                var target = (JsonObject)cp.DeepClone();
                var cpMegaId = cp["megaTaskId"]?.GetValue<string>();
                var matchingResearch = allResearches.FirstOrDefault(r => r["megaTaskId"]?.GetValue<string>() == cpMegaId);
                if (matchingResearch?["id"] != null)
                {
                    target["researchId"] = matchingResearch["id"]!.DeepClone();
                }
                return target;
            })
            .ToList();

        JsonObject? executionBaseline = null;
        JsonArray reports = [];
        if (evidence != null)
        {
            executionBaseline = new JsonObject();
            foreach (var kv in evidence)
            {
                if (kv.Key == "reports")
                {
                    if (kv.Value is JsonArray arr) reports = (JsonArray)arr.DeepClone();
                }
                else
                {
                    executionBaseline[kv.Key] = kv.Value?.DeepClone();
                }
            }
            executionBaseline["reportFingerprint"] = Hash(reports.ToJsonString(Json));
        }

        var technicalTimelineIds = technicalTimeline.Select(e => e["id"]?.DeepClone() ?? (JsonNode)JsonValue.Create("")!).ToArray();
        var homeObj = project["home"] as JsonObject;
        var noteIds = (homeObj?["notes"] as JsonArray ?? []).Select(n => n?["id"]?.DeepClone() ?? (JsonNode)JsonValue.Create("")!).ToArray();
        var revisionIds = revisionsArray.Select(r => r?["id"]?.DeepClone() ?? (JsonNode)JsonValue.Create(0)!).ToArray();

        var currentRevisions = new JsonArray((JsonNode)new JsonObject
        {
            ["id"] = revision["id"]?.DeepClone(),
            ["title"] = revision["title"]?.DeepClone(),
            ["state"] = revision["state"]?.DeepClone(),
            ["direction"] = revision["direction"]?.DeepClone(),
            ["closed"] = revision["closed"]?.DeepClone() ?? new JsonArray()
        });

        var current = new JsonObject
        {
            ["viewedAt"] = (options?.Now?.Invoke() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
            ["timelineEventIds"] = new JsonArray(technicalTimelineIds),
            ["noteIds"] = new JsonArray(noteIds),
            ["revisionIds"] = new JsonArray(revisionIds),
            ["revisions"] = currentRevisions,
            ["git"] = (JsonObject)local.DeepClone(),
            ["execution"] = executionBaseline
        };

        var dispatchArr = row?["dispatch"] as JsonArray ?? [];
        var runningDispatch = dispatchArr.OfType<JsonObject>().Count(p => Execution.Truthy(p["alive"]));
        var runningResearch = researches.Count(r => Execution.Truthy(r["alive"]) && r["status"]?.GetValue<string>() == "watching");
        var running = runningDispatch + runningResearch;

        var rowStatus = row?["status"]?.GetValue<string>();
        string labelText = (rowStatus switch
        {
            "watching" => running > 0 ? "Workers running" : "Waiting for worker progress",
            "needs_approval" => "Waiting for approval",
            "waiting_on_you" => "Waiting for your answer",
            "propose_grill" => "Waiting for your answer",
            "checkpointed" => "Paused \u2014 checkpoint saved",
            "complete" => "Completed",
            "failed" => "Blocked",
            "blocked_no_worker" => "Blocked",
            "blocked_no_folder" => "Blocked",
            "idle" => "Idle",
            _ => "Idle"
        });

        var next = ExecutionNext.GetHomeNext(project, row, durable);

        var canonicalLinked = ProjectWorkspace.LinkedWorkspace(projectFolder);
        var megaStatus = mega?["status"]?.GetValue<string>();
        string? action = null;
        if (string.IsNullOrEmpty(canonicalLinked))
        {
            action = null;
        }
        else if (mega != null)
        {
            if (megaStatus == "READY_FOR_APPROVAL") action = "approve";
            else if ((megaStatus is "PAUSED" or "FAILED") && resumeTargets.Count > 0) action = "resume";
            else action = null;
        }
        else if (rowStatus == "needs_approval") action = "approve";
        else if (Execution.Truthy(row?["canResume"])) action = "resume";
        else if (revision["open"] is JsonArray openArr && openArr.Count > 0) action = "grill";
        else action = null;

        var completed = results.Where(r => r["status"]?.GetValue<string>() == "COMPLETE").ToList();
        var workers = new JsonArray();
        foreach (var r in results.Where(res => res["megaTaskId"]?.GetValue<string>() == megaId && Execution.Truthy(res["pid"])))
        {
            workers.Add((JsonNode)new JsonObject
            {
                ["modelId"] = r["assignedModelId"]?.DeepClone(),
                ["provider"] = r["provider"]?.DeepClone(),
                ["cli"] = r["cli"]?.DeepClone(),
                ["subTaskId"] = r["subTaskId"]?.DeepClone(),
                ["attemptId"] = r["attemptId"]?.DeepClone(),
                ["status"] = r["status"]?.DeepClone()
            });
        }
        foreach (var sub in subTasks.Where(s => s["status"]?.GetValue<string>() == "RUNNING"))
        {
            var attemptIds = sub["attemptIds"] as JsonArray;
            workers.Add((JsonNode)new JsonObject
            {
                ["modelId"] = sub["assignedModelId"]?.DeepClone(),
                ["provider"] = sub["provider"]?.DeepClone(),
                ["cli"] = sub["cli"]?.DeepClone(),
                ["subTaskId"] = sub["id"]?.DeepClone(),
                ["attemptId"] = attemptIds?.LastOrDefault()?.DeepClone(),
                ["status"] = sub["status"]?.DeepClone()
            });
        }

        var baselineToCompare = baselineOmitted ? (homeObj?["lastVisit"] as JsonObject) : (explicitBaseline as JsonObject);
        var since = await CompareAsync(baselineToCompare, current, options);

        var prevEventIds = (baselineToCompare?["timelineEventIds"] as JsonArray ?? []).Select(Execution.Text).ToHashSet(StringComparer.Ordinal);
        var deltaEvents = new JsonArray(technicalTimeline.Where(ev => !prevEventIds.Contains(ev["id"]?.GetValue<string>() ?? "")).Select(e => (JsonNode)e.DeepClone()).ToArray());
        var delta = HomeTimeline(deltaEvents, durable);
        if (delta.Count > 0 && since["firstVisit"]?.GetValue<bool>() != true)
        {
            var changesArr = since["changes"] as JsonArray;
            changesArr?.Add((JsonNode)JsonValue.Create($"{delta.Count} project milestone{(delta.Count == 1 ? "" : "s")} recorded")!);
            var detailsArr = since["details"] as JsonArray;
            foreach (var d in delta.OfType<JsonObject>().TakeLast(8))
            {
                detailsArr?.Add((JsonNode)JsonValue.Create(d["summary"]?.GetValue<string>() ?? "")!);
            }
        }

        string progress = completed.Count > 0
            ? string.Join(" ", completed.TakeLast(3).Select(r => r["summary"]?.GetValue<string>() ?? ""))
            : (!string.IsNullOrEmpty(revision["state"]?.GetValue<string>()) ? revision["state"]!.GetValue<string>() : "No progress summary recorded.");

        var blockers = new JsonArray();
        if (mega?["blockers"] is JsonArray mbArr)
        {
            foreach (var b in mbArr) if (b != null) blockers.Add(b.DeepClone());
        }
        var failedOrBlockedSubs = subTasks.Where(s => s["status"]?.GetValue<string>() is "FAILED" or "BLOCKED").ToList();
        foreach (var r in results)
        {
            if (failedOrBlockedSubs.Any(s => s["resultId"]?.GetValue<string>() == r["resultId"]?.GetValue<string>()))
            {
                if (r["blockers"] is JsonArray rbArr)
                {
                    foreach (var b in rbArr) if (b != null) blockers.Add(b.DeepClone());
                }
            }
        }

        var actionsForDetails = mega != null ? new JsonArray() : (current["execution"]?["actions"] as JsonArray ?? new JsonArray());
        var reportsForDetails = mega != null ? new JsonArray() : reports;

        var progressDetails = new JsonObject
        {
            ["jobTitle"] = row?["jobTitle"]?.GetValue<string>() ?? "",
            ["status"] = row?["status"]?.GetValue<string>() ?? "idle",
            ["actions"] = (JsonArray)actionsForDetails.DeepClone(),
            ["reports"] = (JsonArray)reportsForDetails.DeepClone(),
            ["files"] = (local["files"] as JsonArray)?.DeepClone() ?? new JsonArray(),
            ["megaTask"] = mega?.DeepClone() ?? null,
            ["subTasks"] = new JsonArray(subTasks.Select(s => (JsonNode)s.DeepClone()).ToArray()),
            ["results"] = new JsonArray(results.Select(r => (JsonNode)r.DeepClone()).ToArray()),
            ["completed"] = new JsonArray(completed.Select(c => (JsonNode)JsonValue.Create(c["summary"]?.GetValue<string>() ?? "")!).ToArray()),
            ["pending"] = new JsonArray(subTasks.Where(s => s["status"]?.GetValue<string>() != "COMPLETE").Select(s => (JsonNode)s.DeepClone()).ToArray()),
            ["tests"] = new JsonArray(results.SelectMany(r => r["tests"] as JsonArray ?? []).Where(t => t != null).Select(t => (JsonNode)t!.DeepClone()).ToArray()),
            ["blockers"] = blockers,
            ["workers"] = workers,
            ["gitProgress"] = mega?["repoAfter"]?.DeepClone() ?? mega?["repoBefore"]?.DeepClone() ?? null
        };

        var checkpointDiffs = new JsonArray();
        var inspectFn = options?.Inspect;
        foreach (var cp in resumeTargets)
        {
            JsonObject inspectionFacts;
            if (inspectFn != null && !string.IsNullOrEmpty(canonicalLinked))
            {
                inspectionFacts = inspectFn(canonicalLinked);
            }
            else
            {
                inspectionFacts = new JsonObject
                {
                    ["folder"] = canonicalLinked,
                    ["git"] = ProjectWorkspace.SafeGitWorkspace(canonicalLinked) != null
                };
            }
            var currentRepo = Execution.RepoState(canonicalLinked, inspectionFacts);
            int revId = revision["id"]?.GetValue<int>() ?? 0;
            var completedSubIds = new JsonArray(subTasks.Where(s => s["status"]?.GetValue<string>() == "COMPLETE").Select(s => (JsonNode)JsonValue.Create(s["id"]?.GetValue<string>() ?? "")!).ToArray());
            var diffObj = ProjectHistory.CheckpointDiff(cp, currentRepo, revId, completedSubIds);
            var item = new JsonObject { ["checkpointId"] = cp["id"]?.DeepClone() };
            foreach (var kv in diffObj) item[kv.Key] = kv.Value?.DeepClone();
            checkpointDiffs.Add((JsonNode)item);
        }

        var resumeTargetsArray = new JsonArray(resumeTargets.Select(cp => (JsonNode)cp.DeepClone()).ToArray());

        var actionTarget = new JsonObject { ["projectId"] = projectId };
        if (mega != null) actionTarget["megaTaskId"] = mega["id"]?.DeepClone();
        if (action == "resume" && resumeTargets.Count == 1)
        {
            actionTarget["checkpointId"] = resumeTargets[0]["id"]?.DeepClone();
            actionTarget["megaTaskId"] = resumeTargets[0]["megaTaskId"]?.DeepClone();
            if (resumeTargets[0]["researchId"] != null)
                actionTarget["researchId"] = resumeTargets[0]["researchId"]!.DeepClone();
        }
        else if (mega?["detached"] is JsonValue dv && dv.TryGetValue<bool>(out var isDetached) && isDetached)
        {
            var matchingResearch = allResearches.FirstOrDefault(r => r["megaTaskId"]?.GetValue<string>() == megaId);
            if (matchingResearch?["id"] != null)
                actionTarget["researchId"] = matchingResearch["id"]!.DeepClone();
        }

        string nowText = running > 0 ? "Workers running"
            : (mega != null && (megaStatus is "PAUSED" or "FAILED"))
                ? $"{(megaStatus == "FAILED" ? "Failed" : "Paused")} \u2014 checkpoint saved ({subTasks.Count(s => s["status"]?.GetValue<string>() != "COMPLETE")} slices remaining)"
            : megaStatus == "COMPLETE" ? "Completed"
            : megaStatus == "READY_FOR_APPROVAL" ? "Waiting for approval"
            : labelText;

        var megaTitle = mega?["title"]?.GetValue<string>();
        var rowJobTitle = row?["jobTitle"]?.GetValue<string>();
        var finalJobTitle = !string.IsNullOrEmpty(megaTitle) ? megaTitle : (!string.IsNullOrEmpty(rowJobTitle) ? rowJobTitle : "");

        return new JsonObject
        {
            ["projectId"] = projectId,
            ["goal"] = revision["direction"]?.DeepClone(),
            ["progress"] = progress,
            ["progressDetails"] = progressDetails,
            ["timeline"] = technicalTimelineArray.DeepClone(),
            ["timelineSummary"] = timeline,
            ["technicalTimeline"] = technicalTimelineArray,
            ["checkpointDiffs"] = checkpointDiffs,
            ["resumeTargets"] = resumeTargetsArray,
            ["actionTarget"] = actionTarget,
            ["local"] = local,
            ["now"] = nowText,
            ["running"] = running,
            ["jobTitle"] = finalJobTitle,
            ["next"] = next,
            ["action"] = action,
            ["notes"] = (homeObj?["notes"] as JsonArray)?.DeepClone() ?? new JsonArray(),
            ["baseline"] = baselineOmitted ? (homeObj?["lastVisit"]?.DeepClone() ?? null) : explicitBaseline?.DeepClone(),
            ["current"] = current,
            ["since"] = since
        };
    }

    public static Task<JsonObject> GetProjectHomeAsync(
        JsonObject project,
        JsonObject snapshot,
        ProjectHomeOptions? options = null) =>
        ProjectHomeCoreAsync(project, snapshot, baselineOmitted: true, explicitBaseline: null, options);

    public static Task<JsonObject> GetProjectHomeAsync(
        JsonObject project,
        JsonObject snapshot,
        JsonNode? baseline,
        ProjectHomeOptions? options = null) =>
        ProjectHomeCoreAsync(project, snapshot, baselineOmitted: false, explicitBaseline: baseline, options);

    public static JsonObject GetProjectHome(
        JsonObject project,
        JsonObject snapshot,
        ProjectHomeOptions? options = null) =>
        GetProjectHomeAsync(project, snapshot, options).GetAwaiter().GetResult();

    public static JsonObject GetProjectHome(
        JsonObject project,
        JsonObject snapshot,
        JsonNode? baseline,
        ProjectHomeOptions? options = null) =>
        GetProjectHomeAsync(project, snapshot, baseline, options).GetAwaiter().GetResult();

    // JavaScript parity aliases:
    public static Task<JsonObject> projectHome(
        JsonObject project,
        JsonObject snapshot,
        ProjectHomeOptions? options = null) =>
        GetProjectHomeAsync(project, snapshot, options);

    public static Task<JsonObject> projectHome(
        JsonObject project,
        JsonObject snapshot,
        JsonNode? baseline,
        ProjectHomeOptions? options = null) =>
        GetProjectHomeAsync(project, snapshot, baseline, options);

    public static Task<JsonObject> localGit(
        string? folder,
        ProjectHomeOptions? options = null) =>
        LocalGitAsync(folder, options);

    public static Task<JsonObject> compare(
        JsonObject? previous,
        JsonObject? current,
        ProjectHomeOptions? options = null) =>
        CompareAsync(previous, current, options);
}
