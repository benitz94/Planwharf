using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Port of the standalone cases in engine/browser-settings.test.cjs.
//
// Ported (module-alone pieces of the JavaScript tests, plus detect/resolve/validate
// behaviour that those tests only reach through the server or planwharf.cjs):
//  - "Firefox launch uses an isolated Firefox profile; Chromium browsers retain
//    app mode": browserArguments only. openWindow/spawn is deferred.
//  - "browser choice persists through HTTP and rejects missing executables":
//    validateBrowser relative/unknown/missing path, readBrowserPreference missing
//    file and saved JSON, and that an invalid custom path does not rewrite the
//    preference file. HTTP POST/GET /api/settings/browser is deferred.
//
// Deferred (need server.cjs, planwharf.cjs, or a launched window):
//  - POST /api/settings/browser 200 for default and custom, GET selected, and
//    POST 400 for a missing executable while keeping the saved file (createServer
//    and writePlan in server.cjs).
//  - parseArgs(['node','planwharf.cjs','--browser','firefox']) (planwharf.cjs).
//  - openWindow spawn of fixture-firefox with Firefox arguments, profileDir, and
//    no --app= (planwharf.cjs). Chromium --no-sandbox test args are also there.
//
// Differences from the JavaScript:
//  - Errors are InvalidOperationException; JavaScript throws Error (or TypeError
//    when resolveBrowser/browserArguments read null). User-facing messages match.
//  - resolveBrowser(null) and browserArguments(null, ...) do not throw TypeError.
//    resolveBrowser(null) uses the missing-id path ("Saved browser is unavailable…
//    "). browserArguments treats a missing family as Chromium arguments, matching
//    a plain object whose family is undefined.
//  - detectBrowsers returns DetectedBrowser records; JavaScript returns plain
//    objects. JSON field names stay id/label/path/family.
//  - Optional executable and env arguments are C# test hooks.
//  - Mac candidate strings on Windows may differ from node:path.join because
//    Path.Combine keeps a leading '/'. Those paths are not expected to exist.
//  - File.Exists stands in for fs.statSync(file).isFile().
public sealed class BrowserSettingsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pw-browser-settings-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { /* best effort */ } }

    static bool Windows => OperatingSystem.IsWindows();
    string Exe(string name) => Path.Combine(dir, name + (Windows ? ".exe" : ""));
    static JsonObject Obj(params (string key, JsonNode? value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields) obj[key] = value;
        return obj;
    }

    Dictionary<string, string?> IsolatedEnv(string path) => new()
    {
        ["PATH"] = path,
        ["LOCALAPPDATA"] = Path.Combine(dir, "no-local"),
        ["ProgramFiles"] = Path.Combine(dir, "no-program"),
        ["ProgramFiles(x86)"] = Path.Combine(dir, "no-program86")
    };

    [Fact]
    public void Firefox_launch_uses_an_isolated_Firefox_profile_Chromium_browsers_retain_app_mode()
    {
        const string url = "http://127.0.0.1:4173/";
        var firefox = BrowserSettings.BrowserArguments(Obj(("path", "fixture-firefox"), ("family", "firefox")), url, dir);
        Assert.Equal(["--no-remote", "--profile", dir, "--new-window", url], firefox);
        Assert.DoesNotContain(firefox, arg => arg.StartsWith("--app=", StringComparison.Ordinal));
        foreach (var id in new[] { "edge", "chrome", "brave", "opera", "chromium" })
        {
            var args = BrowserSettings.BrowserArguments(Obj(("id", id), ("family", "chromium")), url, dir);
            Assert.Contains("--app=" + url, args);
            Assert.Contains("--user-data-dir=" + dir, args);
        }
    }

    [Fact]
    public void Browser_choice_persists_through_HTTP_and_rejects_missing_executables()
    {
        var relative = Assert.Throws<InvalidOperationException>(() =>
            BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", "relative.exe"), ("family", "firefox"))));
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.", relative.Message);
        var unknown = Assert.Throws<InvalidOperationException>(() => BrowserSettings.ValidateBrowser(Obj(("id", "unknown"))));
        Assert.Equal("Invalid browser selection.", unknown.Message);

        var planFile = Path.Combine(dir, "notebook.json");
        Assert.True(JsonNode.DeepEquals(Obj(("id", "auto")), BrowserSettings.ReadBrowserPreference(planFile)));
        var customPath = Exe("installed");
        File.WriteAllBytes(customPath, [0]);
        var custom = Obj(("id", "custom"), ("path", customPath), ("family", "firefox"));
        File.WriteAllText(planFile + ".browser.json", custom.ToJsonString());
        Assert.True(JsonNode.DeepEquals(custom, BrowserSettings.ReadBrowserPreference(planFile)));
        var missing = Path.Combine(dir, "missing" + (Windows ? ".exe" : ""));
        var gone = Assert.Throws<InvalidOperationException>(() =>
            BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", missing), ("family", "firefox"))));
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.", gone.Message);
        Assert.True(JsonNode.DeepEquals(custom, BrowserSettings.ReadBrowserPreference(planFile)), "Invalid choices preserve the saved browser");
    }

    [Fact]
    public void ValidateBrowser_accepts_auto_default_and_an_installed_id_and_strips_optional_keys()
    {
        Assert.True(JsonNode.DeepEquals(Obj(("id", "auto")), BrowserSettings.ValidateBrowser(Obj(("id", "auto"), ("path", "x"), ("family", "chromium")))));
        Assert.True(JsonNode.DeepEquals(Obj(("id", "default")), BrowserSettings.ValidateBrowser(Obj(("id", "default")))));
        var extra = Assert.Throws<InvalidOperationException>(() => BrowserSettings.ValidateBrowser(Obj(("id", "auto"), ("other", "1"))));
        Assert.Equal("Invalid browser selection.", extra.Message);
        Assert.Equal("Invalid browser selection.", Assert.Throws<InvalidOperationException>(() => BrowserSettings.ValidateBrowser(null)).Message);
        Assert.Equal("Invalid browser selection.", Assert.Throws<InvalidOperationException>(() => BrowserSettings.ValidateBrowser(new JsonArray())).Message);

        var firefox = Exe("firefox");
        File.WriteAllBytes(firefox, [0]);
        var env = IsolatedEnv(dir);
        bool Ours(string file) => string.Equals(file, firefox, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(Obj(("id", "firefox")), BrowserSettings.ValidateBrowser(Obj(("id", "firefox")), Ours, env)));
        var missing = Assert.Throws<InvalidOperationException>(() => BrowserSettings.ValidateBrowser(Obj(("id", "chrome")), Ours, env));
        Assert.Equal("The selected browser is not installed. Choose a custom executable path.", missing.Message);

        var custom = BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", firefox), ("family", "firefox")), Ours, env);
        Assert.True(JsonNode.DeepEquals(Obj(("id", "custom"), ("path", firefox), ("family", "firefox")), custom));
        var family = Assert.Throws<InvalidOperationException>(() =>
            BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", firefox), ("family", "safari")), Ours, env));
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.", family.Message);
        if (Windows)
        {
            var noExt = Path.Combine(dir, "browser.com");
            File.WriteAllBytes(noExt, [0]);
            Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
                Assert.Throws<InvalidOperationException>(() =>
                    BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", noExt), ("family", "chromium")), _ => true, env)).Message);
        }
        var tooLong = (Windows ? @"C:\" : "/") + new string('a', 1000);
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
            Assert.Throws<InvalidOperationException>(() =>
                BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", tooLong), ("family", "chromium")), _ => true)).Message);
    }

    [Fact]
    public void ReadBrowserPreference_keeps_unknown_fields_and_rethrows_non_missing_errors()
    {
        var planFile = Path.Combine(dir, "notebook.json");
        File.WriteAllText(planFile + ".browser.json", """{"id":"default","unknown":true}""");
        var loaded = BrowserSettings.ReadBrowserPreference(planFile)!.AsObject();
        Assert.Equal("default", loaded["id"]!.GetValue<string>());
        Assert.True(loaded["unknown"]!.GetValue<bool>());
        File.WriteAllText(planFile + ".browser.json", "{");
        Assert.ThrowsAny<Exception>(() => BrowserSettings.ReadBrowserPreference(planFile));
        File.Delete(planFile + ".browser.json");
        Directory.CreateDirectory(planFile + ".browser.json");
        Assert.ThrowsAny<Exception>(() => BrowserSettings.ReadBrowserPreference(planFile));
        var bom = Path.Combine(dir, "bom.json");
        File.WriteAllBytes(bom + ".browser.json", [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);
        Assert.ThrowsAny<Exception>(() => BrowserSettings.ReadBrowserPreference(bom));
    }

    [Fact]
    public void ResolveBrowser_maps_catalog_entries_and_custom_paths()
    {
        Assert.Null(BrowserSettings.ResolveBrowser(Obj(("id", "default"))));
        var firefox = Exe("firefox");
        File.WriteAllBytes(firefox, [0]);
        var env = IsolatedEnv(dir);
        bool Ours(string file) => string.Equals(file, firefox, StringComparison.Ordinal);
        var auto = BrowserSettings.ResolveBrowser(Obj(("id", "auto")), Ours, env)!;
        Assert.Equal("firefox", auto["id"]!.GetValue<string>());
        Assert.Equal("Mozilla Firefox", auto["label"]!.GetValue<string>());
        Assert.Equal(firefox, auto["path"]!.GetValue<string>());
        Assert.Equal("firefox", auto["family"]!.GetValue<string>());
        var named = BrowserSettings.ResolveBrowser(Obj(("id", "firefox")), Ours, env)!;
        Assert.Equal("firefox", named["family"]!.GetValue<string>());
        var custom = BrowserSettings.ResolveBrowser(Obj(("id", "custom"), ("path", firefox), ("family", "chromium")), Ours, env)!;
        Assert.True(JsonNode.DeepEquals(Obj(("id", "custom"), ("path", firefox), ("family", "chromium")), custom));
        var unavailable = Assert.Throws<InvalidOperationException>(() => BrowserSettings.ResolveBrowser(Obj(("id", "chrome")), Ours, env));
        Assert.Equal("Saved browser is unavailable. Use --browser default or choose another executable.", unavailable.Message);
        var chrome = Exe("google-chrome");
        File.WriteAllBytes(chrome, [0]);
        bool ChromeOnly(string file) => string.Equals(file, chrome, StringComparison.Ordinal);
        var chromium = BrowserSettings.ResolveBrowser(Obj(("id", "chrome")), ChromeOnly, env)!;
        Assert.Equal("chromium", chromium["family"]!.GetValue<string>());
        Assert.Equal("Google Chrome", chromium["label"]!.GetValue<string>());
        var asDir = Path.Combine(dir, "folder" + (Windows ? ".exe" : ""));
        Directory.CreateDirectory(asDir);
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
            Assert.Throws<InvalidOperationException>(() =>
                BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", asDir), ("family", "chromium")))).Message);
        Assert.Null(BrowserSettings.ResolveBrowser(Obj(("id", "auto")), _ => false, env));
        var catalogs = BrowserSettings.DetectBrowsers(_ => false, env);
        Assert.Equal(new[] { "edge", "chrome", "brave", "opera", "firefox", "chromium" }, catalogs.Select(b => b.Id));
        Assert.Equal(new[] { "Microsoft Edge", "Google Chrome", "Brave", "Opera", "Mozilla Firefox", "Chromium" }, catalogs.Select(b => b.Label));
        Assert.All(catalogs, b => Assert.Null(b.Path));
        Assert.Equal(firefox, BrowserSettings.DetectBrowsers(Ours, env).Single(b => b.Id == "firefox").Path);
    }

    [Fact]
    public void BrowserArguments_copies_the_javascript_chromium_vector_exactly()
    {
        const string url = "http://127.0.0.1:4173/";
        Assert.Equal(
            ["--no-first-run", "--no-default-browser-check", "--start-fullscreen", "--disable-background-mode", "--app=" + url, "--user-data-dir=" + dir],
            BrowserSettings.BrowserArguments(Obj(("family", "chromium")), url, dir));
        // family comparison is case-sensitive, as in JavaScript (`=== 'firefox'`).
        Assert.Equal(
            ["--no-first-run", "--no-default-browser-check", "--start-fullscreen", "--disable-background-mode", "--app=" + url, "--user-data-dir=" + dir],
            BrowserSettings.BrowserArguments(Obj(("family", "Firefox")), url, dir));
        Assert.Equal(
            ["--no-first-run", "--no-default-browser-check", "--start-fullscreen", "--disable-background-mode", "--app=" + url, "--user-data-dir=" + dir],
            BrowserSettings.BrowserArguments(null, url, dir));
    }

    [Fact]
    public void ValidateBrowser_rejects_empty_id_extra_custom_keys_and_non_string_fields()
    {
        Assert.Equal("Invalid browser selection.",
            Assert.Throws<InvalidOperationException>(() => BrowserSettings.ValidateBrowser(Obj(("id", "")))).Message);
        Assert.Equal("Invalid browser selection.",
            Assert.Throws<InvalidOperationException>(() =>
                BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", Exe("x")), ("family", "firefox"), ("label", "x")))).Message);
        var input = Obj(("id", "auto"), ("path", "kept"));
        Assert.True(JsonNode.DeepEquals(Obj(("id", "auto")), BrowserSettings.ValidateBrowser(input)));
        Assert.Equal("kept", input["path"]!.GetValue<string>());
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
            Assert.Throws<InvalidOperationException>(() =>
                BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", JsonValue.Create(1)), ("family", "firefox")))).Message);
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
            Assert.Throws<InvalidOperationException>(() =>
                BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", Exe("x")), ("family", JsonValue.Create((string?)null))))).Message);
        if (Windows)
        {
            Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
                Assert.Throws<InvalidOperationException>(() =>
                    BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", @"C:relative.exe"), ("family", "firefox")), _ => true)).Message);
        }
        var limit = Windows
            ? @"C:\" + new string('a', 993) + ".exe"
            : "/" + new string('a', 999);
        Assert.Equal(1000, limit.Length);
        var accepted = BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", limit), ("family", "chromium")), _ => true);
        Assert.True(JsonNode.DeepEquals(Obj(("id", "custom"), ("path", limit), ("family", "chromium")), accepted));
        var over = Windows ? @"C:\" + new string('a', 994) + ".exe" : limit + "x";
        Assert.Equal("Choose an existing absolute browser executable path and its browser family.",
            Assert.Throws<InvalidOperationException>(() =>
                BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", over), ("family", "chromium")), _ => true)).Message);
        if (Windows)
        {
            var upper = Path.Combine(dir, "Browser.EXE");
            File.WriteAllBytes(upper, [0]);
            var custom = BrowserSettings.ValidateBrowser(Obj(("id", "custom"), ("path", upper), ("family", "chromium")), _ => true);
            Assert.Equal(upper, custom["path"]!.GetValue<string>());
        }
    }

    [Fact]
    public void DetectBrowsers_prefers_catalog_order_and_ProgramFiles_over_PATH()
    {
        var program = Path.Combine(dir, "Program Files");
        var pathDir = Path.Combine(dir, "bin");
        Directory.CreateDirectory(Path.Combine(program, "Mozilla Firefox"));
        Directory.CreateDirectory(Path.Combine(program, "Google", "Chrome", "Application"));
        Directory.CreateDirectory(pathDir);
        var programFirefox = Path.Combine(program, "Mozilla Firefox", "firefox.exe");
        var pathFirefox = Path.Combine(pathDir, "firefox" + (Windows ? ".exe" : ""));
        var pathChrome = Path.Combine(pathDir, (Windows ? "google-chrome.exe" : "google-chrome"));
        File.WriteAllBytes(programFirefox, [0]);
        File.WriteAllBytes(pathFirefox, [0]);
        File.WriteAllBytes(pathChrome, [0]);
        var env = IsolatedEnv(pathDir);
        env["ProgramFiles"] = program;
        env["ProgramFiles(x86)"] = Path.Combine(dir, "no-program86");
        env["LOCALAPPDATA"] = Path.Combine(dir, "no-local");

        // Only the files of this test count: a browser really installed on the machine (Chrome in
        // /Applications on the macOS runners) must not win over the ones the test made.
        bool Here(string file) => file.StartsWith(dir, StringComparison.Ordinal) && File.Exists(file);
        var found = BrowserSettings.DetectBrowsers(Here, env);
        Assert.Equal(programFirefox, found.Single(b => b.Id == "firefox").Path);
        Assert.Equal(pathChrome, found.Single(b => b.Id == "chrome").Path);
        Assert.Null(found.Single(b => b.Id == "edge").Path);

        var auto = BrowserSettings.ResolveBrowser(Obj(("id", "auto")), Here, env)!;
        Assert.Equal("chrome", auto["id"]!.GetValue<string>());
        Assert.Equal("chromium", auto["family"]!.GetValue<string>());

        var extra = Obj(("id", "default"), ("other", "1"));
        Assert.Null(BrowserSettings.ResolveBrowser(extra, Here, env));
        Assert.Equal("Saved browser is unavailable. Use --browser default or choose another executable.",
            Assert.Throws<InvalidOperationException>(() => BrowserSettings.ResolveBrowser(null)).Message);

        var emptyPath = IsolatedEnv("");
        emptyPath["ProgramFiles"] = Path.Combine(dir, "no-program");
        emptyPath["ProgramFiles(x86)"] = Path.Combine(dir, "no-program86");
        emptyPath["LOCALAPPDATA"] = Path.Combine(dir, "no-local");
        var opera = new List<string>();
        BrowserSettings.DetectBrowsers(file =>
        {
            if (file.Contains("Opera", StringComparison.OrdinalIgnoreCase) || file.EndsWith("opera.exe", StringComparison.Ordinal) || file.EndsWith("opera", StringComparison.Ordinal))
                opera.Add(file);
            return false;
        }, emptyPath);
        var roots = new[] { emptyPath["ProgramFiles"]!, emptyPath["ProgramFiles(x86)"]!, emptyPath["LOCALAPPDATA"]! };
        var relatives = new[] { Path.Combine("Programs", "Opera", "launcher.exe"), Path.Combine("Programs", "Opera", "opera.exe"), Path.Combine("Opera", "launcher.exe"), Path.Combine("Opera", "opera.exe") };
        var expectedWindows = relatives.SelectMany(relative => roots.Select(root => Path.Combine(root, relative))).ToArray();
        Assert.Equal(expectedWindows, opera.Take(expectedWindows.Length));
    }

    [Fact]
    public void ReadBrowserPreference_parses_surrounding_whitespace_and_does_not_validate()
    {
        var planFile = Path.Combine(dir, "notebook.json");
        File.WriteAllText(planFile + ".browser.json", "  {\"id\":\"unknown\",\"keep\":1}  ");
        var loaded = BrowserSettings.ReadBrowserPreference(planFile)!.AsObject();
        Assert.Equal("unknown", loaded["id"]!.GetValue<string>());
        Assert.Equal(1, loaded["keep"]!.GetValue<int>());
    }
}
