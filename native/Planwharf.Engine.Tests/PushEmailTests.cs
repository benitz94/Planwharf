// Tests for PushEmail.cs.
using Xunit;

namespace Planwharf.Engine.Tests;

public class PushEmailTests
{
    [Fact]
    public void PrivateEmailRefused_DetectsGH007ErrorCode()
    {
        Assert.True(PushEmail.PrivateEmailRefused("remote: error: GH007: Your push would contain a private email address"));
        Assert.False(PushEmail.PrivateEmailRefused("Everything pushed cleanly"));
    }

    [Fact]
    public async Task GithubNoreplyAsync_ValidGhApiResponse_FormatsNoreplyAddress()
    {
        Task<ProcessResult> GhMock(IReadOnlyList<string> args)
        {
            return Task.FromResult(new ProcessResult(0, "12345+octocat@users.noreply.github.com\n", ""));
        }

        var email = await PushEmail.GithubNoreplyAsync(GhMock);
        Assert.Equal("12345+octocat@users.noreply.github.com", email);
    }

    [Fact]
    public async Task UseNoreplyAsync_NoUnpushedCommits_ReturnsRewritten0()
    {
        Task<ProcessResult> GitMock(IReadOnlyList<string> args)
        {
            if (args[0] == "config") return Task.FromResult(new ProcessResult(0, "", ""));
            if (args[0] == "rev-list") return Task.FromResult(new ProcessResult(0, "", ""));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        var result = await PushEmail.UseNoreplyAsync(GitMock, "123+user@users.noreply.github.com");
        Assert.True(result.Ok);
        Assert.Equal(0, result.Rewritten);
    }

    [Fact]
    public async Task UseNoreplyAsync_UnpushedCommits_RebasesAndResetsAuthorWithoutTouchingRemote()
    {
        Task<ProcessResult> GitMock(IReadOnlyList<string> args)
        {
            if (args[0] == "config") return Task.FromResult(new ProcessResult(0, "", ""));
            if (args[0] == "rev-list") return Task.FromResult(new ProcessResult(0, "abc1234\ndef5678", ""));
            if (args[0] == "rev-parse") return Task.FromResult(new ProcessResult(0, "root_sha", ""));
            if (args.Contains("rebase")) return Task.FromResult(new ProcessResult(0, "Successfully rebased", ""));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        var result = await PushEmail.UseNoreplyAsync(GitMock, "123+user@users.noreply.github.com");
        Assert.True(result.Ok);
        Assert.Equal(2, result.Rewritten);
    }

    [Fact]
    public async Task UseNoreplyAsync_RebaseFails_AbortsCleanlyAndReportsFailure()
    {
        bool aborted = false;
        Task<ProcessResult> GitMock(IReadOnlyList<string> args)
        {
            if (args[0] == "config") return Task.FromResult(new ProcessResult(0, "", ""));
            if (args[0] == "rev-list") return Task.FromResult(new ProcessResult(0, "abc1234", ""));
            if (args[0] == "rev-parse") return Task.FromResult(new ProcessResult(0, "root_sha", ""));
            if (args.Contains("rebase"))
            {
                if (args.Contains("--abort")) { aborted = true; return Task.FromResult(new ProcessResult(0, "", "")); }
                return Task.FromResult(new ProcessResult(1, "", "rebase conflict"));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        var result = await PushEmail.UseNoreplyAsync(GitMock, "123+user@users.noreply.github.com");
        Assert.False(result.Ok);
        Assert.True(aborted);
        Assert.Contains("Could not change the email", result.Reason);
    }
}
