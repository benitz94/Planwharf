// Ports the project-workspace checks from engine/worker-containment.test.cjs and
// engine/worktrees.test.cjs, plus the sheet's temporary-directory cases.
//
// Deferred cases (not implemented):
// - attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// - createServer
// - createOrchestrator
// - spawnWorker
// - runner.cjs
// - HTTP loops in task-completion.test.cjs
// - grill-chat.test.cjs
// - project-export.test.cjs
// - project-teams.test.cjs
// - start-over.test.cjs (archiveProject)
// - worker-containment.test.cjs (launch artifacts and provider cwd)
// - Live npm test
// - live node --test
// - live taskkill
// - Byte-identical randomUUID values across JavaScript and C#
// - gitEnvironment and within as parity JSON
// - browser root / window / AMD wrappers
// - models.json
// - provider-catalog
// - A second process appending to invocations.jsonl at the same time
// - Reading the checkout's plan.json or .git
// - worktrees.test.cjs merge, conflict, cleanup, and the other worktrees.cjs cases
//
// setPlace dropping home on schema 2 is plan.js, not this module. This port does not add setPlace.
// Assumption: a null Cwd or Job.Folder means the JavaScript property was omitted.
using System.Diagnostics;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ProjectWorkspaceTests : IDisposable
{
    const string NeedLinked = "A real linked project folder is required before worker launch.";
    const string StayInside = "Worker launch folder must stay inside the linked project folder.";
    const string CwdMatch = "Worker cwd and job folder must match the linked project folder.";

    readonly string home = Directory.CreateTempSubdirectory("pw workspace é ").FullName;
    public void Dispose() => DeleteTree(home);

    static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var child in new DirectoryInfo(path).GetFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (child is DirectoryInfo) DeleteDirectoryLink(child.FullName);
                else File.Delete(child.FullName);
            }
            else if (child is DirectoryInfo) DeleteTree(child.FullName);
            else
            {
                child.Attributes = FileAttributes.Normal;
                child.Delete();
            }
        }
        var directory = new DirectoryInfo(path) { Attributes = FileAttributes.Normal };
        directory.Delete();
    }

    // Linux rmdir on a directory symlink reports ENOENT (DirectoryNotFoundException), including
    // when the temp path has an accent and the runner locale is not UTF-8. Unlink it as a file.
    static void DeleteDirectoryLink(string path)
    {
        try { Directory.Delete(path); }
        catch (DirectoryNotFoundException)
        {
            try { File.Delete(path); }
            catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { }
        }
    }

    string Dir(string name)
    {
        var path = Path.Combine(home, name);
        Directory.CreateDirectory(path);
        return path;
    }

    static void LinkDirectory(string link, string target)
    {
        Links.Folder(link, target);
    }

    static void Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var env = ProjectWorkspace.GitEnvironment();
        start.Environment.Clear();
        foreach (var pair in env) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, string.Join(' ', args) + ": " + stderr.Result + stdout.Result);
    }

    [Fact]
    public void Linked_workspace_requires_a_real_absolute_directory_and_resolves_links_before_dotdot()
    {
        Assert.Null(ProjectWorkspace.LinkedWorkspace(null));
        Assert.Null(ProjectWorkspace.LinkedWorkspace(""));
        Assert.Null(ProjectWorkspace.LinkedWorkspace("   "));
        Assert.Null(ProjectWorkspace.LinkedWorkspace("relative"));
        Assert.Null(ProjectWorkspace.LinkedWorkspace(Path.Combine(home, "missing")));
        var notes = Path.Combine(home, "notes.txt");
        File.WriteAllText(notes, "x");
        Assert.Null(ProjectWorkspace.LinkedWorkspace(notes));
        var alpha = Dir("alpha");
        var linked = ProjectWorkspace.LinkedWorkspace(alpha);
        Assert.Equal(linked, ProjectWorkspace.LinkedWorkspace(alpha));
        Assert.NotNull(linked);
        Assert.True(Directory.Exists(linked));
        var beta = Dir("beta");
        var alias = Path.Combine(home, "alias");
        LinkDirectory(alias, alpha);
        Assert.Equal(linked, ProjectWorkspace.LinkedWorkspace(alias));
        var escape = Path.Combine(alpha, "escape");
        LinkDirectory(escape, beta);
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(home), ProjectWorkspace.LinkedWorkspace(Path.Combine(escape, "..")));
        Assert.NotEqual(linked, ProjectWorkspace.LinkedWorkspace(escape));
    }

    [Fact]
    public void Cleanup_removes_a_directory_link_in_the_accented_temp_folder_and_keeps_the_target()
    {
        var alpha = Dir("alpha");
        File.WriteAllText(Path.Combine(alpha, "keep.txt"), "keep");
        var alias = Path.Combine(home, "alias");
        LinkDirectory(alias, alpha);
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(alpha), ProjectWorkspace.LinkedWorkspace(alias));
        DeleteDirectoryLink(alias);
        Assert.False(Directory.Exists(alias));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(alpha, "keep.txt")));
    }

    [Fact]
    public void Within_accepts_the_folder_and_a_child_and_rejects_a_sibling()
    {
        var project = Dir("proj");
        var hidden = Dir("hidden");
        Assert.True(ProjectWorkspace.Within(project, project));
        Assert.True(ProjectWorkspace.Within(project, Path.Combine(project, "src", "file")));
        var relative = Path.GetRelativePath(project, hidden);
        Assert.Equal(".." + Path.DirectorySeparatorChar + "hidden", relative);
        Assert.False(ProjectWorkspace.Within(project, hidden));
        Assert.Equal("..", Path.GetRelativePath(project, home));
        Assert.False(ProjectWorkspace.Within(project, home));
        if (OperatingSystem.IsWindows()) Assert.False(ProjectWorkspace.Within(@"C:\proj", @"D:\hidden"));
    }

    [Fact]
    public void Git_environment_drops_git_keys_and_keeps_the_rest_of_the_dictionary()
    {
        var env = ProjectWorkspace.GitEnvironment(new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["KEEP"] = "yes",
            ["NOT_GIT"] = "stay",
            ["GIT_DIR"] = "/secret",
            ["Git_Config_Count"] = "1",
            ["git_author_name"] = "no",
            ["GIT_CONFIG_NOSYSTEM"] = "0"
        });
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("yes", env["KEEP"]);
        Assert.Equal("stay", env["NOT_GIT"]);
        Assert.Equal("1", env["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", env["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.False(env.ContainsKey("GIT_DIR"));
        Assert.False(env.ContainsKey("Git_Config_Count"));
        Assert.False(env.ContainsKey("git_author_name"));
        Assert.Equal(6, env.Count);
    }

    [Fact]
    public void Implementation_launch_rejects_missing_real_linked_folder_before_creating_launch_artifacts()
    {
        var alpha = Dir("alpha");
        var missing = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new() { Folder = alpha, Job = new() { Kind = "implementation" } }));
        Assert.Equal(NeedLinked, missing.Message);
        var absent = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = Path.Combine(home, "missing"),
            Folder = alpha,
            Job = new() { Kind = "audit" }
        }));
        Assert.Equal(NeedLinked, absent.Message);
        Assert.False(Directory.Exists(Path.Combine(home, "logs")));
    }

    [Fact]
    public void Worker_launch_rejects_sibling_traversal_and_symlink_destinations_before_artifacts()
    {
        var alpha = Dir("alpha");
        var beta = Dir("beta");
        var link = Path.Combine(alpha, "outside");
        LinkDirectory(link, beta);
        foreach (var folder in new[] { beta, Path.Combine(alpha, "..", "beta"), link })
        {
            var error = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
            {
                LinkedFolder = alpha,
                Folder = folder,
                ResultPath = Path.Combine(home, "results", "x"),
                Job = new() { Kind = "audit" }
            }));
            Assert.Equal(StayInside, error.Message);
        }
        var file = Path.Combine(alpha, "notes.txt");
        File.WriteAllText(file, "x");
        var asFile = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = file,
            Job = new() { Kind = "audit" }
        }));
        Assert.Equal(StayInside, asFile.Message);
        var child = Dir(Path.Combine("alpha", "src"));
        var nested = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = child,
            Job = new() { Kind = "audit" }
        }));
        Assert.Equal(StayInside, nested.Message);
    }

    [Fact]
    public void Explicit_launch_cwd_and_job_folder_cannot_select_a_different_directory()
    {
        var alpha = Dir("alpha");
        var beta = Dir("beta");
        var cwd = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = alpha,
            Cwd = beta,
            Job = new() { Kind = "audit" }
        }));
        Assert.Equal(CwdMatch, cwd.Message);
        var jobFolder = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = alpha,
            Job = new() { Kind = "audit", Folder = beta }
        }));
        Assert.Equal(CwdMatch, jobFolder.Message);
        var replaced = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = beta,
            Job = new() { Kind = "audit" }
        }));
        Assert.Equal(StayInside, replaced.Message);
        var empty = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = alpha,
            Cwd = ""
        }));
        Assert.Equal(CwdMatch, empty.Message);
        var matched = ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = alpha,
            Folder = alpha,
            Cwd = alpha,
            Job = new() { Kind = "audit", Folder = alpha }
        });
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(alpha), matched.Folder);
        Assert.Equal(matched.Folder, matched.LinkedFolder);
    }

    [Fact]
    public void Canonical_linked_directory_survives_and_research_keeps_its_isolated_directory()
    {
        var alpha = Dir("alpha");
        var audit = ProjectWorkspace.RequireWorkerWorkspace(new() { LinkedFolder = alpha, Folder = alpha, Job = new() { Kind = "audit" } });
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(alpha), audit.Folder);
        Assert.Equal(audit.Folder, audit.LinkedFolder);
        var onJob = ProjectWorkspace.RequireWorkerWorkspace(new() { Folder = alpha, Job = new() { Kind = "research" } });
        Assert.Equal(audit.Folder, onJob.Folder);
        Assert.Equal("", onJob.LinkedFolder);
        var onInfo = ProjectWorkspace.RequireWorkerWorkspace(new() { Folder = alpha, Kind = "research", LinkedFolder = Dir("ignored") });
        Assert.Equal("", onInfo.LinkedFolder);
        var blankKind = ProjectWorkspace.RequireWorkerWorkspace(new() { Folder = alpha, Kind = "research", Job = new() { Kind = "" } });
        Assert.Equal("", blankKind.LinkedFolder);
        var cwd = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            Folder = alpha,
            Kind = "research",
            Cwd = Dir("elsewhere")
        }));
        Assert.Equal(CwdMatch, cwd.Message);
        var jobFolder = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            Folder = alpha,
            Job = new() { Kind = "research", Folder = Dir("other-job") }
        }));
        Assert.Equal(CwdMatch, jobFolder.Message);
    }

    [Fact]
    public void Safe_git_workspace_rejects_redirected_metadata_and_accepts_an_ordinary_directory()
    {
        const string cleanConfig = "[core]\n\tbare = false\n";
        var clean = Metadata("clean", cleanConfig);
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(clean), ProjectWorkspace.SafeGitWorkspace(clean));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(Dir("empty")));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(null));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(Path.Combine(home, "missing")));

        var gitdir = Dir("gitdir");
        File.WriteAllText(Path.Combine(gitdir, ".git"), "gitdir: " + Path.Combine(clean, ".git") + "\n");
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(gitdir));

        var elsewhere = Dir("elsewhere-meta");
        File.WriteAllText(Path.Combine(elsewhere, "config"), cleanConfig);
        var linkedMeta = Dir("linked-meta");
        LinkDirectory(Path.Combine(linkedMeta, ".git"), elsewhere);
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(linkedMeta));

        var escaped = Metadata("escaped", cleanConfig);
        LinkDirectory(Path.Combine(escaped, ".git", "leave"), Dir("outside-meta"));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(escaped));

        var common = Metadata("common", cleanConfig);
        File.WriteAllText(Path.Combine(common, ".git", "commondir"), "../other\n");
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(common));

        var alternates = Metadata("alternates", cleanConfig);
        Directory.CreateDirectory(Path.Combine(alternates, ".git", "objects", "info"));
        File.WriteAllText(Path.Combine(alternates, ".git", "objects", "info", "alternates"), "/other/objects\n");
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(alternates));

        var http = Metadata("http", cleanConfig);
        Directory.CreateDirectory(Path.Combine(http, ".git", "objects", "info"));
        File.WriteAllText(Path.Combine(http, ".git", "objects", "info", "http-alternates"), "/other/objects\n");
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(http));

        Assert.Null(ProjectWorkspace.SafeGitWorkspace(Metadata("include", "[include]\n\tpath = ../other\n")));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(Metadata("include-if", "[includeIf \"gitdir:../x/\"]\n\tpath = ../other\n")));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(Metadata("worktree-line", "[core]\n\tworktree = /other\n")));
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(Metadata("fsmonitor", "[core]\n\tfsmonitor = watch\n")));
    }

    [Fact]
    public void Parallel_parts_have_isolated_registered_worktrees_and_merge_in_order()
    {
        var repo = Dir(Path.Combine("project with spaces é", "repo"));
        Git(repo, "init", "-b", "main");
        Git(repo, "config", "user.name", "Fixture");
        Git(repo, "config", "user.email", "fixture@example.test");
        Git(repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "base");
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(repo), ProjectWorkspace.SafeGitWorkspace(repo));

        const string projectId = "project-1", mega = "MT-1", sub = "ST-1";
        var resultPath = Path.Combine(home, "data", "results", "attempt.json");
        var first = Path.Combine(home, "data", "worktrees", projectId, mega, sub);
        var second = Path.Combine(home, "data", "worktrees", projectId, mega, "ST-2");
        Git(repo, "worktree", "add", "-b", "feat/work--part-1", first, "HEAD");
        Git(repo, "worktree", "add", "-b", "feat/work--part-2", second, "HEAD");
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(repo), ProjectWorkspace.SafeGitWorkspace(repo));

        var accepted = ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, first, sub, resultPath));
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(first), accepted.Folder);
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(repo), accepted.LinkedFolder);
        var viaResult = ProjectWorkspace.RequireWorkerWorkspace(new()
        {
            LinkedFolder = repo,
            Folder = first,
            ProjectId = projectId,
            MegaTaskId = mega,
            SubTaskId = sub,
            Result = resultPath
        });
        Assert.Equal(accepted.Folder, viaResult.Folder);

        var mismatch = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, second, sub, resultPath)));
        Assert.Equal(StayInside, mismatch.Message);
        var space = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, first, "ST 1", resultPath)));
        Assert.Equal(StayInside, space.Message);
        var relative = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, first, sub, "results/attempt.json")));
        Assert.Equal(StayInside, relative.Message);

        var longId = new string('a', 100);
        var longFolder = Path.Combine(home, "data", "worktrees", projectId, mega, longId);
        Git(repo, "worktree", "add", "-b", "feat/long", longFolder, "HEAD");
        var longAccepted = ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, longFolder, longId, resultPath));
        Assert.Equal(ProjectWorkspace.LinkedWorkspace(longFolder), longAccepted.Folder);
        var tooLong = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, longFolder, new string('a', 101), resultPath)));
        Assert.Equal(StayInside, tooLong.Message);

        File.AppendAllText(Path.Combine(repo, ".git", "config"), "\n[include]\n\tpath = ../other\n");
        Assert.Null(ProjectWorkspace.SafeGitWorkspace(repo));
        var spoiled = Assert.Throws<InvalidOperationException>(() => ProjectWorkspace.RequireWorkerWorkspace(Launch(repo, first, sub, resultPath)));
        Assert.Equal(StayInside, spoiled.Message);
    }

    ProjectWorkspace.WorkerLaunch Launch(string repo, string folder, string subTaskId, string resultPath) => new()
    {
        LinkedFolder = repo,
        Folder = folder,
        ProjectId = "project-1",
        MegaTaskId = "MT-1",
        SubTaskId = subTaskId,
        ResultPath = resultPath
    };

    string Metadata(string name, string config)
    {
        var folder = Dir(name);
        var git = Dir(Path.Combine(name, ".git"));
        File.WriteAllText(Path.Combine(git, "config"), config);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        return folder;
    }
}
