// Port of engine/worktrees.cjs.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed class Worktrees
{
    public sealed record GitReply(int? Status, string Stdout = "", string Stderr = "", string? Error = null);
    public sealed record GitCall(string? Folder, IReadOnlyList<string> Args, int? TimeoutMs, int? MaxBytes);
    public delegate GitReply GitRunner(GitCall call);
    public sealed record Part(string Folder, string Branch, string SubId);
    public sealed record MergeResult(bool Ok, string? Branch = null, string[]? Conflicts = null, string? Reason = null);

    readonly GitRunner runner;
    public Worktrees(GitRunner? runner = null) => this.runner = runner ?? RunGit;

    public static GitReply RunGit(GitCall call)
    {
        using var process = new Process();
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true };
        if (call.Folder is not null) start.WorkingDirectory = call.Folder;
        foreach (var arg in call.Args) start.ArgumentList.Add(arg);
        foreach (var pair in ProjectWorkspace.GitEnvironment()) start.Environment[pair.Key] = pair.Value;
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            if (!ProjectWorkspace.GitEnvironment().ContainsKey(key)) start.Environment.Remove(key);
        process.StartInfo = start;
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (call.TimeoutMs is int timeout && !process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return new GitReply(null, Error: "Git command timed out");
            }
            if (call.TimeoutMs is null) process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (call.MaxBytes is int max && Encoding.UTF8.GetByteCount(stdout.Result) + Encoding.UTF8.GetByteCount(stderr.Result) > max)
                return new GitReply(null, Error: "Git command output exceeded limit");
            return new GitReply(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception error) { return new GitReply(null, Error: error.Message); }
    }

    GitReply Probe(string? folder, params string[] args) => runner(new GitCall(folder, args, null, null));

    public string Git(string folder, params string[] args)
    {
        var reply = runner(new GitCall(folder, args, 30000, 8 * 1024 * 1024));
        if (reply.Status != 0) throw new InvalidOperationException((!string.IsNullOrEmpty(reply.Stderr) ? reply.Stderr : reply.Error ?? "Git command failed").Trim());
        return reply.Stdout.Trim();
    }

    public bool Repository(string folder)
    {
        var root = ProjectWorkspace.LinkedWorkspace(folder);
        if (root is null || ProjectWorkspace.SafeGitWorkspace(root) is null) return false;
        try { return ProjectWorkspace.LinkedWorkspace(Git(root, "rev-parse", "--show-toplevel")) == root; }
        catch { return false; }
    }

    public string BranchFor(string workBranch, long index)
    {
        if (index < 1 || index > 9007199254740991 || workBranch is null || !Regex.IsMatch(workBranch, @"\A[-A-Za-z0-9_./]+\z", RegexOptions.ECMAScript))
            throw new InvalidOperationException(Strings.T("Invalid worktree branch"));
        var branch = $"{workBranch}--part-{index}";
        GitBranchName(branch);
        return branch;
    }

    public void GitBranchName(string branch)
    {
        if (Probe(null, "check-ref-format", "--branch", branch).Status != 0)
            throw new InvalidOperationException(Strings.T("Invalid worktree branch"));
    }

    public static string WorktreePath(string dir, string projectId, string megaId, string subId)
    {
        foreach (var id in new[] { projectId, megaId, subId })
            if (id is null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,100}\z", RegexOptions.ECMAScript))
                throw new InvalidOperationException(Strings.T("Invalid worktree identity"));
        return Path.Combine(dir, "worktrees", projectId, megaId, subId);
    }

    public Part? Create(string folder, string dir, string projectId, string megaId, string subId, long index, string workBranch)
    {
        if (!Repository(folder)) return null;
        var branch = BranchFor(workBranch, index);
        var destination = WorktreePath(dir, projectId, megaId, subId);
        Git(folder, "worktree", "prune");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            var records = Git(folder, "worktree", "list", "--porcelain", "-z").Split('\0');
            var registered = records.Any(record => record.StartsWith("worktree ", StringComparison.Ordinal)
                && ProjectWorkspace.LinkedWorkspace(record[9..].Split('\n')[0]) == ProjectWorkspace.LinkedWorkspace(destination));
            if (!registered || Git(destination, "branch", "--show-current") != branch)
                throw new InvalidOperationException(Strings.T("Worktree destination is occupied"));
            return new Part(destination, branch, subId);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var exists = Probe(folder, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}").Status == 0;
        if (exists) Git(folder, "worktree", "add", destination, branch);
        else Git(folder, "worktree", "add", "-b", branch, destination, workBranch);
        return new Part(destination, branch, subId);
    }

    // Planwharf's own work plan (.planwharf/) is rewritten at every step: it never makes a branch dirty.
    public static string WorkChanges(string? status) => string.Join("\n", (status ?? "").Split('\n')
        .Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0 && !ChangedFile(line).StartsWith(".planwharf/", StringComparison.Ordinal)));

    // A line is "XY path" (or "XY old -> new"); the first line may come trimmed, so the path is after the status.
    static string ChangedFile(string line) => Regex.Replace(line.Trim(), @"^\S+\s+", "").Split(" -> ")[^1].TrimStart('"');

    public MergeResult Integrate(string folder, IReadOnlyList<Part> parts)
    {
        try
        {
            if (WorkChanges(Git(folder, "status", "--porcelain")).Length > 0)
                return new MergeResult(false, Conflicts: [], Reason: "Work branch has uncommitted changes; integration is paused.");
        }
        catch (Exception error) { return new MergeResult(false, Conflicts: [], Reason: error.Message); }

        foreach (var part in parts)
        {
            string dirty;
            try { dirty = Git(part.Folder, "status", "--porcelain"); }
            catch (Exception error)
            {
                var reason = Directory.Exists(part.Folder) ? error.Message : $"Part worktree is unavailable: {part.Folder}";
                return new MergeResult(false, part.Branch, [], reason);
            }
            if (WorkChanges(dirty).Length > 0)
                return new MergeResult(false, part.Branch, [], "Part has uncommitted changes; integration is paused.");
        }

        foreach (var part in parts)
        {
            try { Git(folder, "merge", "--no-ff", "--no-edit", part.Branch); }
            catch (Exception error)
            {
                string[] conflicts;
                try
                {
                    conflicts = Git(folder, "diff", "--name-only", "--diff-filter=U")
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                }
                finally
                {
                    if (Probe(folder, "rev-parse", "-q", "--verify", "MERGE_HEAD").Status == 0)
                        Git(folder, "merge", "--abort");
                }
                return new MergeResult(false, part.Branch, conflicts, error.Message);
            }
        }
        return new MergeResult(true);
    }

    public void Cleanup(string folder, string dir, string projectId, string megaId, IReadOnlyList<Part> parts, bool discardBranches = false)
    {
        var root = Path.GetFullPath(Path.Combine(dir, "worktrees", projectId, megaId));
        foreach (var part in parts)
        {
            var destination = Path.GetFullPath(part.Folder);
            GitBranchName(part.Branch);
            if (!ProjectWorkspace.Within(root, destination)
                || destination != WorktreePath(dir, projectId, megaId, part.SubId))
                throw new InvalidOperationException(Strings.T("Worktree cleanup path escaped its work"));
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                var dirty = Git(destination, "status", "--porcelain");
                // Completion keeps uncommitted work for the owner. Drop this work removes it.
                if (dirty.Length > 0 && !discardBranches)
                    throw new InvalidOperationException(Strings.Format("Worktree has recoverable changes: {0}", destination));
                if (discardBranches) Git(folder, "worktree", "remove", "--force", "--force", destination);
                else Git(folder, "worktree", "remove", destination);
            }
            else Git(folder, "worktree", "prune");
            Git(folder, "branch", discardBranches ? "-D" : "-d", part.Branch);
        }
    }

    // A worktree whose work is gone stays on disk after a plain prune. Remove those directories.
    public static string[] SweepOrphans(string dir, IReadOnlyCollection<string>? liveMegaIds)
    {
        var root = Path.Combine(dir, "worktrees");
        if (!Directory.Exists(root)) return [];
        var live = new HashSet<string>(liveMegaIds ?? []);
        var removed = new List<string>();
        foreach (var projectDir in Directory.GetFileSystemEntries(root))
        {
            if (!Directory.Exists(projectDir)) continue;
            foreach (var megaDir in Directory.GetFileSystemEntries(projectDir))
            {
                if (live.Contains(Path.GetFileName(megaDir))) continue;
                if (Directory.Exists(megaDir)) Directory.Delete(megaDir, recursive: true);
                else File.Delete(megaDir);
                removed.Add(Path.Combine(Path.GetFileName(projectDir), Path.GetFileName(megaDir)));
            }
            if (!Directory.EnumerateFileSystemEntries(projectDir).Any()) Directory.Delete(projectDir);
        }
        return [.. removed];
    }
}
