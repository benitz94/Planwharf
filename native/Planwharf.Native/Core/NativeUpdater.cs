using Planwharf.Engine;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

// The native app updates itself from the latest GitHub release: it downloads the zip for this system,
// checks it against the SHA-256 GitHub publishes, unpacks it next to the installed folder, and quits;
// the new copy then waits for the old one to close, replaces the installed files and starts again.
// Projects and settings live in another folder (see ServerHost) and are never touched.
public sealed record NativeRelease(string Tag, string PageUrl, string AssetName, string AssetUrl, string Sha256);

public static class NativeUpdater
{
    public const string Repo = "benitz94/Planwharf";
    public const string FinishFlag = "--finish-update";
    public const string StagePrefix = ".planwharf-update-";
    public static string BadDigestMessage => Strings.T("The downloaded update does not match the release\u0027s checksum. Nothing was changed.");
    public static string OfflineMessage => Strings.T("Could not download the update (offline or the release page did not answer). Nothing was changed.");
    public static string NoWriteMessage => Strings.T("Planwharf could not write the update next to the installed folder. Nothing was changed.");
    public static string UnpackMessage => Strings.T("The update could not be unpacked. Nothing was changed.");
    public static string NeedsConfirmMessage => Strings.T("A step is still running. Planwharf will update only after you confirm, so the work can be saved first.");
    public static string CheckpointMessage => Strings.T("Could not save the running work, so the update stopped. Nothing was changed.");
    public static string MissingAssetMessage => Strings.T("This release has no Planwharf app for this computer. Nothing was changed.");

    static readonly Regex TagPattern = new(@"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    static readonly Regex DigestPattern = new(@"^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static NativeUpdater() => Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Planwharf");

    public static string ExeName => OperatingSystem.IsWindows() ? "Planwharf.exe" : "Planwharf";

    // The name of this system in the release files: win-x64, linux-x64, osx-arm64 or osx-x64.
    public static string Rid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }

    // The installed folder: where the running Planwharf executable is.
    public static string InstallFolder => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // Reads GitHub's latest-release answer: the stable release and the zip for this system, with its digest.
    public static NativeRelease? Pick(JsonNode? latest, string rid)
    {
        if (latest is not JsonObject release || Bool(release["draft"]) || Bool(release["prerelease"])) return null;
        var tag = Str(release["tag_name"]);
        if (!TagPattern.IsMatch(tag)) return null;
        var page = Str(release["html_url"]);
        if (page != $"https://github.com/{Repo}/releases/tag/{tag}") return null;
        var name = $"Planwharf-native-{rid}-{tag}.zip";
        var asset = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => Str(item["name"]) == name);
        if (asset == null) return null;
        var url = Str(asset["browser_download_url"]);
        var digest = Str(asset["digest"]);
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal) || !DigestPattern.IsMatch(digest)) return null;
        return new NativeRelease(tag, page, name, url, digest["sha256:".Length..]);
    }

    // True when the release is newer than the running version (both as vX.Y.Z or X.Y.Z).
    public static bool Newer(string tag, string current)
    {
        static int[]? Parts(string text)
        {
            var parts = text.Trim().TrimStart('v').Split('.');
            if (parts.Length != 3) return null;
            var numbers = new int[3];
            for (var i = 0; i < 3; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
            return numbers;
        }
        if (Parts(tag) is not { } next) return false;
        if (Parts(current) is not { } now) return true;
        for (var i = 0; i < 3; i++) if (next[i] != now[i]) return next[i] > now[i];
        return false;
    }

    public static async Task<NativeRelease?> CheckAsync(HttpClient http, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Planwharf");
        using var response = await http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return Pick(JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)), Rid);
    }

    // Downloads the zip and checks it. A file that does not match the published digest is deleted and never used.
    public static async Task<string> DownloadAsync(HttpClient http, NativeRelease release, string folder, CancellationToken cancel = default)
    {
        if (!release.AssetUrl.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal))
            throw new InvalidOperationException(MissingAssetMessage);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, release.AssetName);
        try
        {
            using var response = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            await using var target = File.Create(file);
            await response.Content.CopyToAsync(target, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(file);
            throw new InvalidOperationException(ex is HttpRequestException or TaskCanceledException ? OfflineMessage : NoWriteMessage, ex);
        }
        if (!Verify(file, release.Sha256))
        {
            TryDeleteFile(file);
            throw new InvalidOperationException(BadDigestMessage);
        }
        return file;
    }

    public static bool Verify(string file, string sha256)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Unpacks the zip next to the installed folder and returns the new executable. The installed folder is not changed.
    public static string Stage(string zip, string installFolder, string tag)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(installFolder)) ?? throw new InvalidOperationException(NoWriteMessage);
        var stage = Path.Combine(parent, StagePrefix + tag);
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            if (File.Exists(stage)) throw new UnauthorizedAccessException(Strings.T("A file is using the update folder\u0027s name."));
            ZipFile.ExtractToDirectory(zip, stage);
        }
        catch (InvalidOperationException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new InvalidOperationException(NoWriteMessage, ex); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            TryDeleteDirectory(stage);
            throw new InvalidOperationException(UnpackMessage, ex);
        }
        var root = Directory.GetDirectories(stage) is [var only] && Directory.GetFiles(stage).Length == 0 ? only : stage;
        var exe = Path.Combine(root, ExeName);
        if (!File.Exists(exe))
        {
            TryDeleteDirectory(stage);
            throw new InvalidOperationException(Strings.T("The update does not contain the Planwharf program. Nothing was changed."));
        }
        return exe;
    }

    // Starts the new copy, which finishes the update once this process has closed.
    public static ProcessStartInfo FinishStart(string stagedExe, string installFolder, int pid)
    {
        var start = new ProcessStartInfo(stagedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stagedExe)! };
        start.ArgumentList.Add(FinishFlag);
        start.ArgumentList.Add(Path.GetFullPath(installFolder));
        start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    public static void Launch(string stagedExe, string installFolder) => Process.Start(FinishStart(stagedExe, installFolder, Environment.ProcessId));

    // Run by the new copy: waits for the old Planwharf to close, copies itself over the installed folder
    // (retrying while a file is still in use), starts the installed Planwharf again and removes staging folders.
    // source is the staged folder; the running copy passes null and uses its own folder.
    public static int Finish(string installFolder, int oldPid, TimeSpan? wait = null, string? source = null, Action<string>? startInstalled = null)
    {
        installFolder = Path.GetFullPath(installFolder);
        try
        {
            using var old = Process.GetProcessById(oldPid);
            if (!old.HasExited) old.WaitForExit(wait ?? TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException) { /* already closed */ }
        catch (InvalidOperationException) { /* already closed */ }

        var from = source ?? InstallFolder;
        if (!File.Exists(Path.Combine(from, ExeName))) return 1;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try { CopyTree(from, installFolder); break; }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(200); }
            catch (IOException) { return 1; }
            catch (UnauthorizedAccessException) { return 1; }
        }
        var exe = Path.Combine(installFolder, ExeName);
        if (!File.Exists(exe)) return 1;
        try
        {
            if (startInstalled != null) startInstalled(exe);
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installFolder });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return 1;
        }
        CleanStages(installFolder);
        return 0;
    }

    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    // Folders left by an earlier update, next to the installed folder, are removed. The installed folder stays.
    public static void CleanStages(string installFolder)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(installFolder));
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(parent, StagePrefix + "*"))
                if (!IsInside(dir, installFolder)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* still in use: next time */ }
        catch (UnauthorizedAccessException) { }
    }

    // A step the owner would stop with "Stop the crew": a sub-task whose status is RUNNING.
    public static bool StepIsRunning(JsonNode? snapshot) =>
        Projects(snapshot).Any(project => SubTasks(project).Any(step => Status(step) == "RUNNING"));

    // Projects whose running work must be checkpointed the same way as Stop the crew, before the app quits.
    public static IReadOnlyList<string> ProjectsToCheckpoint(JsonNode? snapshot)
    {
        var ids = new List<string>();
        foreach (var project in Projects(snapshot))
        {
            var id = Str(project["id"]);
            if (id.Length == 0) continue;
            if (Bool(project["canStop"]) || SubTasks(project).Any(step => Status(step) == "RUNNING")) ids.Add(id);
        }
        return ids;
    }

    // Refuses the update while a step is running unless the owner has confirmed.
    public static bool MayUpdate(bool stepRunning, bool confirmed) => !stepRunning || confirmed;

    // Checks, downloads and stages a newer release, then asks the caller to checkpoint and launch.
    // The installed folder is replaced only later, by the new copy's --finish-update.
    public static async Task RunAsync(HttpClient http, string installFolder, string currentVersion, JsonNode? snapshot, bool confirmed, Func<IReadOnlyList<string>, Task> checkpoint, Action<string, string> launch, Action<string>? phase = null, CancellationToken cancel = default)
    {
        if (!MayUpdate(StepIsRunning(snapshot), confirmed)) throw new InvalidOperationException(NeedsConfirmMessage);
        phase?.Invoke("downloading");
        NativeRelease? release;
        try { release = await CheckAsync(http, cancel); }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(OfflineMessage, ex);
        }
        if (release == null) throw new InvalidOperationException(MissingAssetMessage);
        if (!Newer(release.Tag, currentVersion)) throw new InvalidOperationException(Strings.T("This release is not newer than the installed Planwharf. Nothing was changed."));
        var download = Path.Combine(Path.GetTempPath(), "planwharf-update-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(installFolder)) ?? "", StagePrefix + release.Tag);
        try
        {
            var zip = await DownloadAsync(http, release, download, cancel);
            var exe = Stage(zip, installFolder, release.Tag);
            phase?.Invoke("checkpointing");
            try { await checkpoint(ProjectsToCheckpoint(snapshot)); }
            catch (Exception ex)
            {
                TryDeleteDirectory(stage);
                throw new InvalidOperationException(CheckpointMessage, ex);
            }
            phase?.Invoke("restarting");
            launch(exe, Path.GetFullPath(installFolder));
        }
        finally { TryDeleteDirectory(download); }
    }

    public static HttpClient SharedHttp => Http;

    static void CopyFile(string from, string to)
    {
        var parent = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.Copy(from, to, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
    }

    static bool IsInside(string parent, string child)
    {
        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, compare);
    }

    static IEnumerable<JsonObject> Projects(JsonNode? snapshot) =>
        (snapshot?["projects"] as JsonArray ?? []).OfType<JsonObject>();

    static IEnumerable<JsonObject> SubTasks(JsonObject project) =>
        (project["subTasks"] as JsonArray ?? []).OfType<JsonObject>();

    static string Status(JsonObject node) => Str(node["status"]).ToUpperInvariant();

    static void TryDeleteFile(string file) { try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
