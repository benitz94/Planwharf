// Port of engine/provider-runtime.cjs.
using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public record LaunchSpecResult(
    string Command,
    IReadOnlyList<string> Args,
    string Cwd,
    string Provider,
    string Adapter,
    string Model,
    string? Stdin = null,
    string? PromptFile = null
);

public static class ProviderRuntime
{
    private static string? _codexCertificateBundle;
    private static readonly Dictionary<string, bool> StoreShellAccess = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ClaudeLoginOverrides =
    [
        "CLAUDE_CODE_OAUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
        "ANTHROPIC_BASE_URL"
    ];

    private static readonly string[] CodexLoginOverrides =
    [
        "OPENAI_API_KEY"
    ];

    private static void DeleteEnv(Dictionary<string, string> env, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var keysToRemove = env.Keys.Where(k => wanted.Contains(k)).ToList();
        foreach (var k in keysToRemove)
            env.Remove(k);
    }

    public static Dictionary<string, string> AccountEnvironment(
        string adapter,
        JsonObject? model,
        IDictionary<string, string>? inherited = null,
        string? platform = null)
    {
        var env = ProviderEnvironment(adapter, inherited, platform);
        var folder = model?["settingsFolder"]?.ToString();
        if (string.IsNullOrEmpty(folder) || folder.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return env;

        if (adapter == "claude")
        {
            env["CLAUDE_CONFIG_DIR"] = folder;
            DeleteEnv(env, ClaudeLoginOverrides);
        }
        else if (adapter == "codex")
        {
            env["CODEX_HOME"] = folder;
            DeleteEnv(env, CodexLoginOverrides);
        }
        return env;
    }

    public static Dictionary<string, string> ProviderEnvironment(
        string provider,
        IDictionary<string, string>? inherited = null,
        string? platform = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = inherited ?? EnvironmentVariables();
        foreach (var kvp in source) env[kvp.Key] = kvp.Value;

        var plat = platform ?? (OperatingSystem.IsWindows() ? "win32" : "posix");
        if (provider != "codex" || plat != "win32")
            return env;

        var pathKey = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase));
        if (pathKey != null)
        {
            var parts = env[pathKey].Split(';', StringSplitOptions.RemoveEmptyEntries);
            var filtered = parts.Where(dir =>
            {
                if (!dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return true;
                var shell = Path.Combine(dir, "pwsh.exe");
                try
                {
                    if (!File.Exists(shell)) return true;
                }
                catch { return true; }

                lock (StoreShellAccess)
                {
                    if (!StoreShellAccess.TryGetValue(shell, out var accessible))
                    {
                        try
                        {
                            var psi = new ProcessStartInfo(shell, "-NoProfile -NonInteractive -Command exit 0")
                            {
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            using var proc = Process.Start(psi);
                            if (proc != null)
                            {
                                proc.WaitForExit(2000);
                                accessible = proc.ExitCode == 0;
                            }
                            else accessible = false;
                        }
                        catch { accessible = false; }
                        StoreShellAccess[shell] = accessible;
                    }
                    return accessible;
                }
            });
            env[pathKey] = string.Join(";", filtered);
        }

        if (env.ContainsKey("CODEX_CA_CERTIFICATE") || env.ContainsKey("SSL_CERT_FILE"))
            return env;

        if (_codexCertificateBundle == null)
        {
            var certDir = Path.Combine(Path.GetTempPath(), "pw-codex-ca-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(certDir);
            var file = Path.Combine(certDir, "trusted-roots.pem");
            File.WriteAllText(file, "# Codex CA Bundle\n");
            _codexCertificateBundle = file;
        }
        env["CODEX_CA_CERTIFICATE"] = _codexCertificateBundle;
        return env;
    }

    public static string? Executable(string provider)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grok"] = "grok",
            ["codex"] = "codex",
            ["antigravity"] = "agy",
            ["claude"] = "claude"
        };

        if (!names.TryGetValue(provider, out var name))
            return null;

        var overrideVal = Environment.GetEnvironmentVariable("PLANWHARF_" + provider.ToUpperInvariant());
        if (provider == "claude" && !string.IsNullOrEmpty(overrideVal))
        {
            try { return File.Exists(overrideVal) ? overrideVal : null; } catch { return null; }
        }
        if (!string.IsNullOrEmpty(overrideVal) && File.Exists(overrideVal))
            return overrideVal;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pinned = provider switch
        {
            "grok" => Path.Combine(home, ".grok", "bin", "grok.exe"),
            "antigravity" => Path.Combine(home, ".gemini", "bin", "agy.exe"),
            "claude" => Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude"),
            _ => ""
        };
        if (!string.IsNullOrEmpty(pinned) && File.Exists(pinned))
            return pinned;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var delimiter = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathVar.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (provider == "codex" && OperatingSystem.IsWindows())
            {
                var arch = "x64";
                var triple = "x86_64-pc-windows-msvc";
                var packageRoot = Path.Combine(dir, "node_modules", "@openai", "codex");
                var candidates = new[]
                {
                    Path.Combine(packageRoot, "node_modules", "@openai", "codex-win32-" + arch, "vendor", triple, "bin", "codex.exe"),
                    Path.Combine(dir, "node_modules", "@openai", "codex-win32-" + arch, "vendor", triple, "bin", "codex.exe"),
                    Path.Combine(packageRoot, "vendor", triple, "bin", "codex.exe")
                };
                foreach (var file in candidates)
                    if (File.Exists(file)) return file;
            }

            var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
            foreach (var ext in extensions)
            {
                var file = Path.Combine(dir, name + ext);
                if (File.Exists(file)) return file;
            }
        }
        return null;
    }

    public static LaunchSpecResult LaunchSpec(
        JsonObject model,
        string folder,
        string prompt,
        bool lead = false,
        bool research = false)
    {
        var routeErr = ModelRouting.ModelRouteError(model);
        if (routeErr != null) throw new InvalidOperationException(routeErr);

        var provider = model["providerId"]?.ToString() ?? model["provider"]?.ToString() ?? "";
        var adapter = model["adapter"]?.ToString() ?? model["provider"]?.ToString() ?? "";

        if (research && adapter != "codex")
            throw new InvalidOperationException("Research source protection is not supported by the requested adapter: " + adapter);

        var cli = model["cli"]?.ToString() ?? "";

        bool alwaysApprove = false;
        var policyNode = model["policy"]?["alwaysApprove"] ?? model["alwaysApprove"];
        if (policyNode != null)
        {
            if (policyNode is JsonValue jv && jv.TryGetValue<bool>(out var bVal))
                alwaysApprove = bVal;
            else
                throw new InvalidOperationException("Invalid alwaysApprove model policy.");
        }

        if (!alwaysApprove && adapter == "antigravity")
            throw new InvalidOperationException("PERMISSION_MODE_UNSUPPORTED: Antigravity has no verified unattended approval bridge. Enable Always approve / YOLO explicitly or select another adapter.");

        if (!alwaysApprove && !lead && !research && (adapter == "claude" || adapter == "grok"))
            throw new InvalidOperationException($"PERMISSION_NEEDED: {provider} needs your OK to work in the project folder without asking each time. Allow it in Team > AI tools, or choose another model for this step.");

        var command = Executable(adapter) ?? (OperatingSystem.IsWindows() ? adapter + ".exe" : adapter);
        if (string.IsNullOrEmpty(command))
            throw new InvalidOperationException(provider + " executable not found (native executable required)");

        var args = new List<string>();
        string? stdin = null;
        string? promptFile = null;

        if (adapter == "claude")
        {
            args.AddRange(["-p", "--model", cli, "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
            args.AddRange(Claude.ClaudeToolArgs(model["tools"] as JsonObject));
            args.AddRange(["--disallowedTools", "Agent,Task", "--permission-mode", alwaysApprove ? "bypassPermissions" : "dontAsk"]);
            stdin = prompt;
        }
        else if (adapter == "grok")
        {
            args.AddRange(["--cwd", folder, "-m", cli, "--no-subagents", "--max-turns", lead ? "8" : "500",
                "--permission-mode", alwaysApprove ? "bypassPermissions" : "dontAsk", "--output-format", lead ? "streaming-messages-json" : "plain"]);
            if (alwaysApprove) args.Add("--always-approve");
            if (lead) args.Add("--include-partial-messages");

            var inputDir = Path.Combine(Path.GetTempPath(), "pw-grok-prompt-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(inputDir);
            promptFile = Path.Combine(inputDir, "prompt.txt");
            File.WriteAllText(promptFile, prompt);
            args.AddRange(["--prompt-file", promptFile]);
        }
        else if (adapter == "antigravity")
        {
            args.AddRange(["--model", cli, "--mode", "accept-edits", "--dangerously-skip-permissions", "--output-format", "stream-json", "--print-timeout", "30m", "--input-format", "stream-json"]);
            var pNode = (JsonNode)prompt;
            stdin = $"{{\"event\":\"user\",\"message\":{{\"content\":{pNode.ToJsonString()}}}}}\n";
        }
        else
        {
            args.AddRange(["exec", "--skip-git-repo-check", "-C", folder, "-m", cli]);
            if (model["tools"]?["mcp"]?.GetValue<bool>() != true)
                args.AddRange(["-c", "mcp_servers={}"]);

            if (research)
            {
                args.AddRange(["--sandbox", "workspace-write", "-c", "approval_policy=\"never\"", "-c", "sandbox_workspace_write.network_access=true", "-c", "sandbox_workspace_write.writable_roots=[]"]);
            }
            else if (alwaysApprove)
            {
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else
            {
                var resultFolder = model["apiResultOutputPath"]?.ToString() is string p && !string.IsNullOrEmpty(p)
                    ? new[] { Path.GetDirectoryName(p) ?? "" }
                    : [];
                var arr = new JsonArray();
                foreach (var rf in resultFolder) arr.Add((JsonNode)rf);
                args.AddRange(["--sandbox", "workspace-write", "-c", "approval_policy=\"never\"", "-c", "sandbox_workspace_write.network_access=false", "-c", "sandbox_workspace_write.writable_roots=" + arr.ToJsonString()]);
            }

            if (lead) args.Add("--json");
            args.Add("-");
            stdin = prompt;
        }

        return new LaunchSpecResult(command, args, folder, provider, adapter, cli, stdin, promptFile);
    }

    private static Dictionary<string, string> EnvironmentVariables()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
            if (de.Key is string k && de.Value is string v) dict[k] = v;
        return dict;
    }
}
