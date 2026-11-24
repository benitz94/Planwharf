using Planwharf.Engine;
using System.Diagnostics;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PlanFileTests
{
    sealed class FakeGit(Func<string, PlanGitResult> reply) : IPlanGit
    {
        public List<string> Calls { get; } = [];
        public PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000)
        {
            var command = string.Join(" ", args);
            Calls.Add(command);
            return reply(command);
        }
    }
    static string Folder() => Directory.CreateTempSubdirectory("pw-plan-test-").FullName;
    static void Remove(string folder)
    {
        Assert.StartsWith(Path.GetTempPath(), folder, StringComparison.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Render_and_parse_preserve_exact_sections_and_escape_headings()
    {
        var rendered = PlanFile.Render(new("Fix\r\n## Steps\r\nnext", "origin/main abcdef0", "fix/test", [new(Role: "Build", Model: "Grok")], "summary"));
        Assert.Contains("Fix\n\\## Steps\nnext", rendered);
        Assert.Equal("Fix\n\\## Steps\nnext", PlanFile.Parse(rendered).Goal);
        Assert.Equal("origin/main abcdef0", PlanFile.Parse(rendered.Replace("\n", "\r\n")).Base);
        Assert.Equal(PlanFile.Rules, PlanFile.Parse(rendered).Rules);
    }

    [Fact]
    public void Tick_step_uses_title_then_role_then_first_unchecked_and_preserves_duplicate_order()
    {
        var folder = Folder();
        try
        {
            var file = new PlanFile();
            file.WritePlan(folder, new("goal", Steps: [new(Role: "Build", Model: "Grok", Title: "Part One"), new(Role: "Build", Model: "Grok", Title: "Part Two")]));
            Assert.True(file.TickStep(folder, new(Title: "Part Two", Commit: "sha-two")));
            Assert.True(file.TickStep(folder, new(Role: "Unknown", Commit: "sha-one")));
            Assert.False(file.TickStep(folder, new(Commit: "third")));
            Assert.Contains("Part Two — sha-two", PlanFile.Read(folder));
            Assert.Contains("Part One — sha-one", PlanFile.Read(folder));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void Rewriting_same_goal_keeps_completed_step_and_new_goal_archives_collision_free()
    {
        var folder = Folder();
        try
        {
            var file = new PlanFile(clock: () => new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero));
            var steps = new[] { new PlanStep(Role: "Build", Model: "Grok") };
            file.WritePlan(folder, new("same goal", Steps: steps));
            file.TickStep(folder, new(Role: "Build", Model: "Grok", Commit: "abc"));
            file.WritePlan(folder, new("same goal", Steps: steps));
            Assert.Contains("— abc", PlanFile.Read(folder));
            file.WritePlan(folder, new("different goal", Steps: steps));
            Assert.Single(Directory.GetFiles(Path.Combine(folder, ".planwharf", "done")));
            file.WritePlan(folder, new("same goal", Steps: steps));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, ".planwharf", "done")).Length);
        }
        finally { Remove(folder); }
    }

    [Theory]
    [InlineData("Build on `feat/gate` from refreshed `origin/main`", "feat/gate")]
    [InlineData("branch creation", null)]
    [InlineData("on branch develop", "develop")]
    public void Named_branch_follows_source_patterns(string goal, string? expected) => Assert.Equal(expected, PlanFile.NamedBranch(goal));

    [Fact]
    public void Pure_messages_and_pull_request_title_match_source()
    {
        Assert.Equal("planwharf/fix-login", PlanFile.BranchName("Fix login", null));
        Assert.Equal("Commit or stash the changes in the folder first: a, b, then press Let the crew do it again.", PlanFile.DirtyMessage([" M a", "?? b"]));
        Assert.True(PlanFile.IsNoise("?? cache/__pycache__/file.pyc"));
        Assert.Equal("Implement job 26 C# gate", PlanFile.PullRequestTitle("Implement job 26 on `feat/gate` from refreshed `origin/main`", ["Implement job 26 C# gate"]));
    }

    [Fact]
    public void Finish_archives_done_plan_and_reports_push_truthfully()
    {
        var folder = Folder();
        try
        {
            var file = new PlanFile();
            file.WritePlan(folder, new("finish goal", Steps: [new(Role: "Build", Model: "Grok")]));
            file.TickStep(folder, new(Role: "Build", Model: "Grok", Commit: "abc"));
            Assert.True(file.Finish(folder, "done", pushed: false));
            Assert.Contains("Pushed: no", PlanFile.Read(folder));
            Assert.Contains("Summary: done", PlanFile.Read(folder));
            Assert.Single(Directory.GetFiles(Path.Combine(folder, ".planwharf", "done")));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void Crew_section_preserves_takeover_order_and_distinct_labels()
    {
        var row = new PlanFile.CrewRow("Grok 4.7", SubTasks: [new("a", "Port quota", "gem"), new("v", "Verify the work independently against the job", "astra")], Results: [new("a", "opus"), new("a", "gem"), new("v", "astra")]);
        var models = new[] { new PlanFile.CrewModel("opus", "Opus 5.5"), new("gem", "Gemini 3.8"), new("astra", "GPT-6 Astra") };
        Assert.Equal("## Crew\n\n- Lead: Grok 4.7\n- Build: Opus 5.5 → Gemini 3.8 (Port quota)\n- Verify: GPT-6 Astra", PlanFile.CrewSection(row, models));
    }

    static string Git(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(" ", args)}: {error}");
        return output.Trim();
    }

    [Fact]
    public void Prepare_non_git_and_offline_repo_use_isolated_folders()
    {
        var folder = Folder();
        try
        {
            var file = new PlanFile();
            var plain = file.Prepare(folder, new("plain work"));
            Assert.True(plain.Ok);
            Assert.False(plain.Git);
            Assert.Contains("Not a git repository.", PlanFile.Read(folder));
            Directory.Delete(Path.Combine(folder, ".planwharf"), true);
            Git(folder, "init", "-q", "-b", "main");
            Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-qm", "initial");
            var prepared = file.Prepare(folder, new("offline work"));
            Assert.True(prepared.Ok);
            Assert.True(prepared.Git);
            Assert.True(prepared.Offline);
            Assert.StartsWith("Offline: the remote could not be read", prepared.Base);
            Assert.Equal("planwharf/offline-work", Git(folder, "branch", "--show-current"));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void Prepare_refreshed_default_and_existing_branch_preserve_earlier_base()
    {
        var folder = Folder();
        var remote = Folder();
        try
        {
            Git(remote, "init", "--bare", "-q");
            Git(folder, "init", "-q", "-b", "main");
            Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-qm", "initial");
            Git(folder, "remote", "add", "origin", remote);
            Git(remote, "fetch", "-q", folder, "main:refs/heads/main");
            Git(remote, "symbolic-ref", "HEAD", "refs/heads/main");
            var file = new PlanFile();
            var first = file.Prepare(folder, new("build on `feat/test`"));
            Assert.True(first.Ok);
            Assert.False(first.Offline);
            Assert.Equal("feat/test", first.Branch);
            var baseBefore = first.Base;
            Git(folder, "switch", "-q", "main");
            Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-qm", "advanced");
            Git(remote, "fetch", "-q", folder, "main:refs/heads/main");
            var resumed = file.Prepare(folder, new("build on `feat/test`"));
            Assert.True(resumed.Ok);
            Assert.Equal(baseBefore, resumed.Base);
            Assert.Equal("feat/test", Git(folder, "branch", "--show-current"));
        }
        finally { Remove(folder); Remove(remote); }
    }

    [Fact]
    public void Managed_exclude_toggle_keeps_personal_rules_and_does_not_duplicate_markers()
    {
        var folder = Folder();
        try
        {
            Git(folder, "init", "-q", "-b", "main");
            var exclude = Path.Combine(folder, ".git", "info", "exclude");
            File.WriteAllText(exclude, "*.log\n");
            var file = new PlanFile();
            file.EnsureExclude(folder);
            var once = File.ReadAllText(exclude);
            Assert.Contains("# Planwharf managed work plan\n/.planwharf/\n", once);
            file.EnsureExclude(folder);
            Assert.Equal(once, File.ReadAllText(exclude));
            file.EnsureExclude(folder, keepWorkPlan: true);
            Assert.Equal("*.log\n", File.ReadAllText(exclude));
            File.WriteAllText(exclude, "*.log\n/.planwharf/\n");
            file.EnsureExclude(folder, keepWorkPlan: true);
            Assert.Equal("*.log\n/.planwharf/\n", File.ReadAllText(exclude));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void New_goal_sets_aside_dirty_work_but_same_goal_keeps_it()
    {
        var folder = Folder();
        try
        {
            Git(folder, "init", "-q", "-b", "main");
            Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-qm", "initial");
            var file = new PlanFile();
            Assert.True(file.Prepare(folder, new("same goal")).Ok);
            File.WriteAllText(Path.Combine(folder, "draft.txt"), "personal work");
            Assert.True(file.Prepare(folder, new("same goal")).Ok);
            Assert.True(File.Exists(Path.Combine(folder, "draft.txt")));
            var other = file.Prepare(folder, new("other goal"));
            Assert.True(other.Ok);
            Assert.Contains("Set aside uncommitted changes", other.Chat);
            Assert.False(File.Exists(Path.Combine(folder, "draft.txt")));
            Assert.Contains("Planwharf set these aside", Git(folder, "stash", "list"));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void Commit_note_requires_clean_tree_and_commit_after_recorded_base()
    {
        var folder = Folder();
        try
        {
            Git(folder, "init", "-q", "-b", "main");
            Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-qm", "initial");
            var baseSha = Git(folder, "rev-parse", "HEAD");
            var file = new PlanFile();
            file.EnsureExclude(folder);
            file.WritePlan(folder, new("commit goal", "origin/main " + baseSha[..7], "main"));
            Assert.Equal("not committed", file.CommitNote(folder));
            Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-qm", "work");
            Assert.Equal(Git(folder, "rev-parse", "--short=7", "HEAD"), file.CommitNote(folder));
            File.WriteAllText(Path.Combine(folder, "draft.txt"), "draft");
            Assert.Equal("not committed", file.CommitNote(folder));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void Prepare_refuses_missing_remote_default_without_switching()
    {
        var folder = Folder();
        try
        {
            var git = new FakeGit(command => command switch
            {
                "rev-parse --is-inside-work-tree" => new(true, "true\n"),
                "status --porcelain" => new(true),
                "fetch origin" => new(true),
                _ => new(false)
            });
            var result = new PlanFile(git).Prepare(folder, new("work"));
            Assert.False(result.Ok);
            Assert.Equal("The remote has no default branch. Set origin/HEAD, then press Let the crew do it again.", result.Reason);
            Assert.DoesNotContain(git.Calls, command => command.StartsWith("switch ", StringComparison.Ordinal));
        }
        finally { Remove(folder); }
    }

    [Fact]
    public void Prepare_reports_stash_failure_and_preserves_files()
    {
        var folder = Folder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "draft.txt"), "personal");
            var git = new FakeGit(command => command switch
            {
                "rev-parse --is-inside-work-tree" => new(true, "true\n"),
                "status --porcelain" => new(true, "?? draft.txt\n"),
                "branch --show-current" => new(true, "main\n"),
                _ => new(false)
            });
            var result = new PlanFile(git).Prepare(folder, new("other goal"));
            Assert.False(result.Ok);
            Assert.Contains("Commit or stash the changes", result.Reason);
            Assert.Contains(git.Calls, command => command.StartsWith("stash push -u", StringComparison.Ordinal));
            Assert.DoesNotContain(git.Calls, command => command.StartsWith("switch ", StringComparison.Ordinal));
            Assert.Equal("personal", File.ReadAllText(Path.Combine(folder, "draft.txt")));
        }
        finally { Remove(folder); }
    }
}
