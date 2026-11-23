using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class RepositoryTests
{
    [Fact]
    public void GithubRepo_ParsesHttpsAndSshUrlsWithOrWithoutGitSuffix()
    {
        Assert.Equal("owner/project", Repository.GithubRepo("https://github.com/owner/project.git"));
        Assert.Equal("owner/project", Repository.GithubRepo("git@github.com:owner/project.git"));
        Assert.Equal("owner/project", Repository.GithubRepo("https://github.com/owner/project/"));
        Assert.Equal("owner/project", Repository.GithubRepo("https://github.com/owner/project"));
        Assert.Null(Repository.GithubRepo("https://evil.example/owner/project"));
        Assert.Null(Repository.GithubRepo(""));
        Assert.Null(Repository.GithubRepo(null));
    }

    [Fact]
    public void RepositoryAddress_GiteaInstance_ParsesBaseUrlAndPathPrefix()
    {
        var addr1 = Repository.GetRepositoryAddress("https://forge.example/git/owner/repo.git");
        Assert.NotNull(addr1);
        Assert.Equal("gitea", addr1.Provider);
        Assert.Equal("https://forge.example/git", addr1.BaseUrl);
        Assert.Equal("owner/repo", addr1.Repository);
        Assert.Equal("https://forge.example/git/owner/repo", addr1.Url);

        var addrGithub = Repository.GetRepositoryAddress("https://github.com/owner/repo");
        Assert.NotNull(addrGithub);
        Assert.Equal("github", addrGithub.Provider);

        Assert.Null(Repository.GetRepositoryAddress("https://user:pass@forge.example/a/b"));
        Assert.Null(Repository.GetRepositoryAddress("http://forge.example/a/b"));
        Assert.Null(Repository.GetRepositoryAddress("https://forge.example/a/b?token=secret"));
    }

    [Fact]
    public void CleanError_RedactsPatAndBearerTokens()
    {
        var cleaned = Repository.CleanError("ghp_secret1234567890abcdef https://name:secret@github.com and github_pat_987654321");
        Assert.DoesNotContain("ghp_secret", cleaned);
        Assert.DoesNotContain("secret@", cleaned);
        Assert.DoesNotContain("github_pat_", cleaned);
        Assert.Contains("[redacted] https://[redacted]@github.com and [redacted]", cleaned);
    }

    [Fact]
    public void Relation_AheadBehindCombinations_ReturnsDivergedAheadBehindInSync()
    {
        Assert.Equal("diverged", Repository.Relation(1, 1));
        Assert.Equal("ahead", Repository.Relation(2, 0));
        Assert.Equal("behind", Repository.Relation(0, 3));
        Assert.Equal("in-sync", Repository.Relation(0, 0));
    }

    [Fact]
    public async Task CreateRepositoryInspector_CachesResultsUntilTtlExpires()
    {
        int executeCalls = 0;
        var inspector = Repository.CreateRepositoryInspector(
            execute: (cmd, args, opt) =>
            {
                executeCalls++;
                if (cmd == "gh")
                {
                    return Task.FromResult(new RepoExecResult(true, "{\"private\":true,\"default_branch\":\"main\"}", null));
                }
                return Task.FromResult(new RepoExecResult(true, "", null));
            },
            ttlMs: 60000
        );

        var project = new JsonObject
        {
            ["folder"] = null,
            ["github"] = "https://github.com/owner/project"
        };

        var res1 = await inspector(project, false);
        Assert.Equal("github", res1["remote"]?["provider"]?.ToString());
        var callsAfterFirst = executeCalls;
        Assert.True(callsAfterFirst > 0);

        var res2 = await inspector(project, false);
        Assert.Equal(callsAfterFirst, executeCalls);

        var res3 = await inspector(project, true);
        Assert.True(executeCalls > callsAfterFirst);
    }

    [Fact]
    public async Task CreateRepositoryInspector_DivergedHistory_DetectsCommitCountsViaMergeBase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-repo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "config"), "[core]\n\trepositoryformatversion = 0\n");
        var canonical = ProjectWorkspace.LinkedWorkspace(tempDir)!;
        try
        {
            var inspector = Repository.CreateRepositoryInspector(
                execute: (cmd, args, opt) =>
                {
                    if (cmd == "gh")
                    {
                        if (args.Contains("api") && args.Any(a => a.StartsWith("repos/owner/repo/branches/")))
                            return Task.FromResult(new RepoExecResult(true, "abcdef1234567890abcdef1234567890abcdef12", null));
                        return Task.FromResult(new RepoExecResult(true, "{\"private\":false,\"default_branch\":\"main\"}", null));
                    }
                    if (cmd == "git")
                    {
                        if (args.Contains("--show-toplevel"))
                            return Task.FromResult(new RepoExecResult(true, canonical, null));
                        if (args.Contains("rev-list"))
                            return Task.FromResult(new RepoExecResult(true, "2\t1", null));
                        if (args.Contains("merge-base"))
                            return Task.FromResult(new RepoExecResult(true, "base123", null));
                        if (args.Contains("rev-parse"))
                            return Task.FromResult(new RepoExecResult(true, "abcdef1234567890abcdef1234567890abcdef12", null));
                        return Task.FromResult(new RepoExecResult(true, "", null));
                    }
                    return Task.FromResult(new RepoExecResult(true, "", null));
                }
            );

            var project = new JsonObject
            {
                ["folder"] = canonical,
                ["github"] = "https://github.com/owner/repo"
            };

            var res = await inspector(project, true);
            var comp = res["comparison"] as JsonObject;
            Assert.NotNull(comp);
            Assert.Equal("diverged", comp["status"]?.ToString());
            Assert.Equal(2, comp["ahead"]?.GetValue<int>());
            Assert.Equal(1, comp["behind"]?.GetValue<int>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
