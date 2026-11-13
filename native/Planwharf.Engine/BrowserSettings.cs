// Port of engine/browser-settings.cjs.
using System.Text;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class BrowserSettings
{
    public sealed record DetectedBrowser(string Id, string Label, string? Path);

    static readonly string[] NamesOrder = ["edge", "chrome", "brave", "opera", "firefox", "chromium"];
    static readonly Dictionary<string, string> Names = new()
    {
        ["edge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["firefox"] = "Mozilla Firefox",
        ["chromium"] = "Chromium"
    };
    static readonly Dictionary<string, string[]> WindowsRelative = new()
    {
        ["edge"] = ["Microsoft/Edge/Application/msedge.exe"],
        ["chrome"] = ["Google/Chrome/Application/chrome.exe"],
        ["brave"] = ["BraveSoftware/Brave-Browser/Application/brave.exe"],
        ["opera"] = ["Programs/Opera/launcher.exe", "Programs/Opera/opera.exe", "Opera/launcher.exe", "Opera/opera.exe"],
        ["firefox"] = ["Mozilla Firefox/firefox.exe"],
        ["chromium"] = ["Chromium/Application/chrome.exe"]
    };
    static readonly Dictionary<string, string[]> Commands = new()
    {
        ["edge"] = ["msedge", "microsoft-edge", "microsoft-edge-stable"],
        ["chrome"] = ["google-chrome", "google-chrome-stable"],
        ["brave"] = ["brave-browser", "brave"],
        ["opera"] = ["opera"],
        ["firefox"] = ["firefox"],
        ["chromium"] = ["chromium", "chromium-browser"]
    };
    static readonly Dictionary<string, string> Mac = new()
    {
        ["edge"] = "Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        ["chrome"] = "Google Chrome.app/Contents/MacOS/Google Chrome",
        ["brave"] = "Brave Browser.app/Contents/MacOS/Brave Browser",
        ["opera"] = "Opera.app/Contents/MacOS/Opera",
        ["firefox"] = "Firefox.app/Contents/MacOS/firefox",
        ["chromium"] = "Chromium.app/Contents/MacOS/Chromium"
    };
    static readonly HashSet<string> AllowedIds = ["auto", "default", "custom", "edge", "chrome", "brave", "opera", "firefox", "chromium"];

    // Optional executable and env are C# test hooks. Omitted, they follow process.env and fs.statSync.
    // HTTP save (POST /api/settings/browser) and window launch (openWindow) live in server.cjs
    // and planwharf.cjs; this module only detects, validates, reads, resolves, and builds arguments.
    public static DetectedBrowser[] DetectBrowsers(Func<string, bool>? executable = null, IReadOnlyDictionary<string, string?>? env = null)
    {
        var isFile = executable ?? IsFile;
        return NamesOrder.Select(id => new DetectedBrowser(id, Names[id], BrowserCandidates(id, env).FirstOrDefault(isFile))).ToArray();
    }

    public static JsonObject ValidateBrowser(JsonNode? value, Func<string, bool>? executable = null, IReadOnlyDictionary<string, string?>? env = null)
    {
        if (value is not JsonObject obj || obj.Any(pair => pair.Key is not ("id" or "path" or "family")) || Text(obj, "id") is not { } id || !AllowedIds.Contains(id))
            throw new InvalidOperationException("Invalid browser selection.");
        if (id == "custom")
        {
            if (obj["path"] is not JsonValue pathValue || !pathValue.TryGetValue<string>(out var path) ||
                path.Length > 1000 || !IsAbsolute(path) || !(executable ?? IsFile)(path) ||
                (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) ||
                obj["family"] is not JsonValue familyValue || !familyValue.TryGetValue<string>(out var family) ||
                family is not ("chromium" or "firefox"))
                throw new InvalidOperationException("Choose an existing absolute browser executable path and its browser family.");
            return new JsonObject { ["id"] = "custom", ["path"] = path, ["family"] = family };
        }
        if (id is not ("auto" or "default") && string.IsNullOrEmpty(DetectBrowsers(executable, env).FirstOrDefault(b => b.Id == id)?.Path))
            throw new InvalidOperationException("The selected browser is not installed. Choose a custom executable path.");
        return new JsonObject { ["id"] = id };
    }

    public static JsonNode? ReadBrowserPreference(string planFile)
    {
        try { return JsonNode.Parse(Encoding.UTF8.GetString(File.ReadAllBytes(planFile + ".browser.json"))); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JsonObject { ["id"] = "auto" };
        }
    }

    public static JsonObject? ResolveBrowser(JsonNode? value, Func<string, bool>? executable = null, IReadOnlyDictionary<string, string?>? env = null)
    {
        var id = Text(value, "id");
        if (id == "default") return null;
        if (id == "custom") return ValidateBrowser(value, executable, env);
        var catalog = DetectBrowsers(executable, env);
        var browser = id == "auto"
            ? catalog.FirstOrDefault(b => !string.IsNullOrEmpty(b.Path))
            : catalog.FirstOrDefault(b => b.Id == id && !string.IsNullOrEmpty(b.Path));
        if (browser is null && id != "auto")
            throw new InvalidOperationException("Saved browser is unavailable. Use --browser default or choose another executable.");
        if (browser is null) return null;
        return new JsonObject
        {
            ["id"] = browser.Id,
            ["label"] = browser.Label,
            ["path"] = browser.Path,
            ["family"] = browser.Id == "firefox" ? "firefox" : "chromium"
        };
    }

    public static string[] BrowserArguments(JsonNode? browser, string url, string profileDir)
    {
        // JavaScript throws TypeError when browser is null or not an object. C# treats a missing
        // family as Chromium arguments, matching `browser.family === 'firefox'` for a plain object.
        var family = Text(browser, "family");
        return family == "firefox"
            ? ["--no-remote", "--profile", profileDir, "--new-window", url]
            : ["--no-first-run", "--no-default-browser-check", "--start-fullscreen", "--disable-background-mode", "--app=" + url, "--user-data-dir=" + profileDir];
    }

    static string? Text(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool IsFile(string file)
    {
        try { return File.Exists(file); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Node path.isAbsolute: a drive-relative path such as C:foo is not absolute; a leading slash is.
    static bool IsAbsolute(string path)
    {
        if (path.Length == 0) return false;
        if (!OperatingSystem.IsWindows()) return path[0] == '/';
        if (path[0] is '/' or '\\') return true;
        return path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';
    }

    static string? ReadEnv(IReadOnlyDictionary<string, string?>? env, string name)
    {
        if (env is not null) return env.TryGetValue(name, out var value) && value is { Length: > 0 } ? value : null;
        var found = Environment.GetEnvironmentVariable(name);
        return found is { Length: > 0 } ? found : null;
    }

    static string Join(params string[] parts)
    {
        var acc = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            var next = OperatingSystem.IsWindows() ? parts[i].Replace('/', '\\') : parts[i];
            acc = Path.Combine(acc, next);
        }
        return acc;
    }

    static IEnumerable<string> BrowserCandidates(string id, IReadOnlyDictionary<string, string?>? env)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = ReadEnv(env, "LOCALAPPDATA") ?? Join(home, "AppData", "Local");
        var program = ReadEnv(env, "ProgramFiles") ?? @"C:\Program Files";
        var program86 = ReadEnv(env, "ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        foreach (var relative in WindowsRelative[id])
            foreach (var root in new[] { program, program86, local })
                yield return Join(root, relative);
        yield return Join("/Applications", Mac[id]);
        yield return Join(home, "Applications", Mac[id]);
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var root in (ReadEnv(env, "PATH") ?? "").Split(Path.PathSeparator))
        {
            if (root.Length == 0) continue;
            foreach (var command in Commands[id])
                yield return Join(root, command + exe);
        }
    }
}
