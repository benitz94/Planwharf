// Port of engine/pr-fix.test.cjs and the PrCheck cases in engine/pr-fix-verify.test.cjs.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PrCheckTests
{
    sealed class Fake
    {
        public readonly List<(string Folder, string Command, string[] Args)> Calls = [];
        public readonly Queue<PrCommandResult> Replies = new();
        public void Add(int status = 0, string stdout = "", string stderr = "") => Replies.Enqueue(new(status, stdout, stderr));
        public Task<PrCommandResult> Run(string folder, string command, string[] args)
        {
            Calls.Add((folder, command, args));
            Assert.NotEmpty(Replies);
            return Task.FromResult(Replies.Dequeue());
        }
        public void Exhausted() => Assert.Empty(Replies);
        public string[] At(int index) => Calls[index].Args;
    }
    static string? Text(JsonObject result, string key) => result[key]?.ToString();
    static JsonObject Fix(string branch = "feat/x", string before = "a", params string[] conflicts)
    {
        var array = new JsonArray(); foreach (var conflict in conflicts) array.Add((JsonNode?)JsonValue.Create(conflict));
        return new() { ["branch"] = branch, ["before"] = before, ["conflicts"] = array };
    }

    [Fact]
    public async Task Lists_failed_checks_and_excludes_cancelled_or_unstarted_runs()
    {
        var fake = new Fake();
        fake.Add(stdout: "[{\"number\":4,\"title\":\"fix\",\"headRefName\":\"feat/x\",\"url\":\"u\",\"mergeable\":\"CONFLICTING\",\"statusCheckRollup\":[{\"name\":\"build\",\"conclusion\":\"FAILURE\"},{\"context\":\"old\",\"state\":\"ERROR\"},{\"name\":\"cancelled\",\"conclusion\":\"CANCELLED\"}]}]");
        var result = await new PrCheck(fake.Run).ListPrs("folder");
        Assert.Equal("true", Text(result, "ok"));
        var pr = (JsonObject)((JsonArray)result["prs"]!)[0]!;
        Assert.Equal("true", Text(pr, "conflicts"));
        Assert.Equal("[\"build\",\"old\"]", pr["failing"]!.ToJsonString());
        Assert.Equal(["pr", "list", "--state", "open", "--json", "number,title,mergeable,headRefName,statusCheckRollup,url"], fake.At(0));
        fake.Exhausted();
    }

    [Theory]
    [InlineData(1, "", "not found", "not found")]
    [InlineData(0, "oops", "", "not readable")]
    [InlineData(0, "{}", "", "not readable")]
    public async Task Bad_list_reply_is_reported(int status, string stdout, string stderr, string fragment)
    {
        var fake = new Fake(); fake.Add(status, stdout, stderr);
        Assert.Contains(fragment, Text(await new PrCheck(fake.Run).ListPrs("folder"), "reason"));
        fake.Exhausted();
    }

    [Fact]
    public async Task Prepare_rejects_closed_or_unusual_branch_before_fetch()
    {
        foreach (var json in new[] { "{\"state\":\"CLOSED\",\"headRefName\":\"feat/x\"}", "{\"state\":\"OPEN\",\"headRefName\":\"-bad\"}" })
        {
            var fake = new Fake(); fake.Add(stdout: json);
            var result = await new PrCheck(fake.Run).PrepareFix("folder", 9, "unused");
            Assert.Equal("false", Text(result, "ok")); Assert.Single(fake.Calls);
            fake.Exhausted();
        }
    }

    [Fact]
    public async Task Prepare_fetches_then_adds_detached_copy_and_preserves_conflicts()
    {
        var fake = new Fake();
        fake.Add(stdout: "{\"state\":\"OPEN\",\"headRefName\":\"feat/x\",\"baseRefName\":\"main\",\"statusCheckRollup\":[{\"name\":\"build\",\"conclusion\":\"FAILURE\"},{\"name\":\"queue\",\"conclusion\":\"STARTUP_FAILURE\",\"detailsUrl\":\"https://x/actions/runs/42/job/1\"}]}");
        fake.Add(); fake.Add(); fake.Add(stdout: "abc\n"); fake.Add(1); fake.Add(stdout: "notes.md\n");
        var tree = Path.Combine(Path.GetTempPath(), "pw-pr-" + Guid.NewGuid().ToString("N"), "copy");
        try
        {
            var result = await new PrCheck(fake.Run).PrepareFix("folder", 9, tree);
            Assert.Equal("true", Text(result, "ok"));
            Assert.Equal("[\"notes.md\"]", result["conflicts"]!.ToJsonString());
            Assert.Equal("[\"build\"]", result["failing"]!.ToJsonString());
            Assert.Equal("42", ((JsonObject)((JsonArray)result["notRun"]!)[0]!)["runId"]!.ToString());
            Assert.Equal(["fetch", "origin", "+refs/heads/feat/x:refs/remotes/origin/feat/x", "+refs/heads/main:refs/remotes/origin/main"], fake.At(1));
            Assert.Equal(["worktree", "add", "--detach", tree, "origin/feat/x"], fake.At(2));
            Assert.Equal(tree, fake.Calls[3].Folder);
            fake.Exhausted();
        }
        finally { Directory.Delete(Path.GetDirectoryName(tree)!, true); }
    }

    [Fact]
    public async Task Merge_failure_without_conflict_removes_copy_and_reports_failure()
    {
        var fake = new Fake(); fake.Add(); fake.Add(); fake.Add(stdout: "a"); fake.Add(1); fake.Add(); fake.Add(1); fake.Add();
        var tree = Path.Combine(Path.GetTempPath(), "pw-pr-" + Guid.NewGuid().ToString("N"), "copy");
        try
        {
            var result = await new PrCheck(fake.Run).PrepareWorktree("folder", "feat/x", "main", [], tree);
            Assert.Equal("The base branch could not be merged.", Text(result, "reason"));
            Assert.Equal(["worktree", "remove", "--force", tree], fake.At(5));
            Assert.Equal(["worktree", "prune"], fake.At(6)); fake.Exhausted();
        }
        finally { Directory.Delete(Path.GetDirectoryName(tree)!, true); }
    }

    [Fact]
    public async Task Unresolved_files_and_markers_stop_before_commit_or_push()
    {
        var fake = new Fake(); fake.Add(stdout: "notes.md\n");
        var result = await new PrCheck(fake.Run).FinishFix("folder", "tree", Fix(conflicts: ["notes.md"]), 2);
        Assert.Contains("Conflicts are still open", Text(result, "reason")); Assert.Single(fake.Calls);
        fake = new(); fake.Add(); fake.Add(stdout: "notes.md\n");
        result = await new PrCheck(fake.Run).FinishFix("folder", "tree", Fix(conflicts: ["notes.md"]), 2);
        Assert.Contains("Conflict markers", Text(result, "reason"));
        Assert.Equal(["grep", "-l", "-E", "^(<<<<<<< |>>>>>>> )", "--", "notes.md"], fake.At(1)); fake.Exhausted();
    }

    [Fact]
    public async Task No_change_does_not_push_and_refusal_keeps_commit_for_resume()
    {
        var fake = new Fake(); fake.Add(); fake.Add(1); fake.Add(); fake.Add(); fake.Add(stdout: "a\n");
        var service = new PrCheck(fake.Run);
        var result = await service.FinishFix("folder", "tree", Fix(), 3);
        Assert.Equal("false", Text(result, "changed")); Assert.Equal(5, fake.Calls.Count); fake.Exhausted();

        fake = new(); fake.Add(); fake.Add(1); fake.Add(); fake.Add(); fake.Add(stdout: "b\n"); fake.Add(1, stderr: "remote changed\nnon-fast-forward\n");
        service = new PrCheck(fake.Run); result = await service.FinishFix("folder", "tree", Fix(), 3);
        Assert.Equal("true", Text(result, "kept")); Assert.Contains("non-fast-forward", Text(result, "reason"));
        Assert.Equal(["push", "origin", "HEAD:refs/heads/feat/x"], fake.At(5)); fake.Exhausted();

        fake = new(); fake.Add(); fake.Add(1); fake.Add(); fake.Add(); fake.Add(stdout: "b\n"); fake.Add();
        result = await new PrCheck(fake.Run).FinishFix("folder", "tree", Fix(), 3);
        Assert.Equal("Pushed to feat/x.", Text(result, "reason")); fake.Exhausted();
    }

    [Fact]
    public async Task Resolve_reports_post_abort_conflict_list_and_reruns_unique_ids()
    {
        var fake = new Fake(); fake.Add(stdout: "{\"mergeable\":\"CONFLICTING\",\"baseRefName\":\"main\"}"); fake.Add(); fake.Add(1); fake.Add(1); fake.Add(); fake.Add(stdout: "");
        var result = await new PrCheck(fake.Run).ResolveConflicts("folder", 7);
        Assert.Equal("[]", result["files"]!.ToJsonString());
        Assert.Equal(["merge", "--abort"], fake.At(4)); fake.Exhausted();
        fake = new(); fake.Add(); fake.Add(1);
        var runs = JsonNode.Parse("[{\"runId\":\"42\"},{\"runId\":\"42\"},{\"runId\":\"43\"}]");
        result = await new PrCheck(fake.Run).RerunNotRun("folder", runs);
        Assert.Equal("2", Text(result, "asked")); Assert.Equal("1", Text(result, "rerun")); fake.Exhausted();
    }

    [Theory]
    [InlineData(0, "Pull request not found.")]
    [InlineData(1, "Pull request was not readable.")]
    [InlineData(2, "Could not check out the pull request.")]
    [InlineData(3, "Merged, but the push failed. The commit is saved locally.")]
    public async Task Resolve_preserves_each_command_failure(int stage, string reason)
    {
        var fake = new Fake();
        fake.Add(stage == 0 ? 1 : 0, stage == 1 ? "not json" : "{\"mergeable\":\"CONFLICTING\",\"baseRefName\":\"main\"}");
        if (stage >= 2) fake.Add(stage == 2 ? 1 : 0);
        if (stage == 3) { fake.Add(1); fake.Add(); fake.Add(1); }
        var result = await new PrCheck(fake.Run).ResolveConflicts("folder", 11);
        Assert.Equal(reason, Text(result, "reason"));
        if (stage == 3) Assert.Equal(["push"], fake.At(4));
        fake.Exhausted();
    }

    [Theory]
    [InlineData(0, "Could not fetch the pull request branch.")]
    [InlineData(1, "Could not make a copy of the folder for the fix.")]
    public async Task Prepare_stops_on_fetch_or_add_failure(int stage, string reason)
    {
        var fake = new Fake(); fake.Add(stage == 0 ? 1 : 0);
        if (stage == 1) fake.Add(1);
        var tree = Path.Combine(Path.GetTempPath(), "pw-pr-" + Guid.NewGuid().ToString("N"), "copy");
        try
        {
            var result = await new PrCheck(fake.Run).PrepareWorktree("folder", "feat/x", "main", [], tree);
            Assert.Equal(reason, Text(result, "reason"));
            Assert.Equal(stage + 1, fake.Calls.Count); fake.Exhausted();
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(tree)!)) Directory.Delete(Path.GetDirectoryName(tree)!, true); }
    }

    [Fact]
    public async Task Finish_commits_in_progress_merge_then_changes_and_pushes_exact_branch()
    {
        var root = Directory.CreateTempSubdirectory("pw-pr-merge-");
        try
        {
            var git = Path.Combine(root.FullName, "git"); Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "MERGE_HEAD"), "a");
            var fake = new Fake();
            fake.Add(); fake.Add(1); fake.Add(stdout: "git\n"); fake.Add(); fake.Add();
            fake.Add(stdout: "modified.md\n"); fake.Add(); fake.Add(); fake.Add(stdout: "b\n"); fake.Add();
            var result = await new PrCheck(fake.Run).FinishFix("folder", root.FullName, Fix("feat/x", "a"), 8);
            Assert.Equal("true", Text(result, "changed"));
            Assert.Equal(["add", "-A"], fake.At(3));
            Assert.Equal(["commit", "--no-edit"], fake.At(4));
            Assert.Equal(["commit", "-m", "Fix pull request #8"], fake.At(7));
            Assert.Equal(["push", "origin", "HEAD:refs/heads/feat/x"], fake.At(9)); fake.Exhausted();
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(true, "The merge could not be committed.")]
    [InlineData(false, "The fix could not be committed.")]
    public async Task Failed_commits_stop_before_push(bool mergeCommit, string reason)
    {
        var root = Directory.CreateTempSubdirectory("pw-pr-commit-");
        try
        {
            var fake = new Fake(); fake.Add(); fake.Add(1);
            if (mergeCommit)
            {
                var git = Path.Combine(root.FullName, "git"); Directory.CreateDirectory(git);
                File.WriteAllText(Path.Combine(git, "MERGE_HEAD"), "a");
                fake.Add(stdout: "git"); fake.Add(); fake.Add(1);
            }
            else
            {
                fake.Add(); fake.Add(stdout: "modified"); fake.Add(); fake.Add(1);
            }
            var result = await new PrCheck(fake.Run).FinishFix("folder", root.FullName, Fix(), 5);
            Assert.Equal(reason, Text(result, "reason"));
            Assert.DoesNotContain(fake.Calls, call => call.Args.Contains("push")); fake.Exhausted();
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Two_independent_folders_keep_commands_separate()
    {
        var left = new Fake(); var right = new Fake();
        foreach (var fake in new[] { left, right })
        {
            fake.Add(stdout: "{\"mergeable\":\"MERGEABLE\"}");
            fake.Add(stdout: "{\"mergeable\":\"MERGEABLE\"}");
        }
        var results = await Task.WhenAll(new PrCheck(left.Run).ResolveConflicts("left", 1), new PrCheck(right.Run).ResolveConflicts("right", 2));
        Assert.All(results, result => Assert.Equal("No conflicts.", Text(result, "reason")));
        Assert.All(left.Calls, call => Assert.Equal("left", call.Folder));
        Assert.All(right.Calls, call => Assert.Equal("right", call.Folder));
        var reruns = JsonNode.Parse("[{\"runId\":\"99\"}]");
        await Task.WhenAll(new PrCheck(left.Run).RerunNotRun("left", reruns), new PrCheck(right.Run).RerunNotRun("right", reruns));
        Assert.All(left.Calls, call => Assert.Equal("left", call.Folder));
        Assert.All(right.Calls, call => Assert.Equal("right", call.Folder));
        left.Exhausted(); right.Exhausted();
    }

    [Fact]
    public async Task Failed_fetch_in_legacy_conflict_repair_is_ignored_before_merge()
    {
        var fake = new Fake(); fake.Add(stdout: "{\"mergeable\":\"CONFLICTING\",\"baseRefName\":\"main\"}");
        fake.Add(); fake.Add(1, stderr: "fetch unavailable"); fake.Add(); fake.Add();
        var result = await new PrCheck(fake.Run).ResolveConflicts("folder", 3);
        Assert.Equal("Merged the base branch and pushed.", Text(result, "reason"));
        Assert.Equal(["merge", "origin/main", "--no-edit"], fake.At(3)); fake.Exhausted();
    }

    [Fact]
    public async Task Failed_grep_and_status_are_treated_like_no_markers_or_changes()
    {
        var fake = new Fake(); fake.Add(); fake.Add(2, stderr: "grep failed");
        fake.Add(1, stderr: "git dir unavailable"); fake.Add(1, stderr: "status unavailable"); fake.Add(stdout: "same\n");
        var result = await new PrCheck(fake.Run).FinishFix("folder", "tree", Fix(before: "same"), 2);
        Assert.Equal("Nothing needed changing.", Text(result, "reason"));
        Assert.Equal(["status", "--porcelain"], fake.At(3)); fake.Exhausted();
    }

    [Fact]
    public async Task Cleanup_attempts_prune_even_when_remove_fails()
    {
        var fake = new Fake(); fake.Add(1); fake.Add(1);
        await new PrCheck(fake.Run).RemoveWorktree("folder", "copy");
        Assert.Equal(["worktree", "remove", "--force", "copy"], fake.At(0));
        Assert.Equal(["worktree", "prune"], fake.At(1)); fake.Exhausted();
    }
}
