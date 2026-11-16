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
    public async Task A_zip_that_cannot_be_unpacked_or_has_no_program_leaves_the_app_and_no_stage()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var junk = Path.Combine(home.Parent, "broken.zip");
        File.WriteAllText(junk, "this is not a zip");
        var broken = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(home.Client(junk, Sha(junk)), home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(NativeUpdater.UnpackMessage, broken.Message);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));

        var empty = Path.Combine(home.Parent, "empty-pack");
        Directory.CreateDirectory(Path.Combine(empty, "Planwharf-native"));
        File.WriteAllText(Path.Combine(empty, "Planwharf-native", "readme.txt"), "no program");
        var zip = Path.Combine(home.Parent, "no-program.zip");
        ZipFile.CreateFromDirectory(empty, zip);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(home.Client(zip, Sha(zip)), home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal("The update does not contain the Planwharf program. Nothing was changed.", missing.Message);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Equal("notebook", File.ReadAllText(home.Notebook));
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));
    }

    [Fact]
    public async Task A_release_that_is_not_newer_or_not_json_changes_nothing()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var zip = home.Zip();
        var same = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(home.Client(zip, Sha(zip), "v0.0.1"), home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal("This release is not newer than the installed Planwharf. Nothing was changed.", same.Message);
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));

        var garbage = new HttpClient(new TextHandler("{")) { Timeout = TimeSpan.FromSeconds(5) };
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeUpdater.RunAsync(garbage, home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(NativeUpdater.OfflineMessage, bad.Message);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Equal("notebook", File.ReadAllText(home.Notebook));
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

    [Fact]
    public void Pick_rejects_draft_prerelease_bad_tag_bad_url_missing_asset_and_bad_digest()
    {
        var rid = NativeUpdater.Rid;
        var tag = "v1.2.4";
        var name = $"Planwharf-native-{rid}-{tag}.zip";
        var url = $"https://github.com/{NativeUpdater.Repo}/releases/download/{tag}/{name}";
        var digest = "sha256:" + new string('a', 64);

        JsonObject Valid() => new()
        {
            ["draft"] = false, ["prerelease"] = false, ["tag_name"] = tag,
            ["html_url"] = $"https://github.com/{NativeUpdater.Repo}/releases/tag/{tag}",
            ["assets"] = new JsonArray(new JsonObject { ["name"] = name, ["browser_download_url"] = url, ["digest"] = digest })
        };

        var draft = Valid(); draft["draft"] = true;
        Assert.Null(NativeUpdater.Pick(draft, rid));

        var pre = Valid(); pre["prerelease"] = true;
        Assert.Null(NativeUpdater.Pick(pre, rid));

        var badTag = Valid(); badTag["tag_name"] = "latest";
        Assert.Null(NativeUpdater.Pick(badTag, rid));

        var badPage = Valid(); badPage["html_url"] = $"https://github.com/other/repo/releases/tag/{tag}";
        Assert.Null(NativeUpdater.Pick(badPage, rid));

        // No asset for a different rid
        Assert.Null(NativeUpdater.Pick(Valid(), rid == "win-x64" ? "linux-x64" : "win-x64"));

        var badDigest = Valid();
        ((badDigest["assets"] as JsonArray)![0] as JsonObject)!["digest"] = "md5:abcdef";
        Assert.Null(NativeUpdater.Pick(badDigest, rid));

        Assert.Null(NativeUpdater.Pick(null, rid));

        // A well-formed release is accepted
        Assert.NotNull(NativeUpdater.Pick(Valid(), rid));
    }

    [Fact]
    public void Newer_compares_all_three_version_positions_and_handles_invalid_input()
    {
        Assert.True(NativeUpdater.Newer("v2.0.0", "v1.9.9"));
        Assert.True(NativeUpdater.Newer("v1.1.0", "v1.0.9"));
        Assert.True(NativeUpdater.Newer("v1.0.1", "v1.0.0"));
        Assert.False(NativeUpdater.Newer("v1.0.0", "v1.0.0"));
        Assert.False(NativeUpdater.Newer("v1.0.0", "v1.0.1"));
        Assert.False(NativeUpdater.Newer("v1.0.0", "v2.0.0"));
        // A tag that cannot be parsed is never newer
        Assert.False(NativeUpdater.Newer("not-a-tag", "v1.0.0"));
        // An unknown installed version: any valid tag is treated as newer
        Assert.True(NativeUpdater.Newer("v1.0.0", "unknown"));
        // Versions without the v prefix are accepted
        Assert.True(NativeUpdater.Newer("1.0.1", "1.0.0"));
    }

    [Fact]
    public void Null_and_finished_snapshot_have_no_running_step_and_empty_checkpoint_list()
    {
        Assert.False(NativeUpdater.StepIsRunning(null));
        Assert.Empty(NativeUpdater.ProjectsToCheckpoint(null));
        var finished = SampleState.Finished();
        Assert.False(NativeUpdater.StepIsRunning(finished));
        Assert.Empty(NativeUpdater.ProjectsToCheckpoint(finished));
    }

    [Fact]
    public void A_paused_project_with_canStop_is_included_in_the_checkpoint_list()
    {
        var state = SampleState.Working().AsObject();
        var project = state["projects"]![0]!.AsObject();
        // Clear the running step so StepIsRunning returns false, then mark the project as stoppable.
        project["subTasks"]![1]!["status"] = "WAITING";
        project["canStop"] = true;
        Assert.False(NativeUpdater.StepIsRunning(state));
        var ids = NativeUpdater.ProjectsToCheckpoint(state);
        Assert.Contains("weather", ids);
    }

    [Fact]
    public void Stage_handles_a_flat_zip_with_the_exe_at_the_root()
    {
        using var home = new Home();
        var pack = Path.Combine(home.Parent, "flat-pack");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, NativeUpdater.ExeName), "flat-build");
        var zip = Path.Combine(home.Parent, "flat.zip");
        ZipFile.CreateFromDirectory(pack, zip);
        var exe = NativeUpdater.Stage(zip, home.InstallFolder, "v1.3.0");
        var stageDir = Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v1.3.0");
        Assert.Equal(Path.Combine(stageDir, NativeUpdater.ExeName), exe);
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void Finish_returns_1_when_no_exe_is_found_in_the_source_folder()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var source = Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v1.2.4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "no exe here");
        var launched = false;
        var code = NativeUpdater.Finish(home.InstallFolder, oldPid: int.MaxValue, wait: TimeSpan.Zero,
            source: source, startInstalled: _ => launched = true);
        Assert.Equal(1, code);
        Assert.False(launched);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.False(File.Exists(Path.Combine(home.InstallFolder, "readme.txt")));
    }

    [Fact]
    public void Finish_returns_1_when_starting_the_installed_app_raises_an_exception()
    {
        using var home = new Home();
        var source = Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v1.2.4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, NativeUpdater.ExeName), "new-build");
        var code = NativeUpdater.Finish(home.InstallFolder, oldPid: int.MaxValue, wait: TimeSpan.Zero,
            source: source, startInstalled: _ => throw new InvalidOperationException("cannot start"));
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task RunAsync_reports_offline_when_zip_download_returns_http_error()
    {
        using var home = new Home();
        var marker = home.Install("keep me.txt", "original");
        var tag = "v1.2.4";
        var name = $"Planwharf-native-{NativeUpdater.Rid}-{tag}.zip";
        var url = $"https://github.com/{NativeUpdater.Repo}/releases/download/{tag}/{name}";
        var json = new JsonObject
        {
            ["draft"] = false,
            ["prerelease"] = false,
            ["tag_name"] = tag,
            ["html_url"] = $"https://github.com/{NativeUpdater.Repo}/releases/tag/{tag}",
            ["assets"] = new JsonArray(new JsonObject { ["name"] = name, ["browser_download_url"] = url, ["digest"] = "sha256:" + new string('0', 64) })
        };
        // Release metadata succeeds, but zip download endpoint returns 404 NotFound.
        var client = new HttpClient(new MockHttpHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("/releases/latest") == true)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var err = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeUpdater.RunAsync(client, home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(NativeUpdater.OfflineMessage, err.Message);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));
    }

    [Fact]
    public async Task RunAsync_reports_missing_asset_when_release_has_no_asset_for_this_rid()
    {
        using var home = new Home();
        var otherRid = NativeUpdater.Rid == "win-x64" ? "linux-x64" : "win-x64";
        var tag = "v1.2.4";
        var name = $"Planwharf-native-{otherRid}-{tag}.zip";
        var url = $"https://github.com/{NativeUpdater.Repo}/releases/download/{tag}/{name}";
        var json = new JsonObject
        {
            ["draft"] = false,
            ["prerelease"] = false,
            ["tag_name"] = tag,
            ["html_url"] = $"https://github.com/{NativeUpdater.Repo}/releases/tag/{tag}",
            ["assets"] = new JsonArray(new JsonObject { ["name"] = name, ["browser_download_url"] = url, ["digest"] = "sha256:" + new string('0', 64) })
        };
        var client = new HttpClient(new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") }));

        var err = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeUpdater.RunAsync(client, home.InstallFolder, "v0.0.1", null, true, _ => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(NativeUpdater.MissingAssetMessage, err.Message);
        Assert.Empty(Directory.GetDirectories(home.Parent, NativeUpdater.StagePrefix + "*"));
    }

    [Fact]
    public async Task RunAsync_overwrites_existing_staging_folder_from_aborted_update()
    {
        using var home = new Home();
        var zip = home.Zip();
        var tag = "v1.2.4";
        var stage = Path.Combine(home.Parent, NativeUpdater.StagePrefix + tag);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "stale.txt"), "leftover");

        var launched = false;
        await NativeUpdater.RunAsync(home.Client(zip, Sha(zip), tag), home.InstallFolder, "v0.0.1", null, true,
            _ => Task.CompletedTask,
            (exe, _) => { launched = File.Exists(exe); });

        Assert.True(launched);
        Assert.False(File.Exists(Path.Combine(stage, "stale.txt")));
    }

    [Fact]
    public void CleanStages_preserves_installFolder_when_inside_stage_and_removes_other_stages()
    {
        using var home = new Home();
        // Create an outer stage folder that actually contains an install folder inside it
        var insideStage = Path.Combine(home.Parent, NativeUpdater.StagePrefix + "v1.0.0");
        var nestedInstall = Path.Combine(insideStage, "installed-app");
        Directory.CreateDirectory(nestedInstall);

        // Create an unrelated abandoned stage folder
        var abandonedStage = Path.Combine(insideStage, NativeUpdater.StagePrefix + "v0.9.0");
        Directory.CreateDirectory(abandonedStage);

        // Create an unrelated normal directory
        var normalDir = Path.Combine(insideStage, "my-normal-dir");
        Directory.CreateDirectory(normalDir);

        NativeUpdater.CleanStages(nestedInstall);

        // The nestedInstall parent is insideStage. Because nestedInstall is inside insideStage, insideStage is kept.
        // abandonedStage should be deleted.
        Assert.False(Directory.Exists(abandonedStage));
        // normalDir should be preserved.
        Assert.True(Directory.Exists(normalDir));

        // Calling CleanStages on a root or non-existent parent does not throw
        NativeUpdater.CleanStages(Path.GetPathRoot(home.Parent)!);
    }

    [Fact]
    public void FinishStart_configures_process_start_info_correctly()
    {
        var exe = Path.Combine(Path.GetTempPath(), "test-stage", NativeUpdater.ExeName);
        var install = Path.Combine(Path.GetTempPath(), "test-install");
        var psi = NativeUpdater.FinishStart(exe, install, 4321);

        Assert.Equal(exe, psi.FileName);
        Assert.Equal(Path.GetDirectoryName(exe), psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(3, psi.ArgumentList.Count);
        Assert.Equal(NativeUpdater.FinishFlag, psi.ArgumentList[0]);
        Assert.Equal(Path.GetFullPath(install), psi.ArgumentList[1]);
        Assert.Equal("4321", psi.ArgumentList[2]);
    }

    [Fact]
    public void Verify_checks_sha256_case_insensitively_and_handles_empty_file()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, [1, 2, 3, 4, 5]);
            var sha = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4, 5]));
            Assert.True(NativeUpdater.Verify(temp, sha.ToLowerInvariant()));
            Assert.True(NativeUpdater.Verify(temp, sha.ToUpperInvariant()));
            Assert.False(NativeUpdater.Verify(temp, new string('0', 64)));

            File.WriteAllBytes(temp, []);
            var emptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            Assert.True(NativeUpdater.Verify(temp, emptySha));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public async Task DownloadAsync_rejects_untrusted_asset_url()
    {
        using var home = new Home();
        var release = new NativeRelease("v1.2.4", "https://github.com/benitz94/Planwharf/releases/tag/v1.2.4",
            "malicious.zip", "https://untrusted-domain.com/malicious.zip", new string('0', 64));
        var client = new HttpClient();
        var err = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeUpdater.DownloadAsync(client, release, home.InstallFolder));
        Assert.Equal(NativeUpdater.MissingAssetMessage, err.Message);
    }

    sealed class MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
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

    sealed class TextHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

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
