using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Native;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// The native updater, against a release zip and its digest kept in a temporary folder. No GitHub call.
public class NativeUpdaterTests
{
    [Fact]
    public async Task A_wrong_digest_changes_nothing()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var zip = home.Zip();
        var client = home.Client(zip, Sha(zip)[..^1] + (Sha(zip)[^1] == 'a' ? "b" : "a"));
        var checkpointed = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(client, home.InstallFolder, "v0.0.1", SampleState.Finished(), confirmed: true, _ => { checkpointed = true; return Task.CompletedTask; }, (_, _) => { }));
        Assert.Equal(NativeUpdater.BadDigestMessage, error.Message);
        Assert.False(checkpointed);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Equal("notebook", File.ReadAllText(home.Notebook));
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));
    }

    [Fact]
    public async Task Finish_copies_over_spaces_and_non_ascii_names_and_cleans_stages()
    {
        using var home = new Home();
        var oldExe = Path.Combine(home.InstallFolder, NativeUpdater.ExeName);
        File.WriteAllText(oldExe, "old-build");
        var source = Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v1.2.4", "Planwharf-native");
        Directory.CreateDirectory(Path.Combine(source, "My Files"));
        var exe = Path.Combine(source, NativeUpdater.ExeName);
        File.WriteAllText(exe, "new-build");
        File.WriteAllText(Path.Combine(source, "café notes.txt"), "bonjour");
        File.WriteAllText(Path.Combine(source, "My Files", "读我.txt"), "hello");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v0.0.1"));
        File.WriteAllText(Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v0.0.1", "junk.txt"), "old stage");
        string? started = null;
        var code = NativeUpdater.Finish(home.InstallFolder, oldPid: int.MaxValue, wait: TimeSpan.Zero, source: source, startInstalled: path => started = path);
        Assert.Equal(0, code);
        Assert.Equal("new-build", File.ReadAllText(oldExe));
        Assert.Equal("bonjour", File.ReadAllText(Path.Combine(home.InstallFolder, "café notes.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(home.InstallFolder, "My Files", "读我.txt")));
        Assert.Equal(Path.Combine(home.InstallFolder, NativeUpdater.ExeName), started);
        Assert.Equal("notebook", File.ReadAllText(home.Notebook));
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(oldExe).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Offline_and_no_write_access_leave_the_installed_app()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var offline = new HttpClient(new ThrowingHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        var down = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(offline, home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(NativeUpdater.OfflineMessage, down.Message);
        Assert.Equal("original", File.ReadAllText(marker));

        var zip = home.Zip();
        var tag = "v1.2.4";
        File.WriteAllText(Path.Combine(home.Parent, NativeUpdater.StagePrefix + tag), "not a folder");
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(home.Client(zip, Sha(zip), tag), home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(NativeUpdater.NoWriteMessage, blocked.Message);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Equal("notebook", File.ReadAllText(home.Notebook));
        Assert.True(File.Exists(Path.Combine(home.Parent, NativeUpdater.StagePrefix + tag)));
    }

    [Fact]
    public async Task A_running_step_is_refused_unless_confirmed_and_checkpointed_before_launch()
    {
        using var home = new Home();
        home.Install("keep me.txt", "original");
        var zip = home.Zip();
        var running = SampleState.Working();
        Assert.True(NativeUpdater.StepIsRunning(running));
        Assert.False(NativeUpdater.MayUpdate(stepRunning: true, confirmed: false));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(home.Client(zip, Sha(zip)), home.InstallFolder, "v0.0.1", running, confirmed: false, _ => throw new InvalidOperationException("should not checkpoint"), (_, _) => throw new InvalidOperationException("should not launch")));
        Assert.Equal(NativeUpdater.NeedsConfirmMessage, refused.Message);
        Assert.Equal("original", File.ReadAllText(Path.Combine(home.InstallFolder, "keep me.txt")));

        var events = new List<string>();
        IReadOnlyList<string>? stopped = null;
        await NativeUpdater.RunAsync(home.Client(zip, Sha(zip)), home.InstallFolder, "v0.0.1", running, confirmed: true,
            ids => { stopped = ids; events.Add("checkpoint"); return Task.CompletedTask; },
            (exe, _) => { events.Add(File.Exists(exe) ? "launch" : "missing"); },
            phase => events.Add(phase));
        Assert.Equal(["downloading", "checkpointing", "checkpoint", "restarting", "launch"], events);
        Assert.Equal(["weather"], stopped);
        Assert.Equal("original", File.ReadAllText(Path.Combine(home.InstallFolder, "keep me.txt")));
        Assert.False(NativeUpdater.StepIsRunning(SampleState.Finished()));
        Assert.True(NativeUpdater.MayUpdate(stepRunning: false, confirmed: false));
    }

    [Fact]
    public async Task A_failed_checkpoint_removes_the_stage_and_leaves_the_app()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var zip = home.Zip();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(home.Client(zip, Sha(zip)), home.InstallFolder, "v0.0.1", SampleState.Working(), true, _ => throw new IOException("disk full"), (_, _) => { }));
        Assert.Equal(NativeUpdater.CheckpointMessage, error.Message);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));
    }

    [Fact]
    public void Finish_update_is_handled_before_a_window_and_rejects_bad_arguments()
    {
        Assert.Null(Program.FinishUpdate([]));
        Assert.Null(Program.FinishUpdate(["--port", "1"]));
        Assert.Equal(2, Program.FinishUpdate([NativeUpdater.FinishFlag]));
        Assert.Equal(2, Program.FinishUpdate([NativeUpdater.FinishFlag, " ", "4"]));
        Assert.Equal(2, Program.FinishUpdate([NativeUpdater.FinishFlag, "C:\\Plan wharf", "nope"]));
    }

    sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    // A temporary install folder with a space and a non-ASCII name, plus a notebook the updater must not touch.
    sealed class Home : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "pw-native-update-" + Guid.NewGuid().ToString("N"));
        public string InstallFolder => Path.Combine(Parent, "Plan wharf café");
        public string Notebook { get; }

        public Home()
        {
            Directory.CreateDirectory(InstallFolder);
            var projects = Path.Combine(Parent, "My Projects");
            Directory.CreateDirectory(projects);
            Notebook = Path.Combine(projects, "plan.json");
            File.WriteAllText(Notebook, "notebook");
        }

        public string Install(string name, string text)
        {
            var path = Path.Combine(InstallFolder, name);
            File.WriteAllText(path, text);
            return path;
        }

        public string Zip()
        {
            var pack = Path.Combine(Parent, "pack");
            var inner = Path.Combine(pack, "Planwharf-native");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(inner, NativeUpdater.ExeName), "new-build");
            var zip = Path.Combine(Parent, "release.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(pack, zip);
            return zip;
        }

        public HttpClient Client(string zip, string sha, string tag = "v1.2.4")
        {
            var name = $"Planwharf-native-{NativeUpdater.Rid}-{tag}.zip";
            var url = $"https://github.com/{NativeUpdater.Repo}/releases/download/{tag}/{name}";
            var json = new JsonObject
            {
                ["draft"] = false,
                ["prerelease"] = false,
                ["tag_name"] = tag,
                ["html_url"] = $"https://github.com/{NativeUpdater.Repo}/releases/tag/{tag}",
                ["assets"] = new JsonArray(new JsonObject { ["name"] = name, ["browser_download_url"] = url, ["digest"] = "sha256:" + sha })
            };
            return new HttpClient(new FolderHandler(json.ToJsonString(), File.ReadAllBytes(zip))) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public void Dispose()
        {
            try { Directory.Delete(Parent, recursive: true); } catch (IOException) { }
        }
    }

    static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    sealed class FolderHandler(string json, byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/releases/latest", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
