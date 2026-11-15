using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

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
    const string StagePrefix = ".planwharf-update-";

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
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")) return null;
        var page = Str(release["html_url"]);
        if (page != $"https://github.com/{Repo}/releases/tag/{tag}") return null;
        var name = $"Planwharf-native-{rid}-{tag}.zip";
        var asset = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => Str(item["name"]) == name);
        if (asset == null) return null;
        var url = Str(asset["browser_download_url"]);
        var digest = Str(asset["digest"]);
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal) || !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$")) return null;
        return new NativeRelease(tag, page, name, url, digest["sha256:".Length..]);
    }

    // True when the release is newer than the running version (both as vX.Y.Z or X.Y.Z).
    public static bool Newer(string tag, string current)
    {
        static int[]? Parts(string text) => text.TrimStart('v').Split('.') is { Length: 3 } parts && parts.All(part => int.TryParse(part, out _)) ? parts.Select(int.Parse).ToArray() : null;
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

    // Downloads the zip and checks it: a file that does not match GitHub's digest is deleted, never used.
    public static async Task<string> DownloadAsync(HttpClient http, NativeRelease release, string folder, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, release.AssetName);
        using (var response = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var target = File.Create(file);
            await response.Content.CopyToAsync(target, cancel);
        }
        if (!Verify(file, release.Sha256)) { File.Delete(file); throw new InvalidOperationException("The downloaded update does not match the release's checksum. Nothing was changed."); }
        return file;
    }

    public static bool Verify(string file, string sha256)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Unpacks the zip next to the installed folder; returns the new executable.
    public static string Stage(string zip, string installFolder, string tag)
    {
        var stage = Path.Combine(Path.GetDirectoryName(installFolder)!, StagePrefix + tag);
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        ZipFile.ExtractToDirectory(zip, stage);
        // The zip holds one folder named after the release.
        var root = Directory.GetDirectories(stage) is [var only] && Directory.GetFiles(stage).Length == 0 ? only : stage;
        var exe = Path.Combine(root, OperatingSystem.IsWindows() ? "Planwharf.exe" : "Planwharf");
        if (!File.Exists(exe)) throw new InvalidOperationException("The update does not contain the Planwharf program.");
        return exe;
    }

    // Starts the new copy, which finishes the update once this process has closed.
    public static void Launch(string stagedExe, string installFolder)
    {
        var start = new ProcessStartInfo(stagedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stagedExe)! };
        start.ArgumentList.Add(FinishFlag);
        start.ArgumentList.Add(installFolder);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(start);
    }

    // Run by the new copy: waits for the old Planwharf to close, copies itself over the installed folder
    // (retrying while a file is still in use), then starts the installed Planwharf again.
    public static int Finish(string installFolder, int oldPid, TimeSpan? wait = null)
    {
        try { using var old = Process.GetProcessById(oldPid); old.WaitForExit((int)(wait ?? TimeSpan.FromSeconds(60)).TotalMilliseconds); }
        catch (ArgumentException) { /* already closed */ }
        var from = InstallFolder;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try { CopyTree(from, installFolder); break; }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(500); }
        }
        var exe = Path.Combine(installFolder, OperatingSystem.IsWindows() ? "Planwharf.exe" : "Planwharf");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installFolder });
        return 0;
    }

    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    // Folders left by an earlier update are removed at the next start.
    public static void CleanStages(string installFolder)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(installFolder)!, StagePrefix + "*"))
                if (!installFolder.StartsWith(dir, StringComparison.Ordinal)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* still in use: next time */ }
        catch (UnauthorizedAccessException) { }
    }

    static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
