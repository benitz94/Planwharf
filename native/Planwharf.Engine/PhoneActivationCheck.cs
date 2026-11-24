// Port of engine/phone-activation-check.cjs.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public class PhoneActivationCheckOptions
{
    public IReadOnlyList<string>? Argv { get; set; }
    public string? From { get; set; }
    public string? Attempts { get; set; }
    public string? BackupTo { get; set; }
    public string? Notebook { get; set; }
    public string? Starter { get; set; }
    public string? Report { get; set; }
    public string? ListenersFile { get; set; }
    public string? Build { get; set; }
    public string? Live { get; set; }

    public Func<JsonObject?, Task<JsonObject?>>? ListInstalledModels { get; set; }
    public Func<JsonObject?, Task<JsonObject?>>? AskOllama { get; set; }
    public Func<string, Task<HttpResponseMessage>>? FetchImpl { get; set; }
    public Action<string>? StdoutWrite { get; set; }
    public Action<string>? StderrWrite { get; set; }
}

public record PhoneActivationCheckResult(
    int ExitCode,
    bool Allowed,
    string Blocker,
    ActivationAssessment? Assessment,
    JsonObject? Probe,
    LiveExchangeResult? Exchange,
    JsonObject? Local,
    BackupResult? Backup,
    string? NotebookRoot,
    string MarkdownReport,
    JsonObject? JsonReport,
    string StdoutText,
    string StderrText);

public static class PhoneActivationCheck
{
    public static string Arg(IReadOnlyList<string>? argv, string name)
    {
        if (argv == null) return string.Empty;
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return argv[i + 1];
            }
        }
        return string.Empty;
    }

    public static async Task<PhoneActivationCheckResult> RunAsync(PhoneActivationCheckOptions options)
    {
        var argv = options.Argv ?? Array.Empty<string>();
        var from = !string.IsNullOrEmpty(options.From) ? options.From : Arg(argv, "--from");
        var attemptsArg = !string.IsNullOrEmpty(options.Attempts) ? options.Attempts : Arg(argv, "--attempts");
        var attempts = !string.IsNullOrEmpty(attemptsArg) ? attemptsArg : from;
        var backupTo = !string.IsNullOrEmpty(options.BackupTo) ? options.BackupTo : Arg(argv, "--backup-to");
        var notebook = !string.IsNullOrEmpty(options.Notebook) ? options.Notebook : Arg(argv, "--notebook");
        var starter = !string.IsNullOrEmpty(options.Starter) ? options.Starter : Arg(argv, "--starter");
        var report = !string.IsNullOrEmpty(options.Report) ? options.Report : Arg(argv, "--report");
        var listenersFile = !string.IsNullOrEmpty(options.ListenersFile) ? options.ListenersFile : Arg(argv, "--listeners");
        var build = !string.IsNullOrEmpty(options.Build) ? options.Build : Arg(argv, "--build");
        var live = !string.IsNullOrEmpty(options.Live) ? options.Live : Arg(argv, "--live");

        var stdoutBuf = new List<string>();
        var stderrBuf = new List<string>();

        void WriteStdout(string s)
        {
            stdoutBuf.Add(s);
            options.StdoutWrite?.Invoke(s);
        }

        void WriteStderr(string s)
        {
            stderrBuf.Add(s);
            options.StderrWrite?.Invoke(s);
        }

        try
        {
            JsonArray listeners;
            if (!string.IsNullOrEmpty(listenersFile) && File.Exists(listenersFile))
            {
                var content = File.ReadAllText(listenersFile);
                var parsed = JsonNode.Parse(content);
                if (parsed is JsonArray arr)
                {
                    listeners = arr;
                }
                else if (parsed is JsonObject obj)
                {
                    listeners = new JsonArray { (JsonNode)obj.DeepClone() };
                }
                else
                {
                    listeners = new JsonArray();
                }
            }
            else
            {
                listeners = new JsonArray();
            }

            var backup = !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(backupTo)
                ? PhoneActivation.CopyBackup(from, backupTo, attempts)
                : new BackupResult(string.Empty, Array.Empty<JsonObject>());

            (string Root, string Attempts, string Plan)? made = null;
            if (!string.IsNullOrEmpty(notebook) && !string.IsNullOrEmpty(starter))
            {
                made = PhoneActivation.WriteIsolatedNotebook(notebook, starter);
            }

            var tokenPath = !string.IsNullOrEmpty(attempts) ? Path.Combine(attempts, "telegram-bot.token") : string.Empty;
            var token = !string.IsNullOrEmpty(tokenPath) && File.Exists(tokenPath)
                ? File.ReadAllText(tokenPath).Trim()
                : string.Empty;

            var assessOpts = new JsonObject
            {
                ["listeners"] = (JsonNode)listeners.DeepClone(),
                ["thisBuildPath"] = build,
                ["tokenPresent"] = !string.IsNullOrEmpty(token),
                ["isolatedHasToken"] = made != null && File.Exists(Path.Combine(made.Value.Attempts, "telegram-bot.token")),
                ["model"] = PhoneActivation.DefaultOllamaModel
            };
            var assessment = PhoneActivation.AssessPairedActivation(assessOpts);

            JsonObject probe;
            if (options.ListInstalledModels != null)
            {
                var listed = await options.ListInstalledModels(new JsonObject { ["timeoutMs"] = 4000 });
                var modelsArr = listed?["models"] as JsonArray ?? new JsonArray();
                var hasModel = false;
                foreach (var item in modelsArr)
                {
                    if (string.Equals(item?.ToString(), PhoneActivation.DefaultOllamaModel, StringComparison.OrdinalIgnoreCase))
                    {
                        hasModel = true;
                        break;
                    }
                }
                probe = new JsonObject
                {
                    ["ok"] = listed?["ok"]?.GetValue<bool>() == true,
                    ["models"] = (JsonNode)modelsArr.DeepClone(),
                    ["installed"] = hasModel
                };
            }
            else
            {
                probe = new JsonObject
                {
                    ["ok"] = false,
                    ["models"] = new JsonArray(),
                    ["installed"] = false
                };
            }

            JsonObject? local = null;
            if (probe["installed"]?.GetValue<bool>() == true && options.AskOllama != null)
            {
                local = await options.AskOllama(new JsonObject
                {
                    ["model"] = PhoneActivation.DefaultOllamaModel,
                    ["timeoutMs"] = 60000,
                    ["messages"] = new JsonArray
                    {
                        (JsonNode)new JsonObject { ["role"] = "system", ["content"] = "Reply with exactly one word: ready" },
                        (JsonNode)new JsonObject { ["role"] = "user", ["content"] = "ready?" }
                    }
                });
            }

            LiveExchangeResult exchange;
            if (string.Equals(live, "yes", StringComparison.OrdinalIgnoreCase))
            {
                exchange = await PhoneActivation.LiveExchangeAsync(assessment.Allowed, token, options.FetchImpl);
            }
            else
            {
                exchange = new LiveExchangeResult(Ran: false, Reason: "not requested");
            }

            var reportOpts = new JsonObject
            {
                ["assessment"] = new JsonObject
                {
                    ["allowed"] = assessment.Allowed,
                    ["blocker"] = assessment.Blocker,
                    ["model"] = assessment.Model
                },
                ["probe"] = (JsonNode)probe.DeepClone(),
                ["backup"] = new JsonObject
                {
                    ["files"] = new JsonArray(backup.Files.Select(f => (JsonNode)f.DeepClone()).ToArray())
                },
                ["exchange"] = new JsonObject
                {
                    ["ran"] = exchange.Ran
                },
                ["build"] = build
            };
            var markdown = PhoneActivation.ReportMarkdown(reportOpts);

            string localLine;
            if (local != null)
            {
                var okStr = local["ok"]?.GetValue<bool>() == true ? "true" : "false";
                var modelStr = local["model"]?.ToString() ?? string.Empty;
                localLine = $"Local model reply ok: {okStr}\nLocal model: {modelStr}\n";
            }
            else
            {
                localLine = "Local model reply: not run\n";
            }

            var replyLine = local?["ok"]?.GetValue<bool>() == true
                ? $"Local reply:\n{local["text"]}\n"
                : string.Empty;

            JsonObject? reportJson = null;
            if (!string.IsNullOrEmpty(report))
            {
                var fullReport = Path.GetFullPath(report);
                var dir = Path.GetDirectoryName(fullReport);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(report, markdown + localLine + replyLine);

                // Note: Regex replacing .md with .json will not replace non-.md extension,
                // causing a collision where report file is overwritten by json. This preserves
                // observed source behavior.
                var jsonPath = Regex.Replace(report, @"\.md$", ".json", RegexOptions.IgnoreCase);

                var backupArr = new JsonArray();
                foreach (var f in backup.Files)
                {
                    backupArr.Add((JsonNode)new JsonObject
                    {
                        ["name"] = f["name"]?.ToString() ?? string.Empty,
                        ["sha256"] = f["sha256"]?.ToString() ?? string.Empty,
                        ["bytes"] = f["bytes"]?.GetValue<int>() ?? 0
                    });
                }

                JsonObject? localJson = null;
                if (local != null)
                {
                    var isOk = local["ok"]?.GetValue<bool>() == true;
                    localJson = new JsonObject
                    {
                        ["ok"] = isOk,
                        ["model"] = local["model"]?.ToString() ?? string.Empty,
                        ["reason"] = local["reason"]?.ToString() ?? string.Empty,
                        ["text"] = isOk ? local["text"]?.ToString() ?? string.Empty : string.Empty
                    };
                }

                reportJson = new JsonObject
                {
                    ["allowed"] = assessment.Allowed,
                    ["blocker"] = assessment.Blocker,
                    ["model"] = assessment.Model,
                    ["installedListeners"] = new JsonArray(assessment.InstalledListeners.Select(l => (JsonNode)l.DeepClone()).ToArray()),
                    ["probe"] = (JsonNode)probe.DeepClone(),
                    ["exchange"] = new JsonObject
                    {
                        ["ran"] = exchange.Ran,
                        ["ok"] = exchange.Ok,
                        ["updates"] = exchange.Updates,
                        ["reason"] = exchange.Reason ?? string.Empty
                    },
                    ["local"] = localJson,
                    ["backup"] = backupArr,
                    ["notebook"] = made?.Root,
                    ["build"] = build ?? string.Empty
                };

                File.WriteAllText(jsonPath, reportJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }

            var blockerOut = !string.IsNullOrEmpty(assessment.Blocker) ? assessment.Blocker : "no blocker";
            var statusLine = $"{(assessment.Allowed ? "ALLOWED" : "BLOCKED")}\n{blockerOut}\n";
            WriteStdout(statusLine);

            return new PhoneActivationCheckResult(
                ExitCode: 0,
                Allowed: assessment.Allowed,
                Blocker: assessment.Blocker,
                Assessment: assessment,
                Probe: probe,
                Exchange: exchange,
                Local: local,
                Backup: backup,
                NotebookRoot: made?.Root,
                MarkdownReport: markdown + localLine + replyLine,
                JsonReport: reportJson,
                StdoutText: string.Join(string.Empty, stdoutBuf),
                StderrText: string.Empty);
        }
        catch (Exception error)
        {
            var errLine = $"{(!string.IsNullOrEmpty(error.Message) ? error.Message : error.ToString())}\n";
            WriteStderr(errLine);

            return new PhoneActivationCheckResult(
                ExitCode: 1,
                Allowed: false,
                Blocker: string.Empty,
                Assessment: null,
                Probe: null,
                Exchange: null,
                Local: null,
                Backup: null,
                NotebookRoot: null,
                MarkdownReport: string.Empty,
                JsonReport: null,
                StdoutText: string.Join(string.Empty, stdoutBuf),
                StderrText: string.Join(string.Empty, stderrBuf));
        }
    }
}
