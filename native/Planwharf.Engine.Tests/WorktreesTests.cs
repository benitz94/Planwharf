// Port of engine/worktrees.test.cjs module-level cases.
using System.Diagnostics;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class WorktreesTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw worktrees é ").FullName;
    readonly string folder;
    readonly string data;
    readonly Worktrees worktrees = new();

    public WorktreesTests()
    {
        folder = Path.Combine(root, "project with spaces é");
        data = Path.Combine(root, "data");
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-b", "main");
        Git(folder, "config", "user.name", "Fixture");
        Git(folder, "config", "user.email", "fixture@example.test");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, "base.txt"), "base\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-m", "base");
        Git(folder, "switch", "-c", "feat/work");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    static string Git(string folder, params string[] args)
    {
        var reply = Worktrees.RunGit(new Worktrees.GitCall(folder, args, 30000, 8 * 1024 * 1024));
        Assert.Equal(0, reply.Status);
        return reply.Stdout.Trim();
    }

    Worktrees.Part Part(string id, int index) => worktrees.Create(folder, data, "project-1", "MT-1", id, index, "feat/work")!;
    void Commit(Worktrees.Part part, string name, string text)
    {
        File.WriteAllText(Path.Combine(part.Folder, name), text);
        Git(part.Folder, "add", name);
        Git(part.Folder, "commit", "-m", name);
    }

    [Fact]
    public void ParallelPartsHaveIsolatedRegisteredWorktreesAndMergeInOrder()
    {
        Assert.True(worktrees.Repository(folder), $"linked={ProjectWorkspace.LinkedWorkspace(folder)}, safe={ProjectWorkspace.SafeGitWorkspace(folder)}, top={Git(folder, "rev-parse", "--show-toplevel")}");
        var first = Part("ST-1", 1);
        var second = Part("ST-2", 2);
        Assert.NotEqual(first.Folder, second.Folder);
        Assert.Equal("feat/work--part-1", Git(first.Folder, "branch", "--show-current"));
        Commit(first, "one é.txt", "one\n");
        Assert.False(File.Exists(Path.Combine(second.Folder, "one é.txt")));
        Commit(second, "two.txt", "two\n");
        Assert.True(worktrees.Integrate(folder, [first, second]).Ok);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(folder, "one é.txt")));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(folder, "two.txt")));
        worktrees.Cleanup(folder, data, "project-1", "MT-1", [first, second]);
        Assert.False(Directory.Exists(first.Folder));
        Assert.Empty(Git(folder, "branch", "--list", "feat/work--part-*"));
    }

    [Fact]
    public void ConflictAbortsActiveMergeAndRetainsEarlierMergeAndBranches()
    {
        var first = Part("ST-1", 1);
        var second = Part("ST-2", 2);
        Commit(first, "first.txt", "first\n");
        Commit(second, "first.txt", "second\n");
        var result = worktrees.Integrate(folder, [first, second]);
        Assert.False(result.Ok);
        Assert.Equal(second.Branch, result.Branch);
        Assert.Equal(["first.txt"], result.Conflicts!);
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(folder, "first.txt")));
        Assert.Empty(Git(folder, "status", "--porcelain"));
        Assert.NotEqual(0, Worktrees.RunGit(new Worktrees.GitCall(folder, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], 30000, 1000)).Status);
        Assert.True(Directory.Exists(second.Folder));
    }

    [Fact]
    public void InterruptedTreeIsReusedAndDirtyWorkIsPreserved()
    {
        var first = Part("ST-1", 1);
        Assert.Equal(first, Part("ST-1", 1));
        File.WriteAllText(Path.Combine(first.Folder, "unfinished.txt"), "recover me\n");
        Assert.Contains("recoverable changes", Assert.Throws<InvalidOperationException>(() =>
            worktrees.Cleanup(folder, data, "project-1", "MT-1", [first])).Message);
        Assert.Equal("recover me\n", File.ReadAllText(Path.Combine(first.Folder, "unfinished.txt")));
    }

    [Fact]
    public void ExplicitDropRemovesDirtyUnmergedPart()
    {
        var first = Part("ST-1", 1);
        File.WriteAllText(Path.Combine(first.Folder, "unfinished.txt"), "discard\n");
        worktrees.Cleanup(folder, data, "project-1", "MT-1", [first], discardBranches: true);
        Assert.False(Directory.Exists(first.Folder));
        Assert.Empty(Git(folder, "branch", "--list", first.Branch));
    }

    [Fact]
    public void OccupiedDestinationAndEscapedCleanupAreRejected()
    {
        var occupied = Worktrees.WorktreePath(data, "project-1", "MT-1", "ST-1");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        Assert.Equal("Worktree destination is occupied", Assert.Throws<InvalidOperationException>(() => Part("ST-1", 1)).Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(occupied, "keep.txt")));
        var other = new Worktrees.Part(folder, "feat/work--part-1", "ST-1");
        Assert.Equal("Worktree cleanup path escaped its work", Assert.Throws<InvalidOperationException>(() =>
            worktrees.Cleanup(folder, data, "project-1", "MT-1", [other], discardBranches: true)).Message);
    }

    [Fact]
    public void DirtyOrMissingPartBlocksAllMerges()
    {
        var first = Part("ST-1", 1);
        var second = Part("ST-2", 2);
        Commit(first, "one.txt", "one\n");
        File.WriteAllText(Path.Combine(second.Folder, "unfinished.txt"), "keep\n");
        var dirty = worktrees.Integrate(folder, [first, second]);
        Assert.False(dirty.Ok);
        Assert.Equal(second.Branch, dirty.Branch);
        Assert.False(File.Exists(Path.Combine(folder, "one.txt")));
        Directory.Delete(second.Folder, recursive: true);
        var missing = worktrees.Integrate(folder, [first, second]);
        Assert.StartsWith("Part worktree is unavailable:", missing.Reason);
        Assert.False(File.Exists(Path.Combine(folder, "one.txt")));
    }

    [Fact]
    public void IdentityAndBranchValidationRejectUnsafeInputs()
    {
        Assert.Equal("Invalid worktree identity", Assert.Throws<InvalidOperationException>(() =>
            Worktrees.WorktreePath(data, "project", "../escape", "part")).Message);
        Assert.Equal("Invalid worktree identity", Assert.Throws<InvalidOperationException>(() =>
            Worktrees.WorktreePath(data, "project", "mega", new string('x', 101))).Message);
        Assert.Equal("Invalid worktree branch", Assert.Throws<InvalidOperationException>(() => worktrees.BranchFor("feat work", 1)).Message);
        Assert.Equal("Invalid worktree branch", Assert.Throws<InvalidOperationException>(() => worktrees.BranchFor("feat/work", 0)).Message);
        Assert.Equal("Invalid worktree branch", Assert.Throws<InvalidOperationException>(() => worktrees.BranchFor("feat/work", 9007199254740992)).Message);
        Assert.Equal("Invalid worktree branch", Assert.Throws<InvalidOperationException>(() => worktrees.BranchFor("feat//work", 1)).Message);
    }

    [Fact]
    public void NonGitFolderDoesNotAcquireWorktrees()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Null(worktrees.Create(plain, data, "p", "m", "s", 1, "work"));
        Assert.False(Directory.Exists(Path.Combine(data, "worktrees")));
    }

    [Fact]
    public void DirtyRootBlocksIntegrationBeforePartProbe()
    {
        var first = Part("ST-1", 1);
        Commit(first, "one.txt", "one\n");
        File.WriteAllText(Path.Combine(folder, "unfinished.txt"), "keep\n");
        var result = worktrees.Integrate(folder, [first]);
        Assert.Equal("Work branch has uncommitted changes; integration is paused.", result.Reason);
        Assert.False(File.Exists(Path.Combine(folder, "one.txt")));
    }

    [Fact]
    public void BoundedHelperAndUnboundedProbesAreDistinct()
    {
        var calls = new List<Worktrees.GitCall>();
        var fake = new Worktrees(call =>
        {
            calls.Add(call);
            if (call.Args[0] == "check-ref-format") return new Worktrees.GitReply(0);
            if (call.Args[0] == "show-ref") return new Worktrees.GitReply(null, Error: "simulated hang");
            return new Worktrees.GitReply(0, "  value\n");
        });
        Assert.Equal("value", fake.Git(folder, "status", "--porcelain"));
        Assert.Equal("feat/work--part-1", fake.BranchFor("feat/work", 1));
        Assert.Equal(30000, calls[0].TimeoutMs);
        Assert.Equal(8 * 1024 * 1024, calls[0].MaxBytes);
        Assert.Null(calls[1].TimeoutMs);
        Assert.Null(calls[1].MaxBytes);
        Assert.Equal(["check-ref-format", "--branch", "feat/work--part-1"], calls[1].Args);
    }

    [Fact]
    public void GitErrorsUseStderrThenErrorThenFallback()
    {
        var replies = new Queue<Worktrees.GitReply>([
            new(1, Stderr: "  rejected\n"), new(null, Error: "  simulated timeout "), new(1)]);
        var fake = new Worktrees(_ => replies.Dequeue());
        Assert.Equal("rejected", Assert.Throws<InvalidOperationException>(() => fake.Git(folder, "status")).Message);
        Assert.Equal("simulated timeout", Assert.Throws<InvalidOperationException>(() => fake.Git(folder, "status")).Message);
        Assert.Equal("Git command failed", Assert.Throws<InvalidOperationException>(() => fake.Git(folder, "status")).Message);
    }

    [Fact]
    public void UnboundedMergeHeadProbeFailureDoesNotAbort()
    {
        var calls = new List<Worktrees.GitCall>();
        var part = new Worktrees.Part(folder, "part-1", "ST-1");
        var fake = new Worktrees(call =>
        {
            calls.Add(call);
            if (call.Args[0] == "merge") return new Worktrees.GitReply(1, Stderr: "merge failed");
            if (call.Args[0] == "rev-parse") return new Worktrees.GitReply(null, Error: "simulated hang");
            return new Worktrees.GitReply(0);
        });
        var result = fake.Integrate(folder, [part]);
        Assert.Equal("merge failed", result.Reason);
        Assert.DoesNotContain(calls, call => call.Args.SequenceEqual(["merge", "--abort"]));
        Assert.Null(calls.Single(call => call.Args[0] == "rev-parse").TimeoutMs);
    }

    [Fact]
    public void OrphanSweepOnlyTouchesSuppliedTemporaryRoot()
    {
        var tree = Path.Combine(data, "worktrees", "project");
        Directory.CreateDirectory(Path.Combine(tree, "live"));
        Directory.CreateDirectory(Path.Combine(tree, "old"));
        File.WriteAllText(Path.Combine(tree, "old", "file.txt"), "old");
        Assert.Equal([Path.Combine("project", "old")], Worktrees.SweepOrphans(data, ["live"]));
        Assert.True(Directory.Exists(Path.Combine(tree, "live")));
        Assert.False(Directory.Exists(Path.Combine(tree, "old")));
    }

    [Fact]
    public void FailedUnboundedShowRefProbeCreatesBranchWithBoundedGit()
    {
        var calls = new List<Worktrees.GitCall>();
        var fake = new Worktrees(call =>
        {
            calls.Add(call);
            if (call.Args.SequenceEqual(["rev-parse", "--show-toplevel"])) return new Worktrees.GitReply(0, folder);
            if (call.Args[0] == "show-ref") return new Worktrees.GitReply(null, Error: "simulated hang");
            return new Worktrees.GitReply(0);
        });
        var part = fake.Create(folder, data, "project-1", "MT-1", "ST-1", 1, "feat/work");
        Assert.NotNull(part);
        var showRef = calls.Single(call => call.Args[0] == "show-ref");
        Assert.Null(showRef.TimeoutMs);
        Assert.Null(showRef.MaxBytes);
        var add = calls.Single(call => call.Args.SequenceEqual(["worktree", "add", "-b", "feat/work--part-1", part.Folder, "feat/work"]));
        Assert.Equal(30000, add.TimeoutMs);
        Assert.Equal(8 * 1024 * 1024, add.MaxBytes);
    }

    [Fact]
    public void FailedRootStatusReturnsErrorWithoutMerge()
    {
        var calls = new List<Worktrees.GitCall>();
        var fake = new Worktrees(call =>
        {
            calls.Add(call);
            return new Worktrees.GitReply(1, Stderr: "status failed");
        });
        var result = fake.Integrate(folder, [new Worktrees.Part(folder, "part", "ST-1")]);
        Assert.False(result.Ok);
        Assert.Equal("status failed", result.Reason);
        Assert.Equal(["status", "--porcelain"], Assert.Single(calls).Args);
    }

    [Fact]
    public void Planwharfs_own_work_plan_is_not_a_change_that_pauses_integration()
    {
        Assert.Equal("?? src/a.cs", Worktrees.WorkChanges("M .planwharf/plan.md\n?? src/a.cs"));
        Assert.Equal("", Worktrees.WorkChanges(" M .planwharf/plan.md\r\n?? \".planwharf/x y\""));
        Assert.Equal(" M src/b.cs", Worktrees.WorkChanges(" M src/b.cs"));
        Assert.Equal("", Worktrees.WorkChanges("R  old.md -> .planwharf/plan.md"));
    }
}
