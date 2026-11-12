using System.Diagnostics;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// Commit from the app, against the real server and a real git folder. Nothing is pushed.
public class CommitServerTests
{
    static void Git(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var git = Process.Start(info)!;
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, git.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task Reads_changes_and_commits_them()
    {
        var data = Directory.CreateTempSubdirectory("pw-native-git-");
        foreach (var (name, value) in new[] { ("GIT_AUTHOR_NAME", "Sample"), ("GIT_AUTHOR_EMAIL", "sample@example.com"), ("GIT_COMMITTER_NAME", "Sample"), ("GIT_COMMITTER_EMAIL", "sample@example.com") })
            Environment.SetEnvironmentVariable(name, value);
        var host = new ServerHost(Path.Combine(data.FullName, "data"));
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(data.FullName, "site")).FullName;
            Git(folder, "init", "-q");
            File.WriteAllText(Path.Combine(folder, "index.html"), "<h1>Sample</h1>");
            var api = new PlanwharfApi(await host.StartAsync("v0.0.1", TimeSpan.FromSeconds(60)));
            var id = (await api.CreateProjectAsync("Git sample", folder))!;

            var before = WorkDetails.Repo(await api.RepositoryAsync(id, refresh: true));
            Assert.True(before.Git);
            Assert.Equal(1, before.Uncommitted);

            var result = await api.CommitAsync(id, "Add the sample page", push: false);
            Assert.False(result?["pushed"]?.GetValue<bool>());
            Assert.Matches("^[0-9a-f]{7,}$", result?["commit"]?.GetValue<string>() ?? "");
            Assert.Equal(0, WorkDetails.Repo(await api.RepositoryAsync(id, refresh: true)).Uncommitted);
            var nothing = await Assert.ThrowsAsync<InvalidOperationException>(() => api.CommitAsync(id, "Again", push: false));
            Assert.Contains("Nothing to commit", nothing.Message);
        }
        finally
        {
            await host.DisposeAsync();
            try { data.Delete(true); } catch { }
        }
    }
}
