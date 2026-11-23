// Port of engine/phone-activation.cjs.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public record ActivationAssessment(
    bool Allowed,
    string Model,
    IReadOnlyList<JsonObject> InstalledListeners,
    string Blocker);

public record BackupResult(string Destination, IReadOnlyList<JsonObject> Files);

public record LiveExchangeResult(bool Ran, bool Ok = false, int Updates = 0, string? Reason = null);

public static class PhoneActivation
{
    public const string DefaultOllamaModel = "qwen2.5:3b";
    public const string DefaultOllamaUrl = "http://127.0.0.1:11434";

    public static bool SamePath(string? left, string? right)
    {
        static string Normalize(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            try
            {
                return Path.GetFullPath(raw).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            }
            catch
            {
                return raw.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            }
        }

        var a = Normalize(left);
        var b = Normalize(right);
        return !string.IsNullOrEmpty(a) && a == b;
    }

    public static JsonObject IsolatedPhoneSettings()
    {
        return new JsonObject
        {
            ["ntfy"] = new JsonObject
            {
                ["enabled"] = false,
                ["topic"] = "",
                ["serverUrl"] = "https://ntfy.sh"
            },
            ["telegram"] = new JsonObject
            {
                ["enabled"] = false,
                ["startCode"] = "isolated-validation",
                ["pairedChatId"] = null,
                ["pairedUsername"] = null,
                ["updateOffset"] = 0
            },
            ["ollama"] = new JsonObject
            {
                ["enabled"] = true,
                ["baseUrl"] = DefaultOllamaUrl,
                ["model"] = DefaultOllamaModel
            }
        };
    }

    public static IReadOnlyList<string> BackupNames() =>
        new[] { "phone-settings.json", "telegram-bot.token", "plan.json" };

    public static ActivationAssessment AssessPairedActivation(JsonObject? options = null)
    {
        options ??= new JsonObject();
        var list = new List<JsonObject>();
        if (options.TryGetPropertyValue("listeners", out var listenersNode) && listenersNode != null)
        {
            if (listenersNode is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject obj) list.Add(obj);
                }
            }
            else if (listenersNode is JsonObject single)
            {
                list.Add(single);
            }
        }

        var thisBuildPath = options["thisBuildPath"]?.ToString() ?? string.Empty;
        var tokenPresent = options["tokenPresent"]?.GetValue<bool>() == true;
        var isolatedHasToken = options["isolatedHasToken"]?.GetValue<bool>() == true;
        var modelRaw = options["model"]?.ToString();
        var model = !string.IsNullOrWhiteSpace(modelRaw) ? modelRaw.Trim() : DefaultOllamaModel;

        var installed = new List<JsonObject>();
        foreach (var item in list)
        {
            var running = item["running"]?.GetValue<bool>() == true;
            var pollsTelegram = item["pollsTelegram"]?.GetValue<bool>() == true;
            var path = item["path"]?.ToString();
            if (running && pollsTelegram && !SamePath(path, thisBuildPath))
            {
                int? pid = null;
                if (item.TryGetPropertyValue("pid", out var pidNode) && pidNode is JsonValue val && val.TryGetValue<int>(out var p))
                {
                    pid = p;
                }
                var obj = new JsonObject
                {
                    ["path"] = path ?? string.Empty,
                    ["pid"] = pid
                };
                installed.Add(obj);
            }
        }

        var reasons = new List<string>();
        if (!tokenPresent)
        {
            reasons.Add("No bot token is available, so a live Telegram exchange cannot start.");
        }
        if (installed.Count > 0)
        {
            var where = string.Join("; ", installed.Select(item =>
            {
                var p = item["path"]?.ToString();
                if (!string.IsNullOrEmpty(p)) return p;
                return $"pid {item["pid"]}";
            }));
            reasons.Add($"The installed Planwharf listener is already polling Telegram ({where}). A second getUpdates call would compete with it.");
        }
        if (isolatedHasToken)
        {
            reasons.Add("The isolated validation notebook contains a bot token. Remove it before starting that notebook so it cannot poll.");
        }

        return new ActivationAssessment(
            Allowed: reasons.Count == 0,
            Model: model,
            InstalledListeners: installed,
            Blocker: string.Join(" ", reasons));
    }

    public static (string Root, string Attempts, string Plan) WriteIsolatedNotebook(string dir, string starterFile)
    {
        var root = Path.GetFullPath(dir);
        var attempts = Path.Combine(root, ".attempts-plan");
        Directory.CreateDirectory(attempts);
        File.Copy(starterFile, Path.Combine(root, "plan.json"), overwrite: true);
        File.WriteAllText(Path.Combine(attempts, "phone-settings.json"), IsolatedPhoneSettings().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var token = Path.Combine(attempts, "telegram-bot.token");
        if (File.Exists(token)) File.Delete(token);
        return (root, attempts, Path.Combine(root, "plan.json"));
    }

    public static BackupResult CopyBackup(string fromDir, string toDir, string? attemptsDir = null)
    {
        attemptsDir ??= fromDir;
        var destination = Path.GetFullPath(toDir);
        Directory.CreateDirectory(destination);
        var locations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan.json"] = Path.Combine(fromDir, "plan.json"),
            ["phone-settings.json"] = Path.Combine(attemptsDir, "phone-settings.json"),
            ["telegram-bot.token"] = Path.Combine(attemptsDir, "telegram-bot.token")
        };

        string? ResolveSource(string name)
        {
            if (locations.TryGetValue(name, out var primary) && File.Exists(primary)) return primary;
            var fallbackAttempt = Path.Combine(fromDir, ".attempts-plan", name);
            if (File.Exists(fallbackAttempt)) return fallbackAttempt;
            var fallbackRoot = Path.Combine(fromDir, name);
            if (File.Exists(fallbackRoot)) return fallbackRoot;
            return null;
        }

        var files = new List<JsonObject>();
        foreach (var name in BackupNames())
        {
            var source = ResolveSource(name);
            if (source == null) continue;
            var target = Path.Combine(destination, name);
            File.Copy(source, target, overwrite: true);
            var bytes = File.ReadAllBytes(source);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var fileObj = new JsonObject
            {
                ["name"] = name,
                ["sha256"] = sha256,
                ["bytes"] = bytes.Length
            };
            files.Add(fileObj);
        }

        var manifestObj = new JsonObject
        {
            ["files"] = new JsonArray(files.Select(f => (JsonNode)f.DeepClone()).ToArray())
        };
        File.WriteAllText(Path.Combine(destination, "manifest.json"), manifestObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new BackupResult(destination, files);
    }

    public static async Task<LiveExchangeResult> LiveExchangeAsync(JsonObject options, Func<string, Task<HttpResponseMessage>>? fetchImpl = null)
    {
        var allowed = options["allowed"]?.GetValue<bool>() == true;
        if (!allowed) return new LiveExchangeResult(false, Reason: "blocked");
        var token = options["token"]?.ToString();
        if (string.IsNullOrEmpty(token) || fetchImpl == null) return new LiveExchangeResult(false, Reason: "missing");
        var offset = options["offset"]?.GetValue<int>() ?? 0;
        try
        {
            var response = await fetchImpl($"https://api.telegram.org/bot{token}/getUpdates?offset={offset}&timeout=0");
            var jsonStr = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(jsonStr) as JsonObject;
            var ok = response.IsSuccessStatusCode && doc?["ok"]?.GetValue<bool>() == true;
            var updates = (doc?["result"] as JsonArray)?.Count ?? 0;
            return new LiveExchangeResult(true, Ok: ok, Updates: updates);
        }
        catch (Exception error)
        {
            return new LiveExchangeResult(true, Ok: false, Updates: 0, Reason: error.Message ?? "network error");
        }
    }

    public static Task<LiveExchangeResult> LiveExchangeAsync(bool allowed, string? token, Func<string, Task<HttpResponseMessage>>? fetchImpl, int offset = 0)
    {
        var opts = new JsonObject
        {
            ["allowed"] = allowed,
            ["token"] = token,
            ["offset"] = offset
        };
        return LiveExchangeAsync(opts, fetchImpl);
    }

    public static string ReportMarkdown(JsonObject options)
    {
        var assessment = options["assessment"] as JsonObject;
        var probe = options["probe"] as JsonObject;
        var backup = options["backup"] as JsonObject;
        var exchange = options["exchange"] as JsonObject;
        var build = options["build"]?.ToString();

        var model = assessment?["model"]?.ToString();
        if (string.IsNullOrEmpty(model)) model = DefaultOllamaModel;
        var allowed = assessment?["allowed"]?.GetValue<bool>() == true ? "yes" : "no";
        var blocker = assessment?["blocker"]?.ToString();
        if (string.IsNullOrEmpty(blocker)) blocker = "none";

        string probeStr;
        if (probe != null)
        {
            var installed = probe["installed"]?.GetValue<bool>() == true;
            probeStr = installed ? "installed" : "not installed";
        }
        else
        {
            probeStr = "not run";
        }

        var exchangeStr = exchange?["ran"]?.GetValue<bool>() == true ? "ran" : "not run";

        var backupFilesList = new List<string>();
        if (backup?["files"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item?["name"]?.ToString() is string s)
                    backupFilesList.Add(s);
            }
        }
        var filesStr = backupFilesList.Count > 0 ? string.Join(", ", backupFilesList) : "none";
        var buildStr = !string.IsNullOrEmpty(build) ? build : "not built";

        var lines = new[]
        {
            "# Phone activation",
            "",
            $"Default model: {model}",
            $"Paired-bot verification allowed: {allowed}",
            $"Blocker: {blocker}",
            $"Local model probe: {probeStr}",
            $"Live Telegram exchange: {exchangeStr}",
            $"Backup files: {filesStr}",
            $"Native build: {buildStr}",
            "",
            "This check does not call Telegram getUpdates while the installed listener is running, and it does not change the installed app."
        };
        return string.Join("\n", lines) + "\n";
    }
}
