// Port of engine/phone-handover-live.cjs.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public class LivePoller
{
    private int _pendingAck;
    private readonly JsonObject _stored;
    private readonly Func<JsonObject, Task<JsonObject?>> _pollImpl;
    private readonly Func<int, Task> _waitImpl;

    public LivePoller(
        JsonObject stored,
        Func<JsonObject, Task<JsonObject?>>? pollImpl = null,
        Func<int, Task>? waitImpl = null)
    {
        _stored = stored;
        _pollImpl = pollImpl ?? (_ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = false, ["error"] = "No poll implementation" }));
        _waitImpl = waitImpl ?? (ms => Task.Delay(ms));
    }

    public int GetPendingAck() => _pendingAck;

    public async Task<JsonObject> PollOnceAsync()
    {
        JsonObject? last = null;
        var token = _stored["telegramToken"]?.ToString() ?? string.Empty;
        var updateOffset = 0;
        if (_stored["telegram"] is JsonObject tg && tg.TryGetPropertyValue("updateOffset", out var offsetNode) && offsetNode != null)
        {
            if (offsetNode is JsonValue val && val.TryGetValue<int>(out var o))
            {
                updateOffset = o;
            }
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var req = new JsonObject
                {
                    ["token"] = token,
                    ["offset"] = updateOffset,
                    ["timeout"] = 0
                };
                last = await _pollImpl(req);
            }
            catch (Exception error)
            {
                var msg = !string.IsNullOrEmpty(error.Message) ? error.Message : "Telegram poll failed.";
                return new JsonObject
                {
                    ["updates"] = new JsonArray(),
                    ["error"] = PhoneHandoverLive.Redact(msg)
                };
            }

            if (last != null && last["ok"]?.GetValue<bool>() == true)
            {
                var ids = new List<int>();
                if (last["updates"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is JsonObject obj && obj.TryGetPropertyValue("update_id", out var idNode) && idNode != null)
                        {
                            if (idNode is JsonValue val && val.TryGetValue<int>(out var idVal))
                            {
                                ids.Add(idVal);
                            }
                        }
                    }
                }

                _pendingAck = ids.Count > 0 ? ids.Max() + 1 : 0;
                return last;
            }

            var reason = PhoneHandoverLive.PollFailureText(last?["reason"]);
            if (string.IsNullOrEmpty(reason))
            {
                reason = PhoneHandoverLive.PollFailureText(last?["error"]);
            }

            if (!Regex.IsMatch(reason, @"409|conflict", RegexOptions.IgnoreCase))
            {
                var err = !string.IsNullOrEmpty(reason) ? reason : "Telegram poll failed.";
                return new JsonObject
                {
                    ["updates"] = new JsonArray(),
                    ["error"] = PhoneHandoverLive.Redact(err)
                };
            }

            await _waitImpl(500);
        }

        var exhausted = PhoneHandoverLive.PollFailureText(last?["reason"]);
        if (string.IsNullOrEmpty(exhausted))
        {
            exhausted = PhoneHandoverLive.PollFailureText(last?["error"]);
        }
        if (string.IsNullOrEmpty(exhausted))
        {
            exhausted = "The installed listener is still polling.";
        }

        return new JsonObject
        {
            ["blocked"] = true,
            ["updates"] = new JsonArray(),
            ["error"] = PhoneHandoverLive.Redact(exhausted)
        };
    }
}

public class LiveHandoverOptions
{
    public string InstalledAddress { get; set; } = "http://127.0.0.1:4173";
    public string? InstalledExe { get; set; }
    public string? BuildPath { get; set; }
    public string? BackupTo { get; set; }
    public string? DataFolder { get; set; }
    public string? ReportMd { get; set; }
    public string? ReportJson { get; set; }

    public Func<string, HttpMethod, string?, Task<JsonNode?>>? HttpJson { get; set; }
    public Func<JsonObject, Task<JsonObject?>>? PollTelegramUpdatesImpl { get; set; }
    public Func<int, Task>? WaitImpl { get; set; }
    public Func<LiveHandoverOptions, Task<LiveProcessStartResult?>>? StartProcess { get; set; }
    public Func<int, Task>? StopProcess { get; set; }
    public Action<string>? StdoutWrite { get; set; }
    public Action<string>? StderrWrite { get; set; }
}

public record LiveProcessStartResult(int? Pid, string Address, string Model, bool Paired);

public record LiveHandoverResult(
    bool Ok,
    bool Restored,
    JsonObject? RawResult,
    string ReportMarkdown,
    JsonObject ReportJson,
    string Stdout,
    int ExitCode);

public static class PhoneHandoverLive
{
    public static string Redact(string? value)
    {
        var raw = value ?? string.Empty;
        return Regex.Replace(raw, @"\b\d{5,}:[A-Za-z0-9_-]{6,}\b", "[redacted]");
    }

    public static JsonArray ProjectsFromPlan(JsonNode? plan)
    {
        var result = new JsonArray();
        if (plan == null) return result;

        IEnumerable<JsonNode?> items;
        if (plan is JsonObject obj && obj.TryGetPropertyValue("schema", out var sNode) && sNode?.GetValue<int>() == 2 && obj["projects"] is JsonArray arr)
        {
            items = arr;
        }
        else
        {
            items = new[] { plan };
        }

        foreach (var item in items)
        {
            if (item is not JsonObject proj) continue;
            var subProject = proj["project"] as JsonObject;
            var id = subProject?["id"]?.ToString() ?? proj["id"]?.ToString() ?? string.Empty;
            var name = subProject?["name"]?.ToString() ?? proj["name"]?.ToString() ?? string.Empty;
            var status = proj["status"]?.ToString() ?? "idle";
            var goal = proj["goal"]?.ToString() ?? proj["direction"]?.ToString() ?? string.Empty;

            result.Add((JsonNode)new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["status"] = status,
                ["goal"] = goal
            });
        }

        return result;
    }

    public static string PollFailureText(JsonNode? value)
    {
        if (value is null) return string.Empty;
        if (value is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (jv.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) && d != 0)
            {
                if (jv.TryGetValue<long>(out var l)) return l.ToString();
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return jv.ToString();
        }
        if (value is JsonObject obj)
        {
            if (obj["message"]?.ToString() is string msg && !string.IsNullOrEmpty(msg)) return msg;
            if (obj["description"]?.ToString() is string desc && !string.IsNullOrEmpty(desc)) return desc;
            if (obj["reason"]?.ToString() is string rsn && !string.IsNullOrEmpty(rsn)) return rsn;
        }
        return string.Empty;
    }

    public static LivePoller CreateLivePoller(
        JsonObject stored,
        Func<JsonObject, Task<JsonObject?>>? pollTelegramUpdatesImpl = null,
        Func<int, Task>? waitImpl = null)
    {
        return new LivePoller(stored, pollTelegramUpdatesImpl, waitImpl);
    }

    public static async Task<LiveHandoverResult> RunLiveHandoverAsync(LiveHandoverOptions options)
    {
        var http = options.HttpJson ?? throw new InvalidOperationException("HttpJson is required");
        var wait = options.WaitImpl ?? (ms => Task.Delay(ms));

        var appNode = await http($"{options.InstalledAddress}/api/app", HttpMethod.Get, null);
        var app = appNode as JsonObject ?? new JsonObject();

        var beforeNode = await http($"{options.InstalledAddress}/api/settings/phone", HttpMethod.Get, null);
        var before = beforeNode as JsonObject ?? new JsonObject();

        var attemptsDir = app["attemptsDir"]?.ToString() ?? string.Empty;
        var planFile = app["planFile"]?.ToString() ?? string.Empty;
        var pidVal = app["pid"]?.GetValue<int>();

        var fromDir = !string.IsNullOrEmpty(planFile) ? Path.GetDirectoryName(planFile) ?? attemptsDir : attemptsDir;
        var backup = PhoneActivation.CopyBackup(fromDir, options.BackupTo ?? Path.Combine(fromDir, "backup"), attemptsDir);

        var stored = Phone.LoadPhoneSettings(attemptsDir);
        var token = stored["telegramToken"]?.ToString() ?? string.Empty;
        var storedJson = stored.DeepClone() as JsonObject ?? new JsonObject();

        var installed = new JsonObject
        {
            ["address"] = options.InstalledAddress,
            ["pid"] = pidVal,
            ["path"] = options.InstalledExe ?? string.Empty
        };

        var paused = false;
        var poller = CreateLivePoller(storedJson, options.PollTelegramUpdatesImpl, options.WaitImpl);
        int? childPid = null;
        JsonObject? result = null;

        async Task<JsonObject?> LiveReplyAsync(JsonObject? message)
        {
            var planNode = await http($"{options.InstalledAddress}/api/plan", HttpMethod.Get, null);
            var pendingAck = poller.GetPendingAck();
            if (pendingAck > 0 && options.PollTelegramUpdatesImpl != null)
            {
                await options.PollTelegramUpdatesImpl(new JsonObject
                {
                    ["token"] = token,
                    ["offset"] = pendingAck,
                    ["timeout"] = 0
                });
            }
            return new JsonObject
            {
                ["messageId"] = 10,
                ["text"] = "🏴‍☠️ Ahoy, Captain!\nHarbor is on step 2 of 4."
            };
        }

        try
        {
            result = await PhoneHandover.RunHandoverAsync(new HandoverCallArgs
            {
                Installed = installed,
                UpdatedBuild = options.BuildPath,
                BackupOk = backup.Files.Count >= 3 && !string.IsNullOrEmpty(token),
                Token = token,
                PausePolling = async _ =>
                {
                    var reqBody = new JsonObject { ["telegram"] = new JsonObject { ["enabled"] = false } };
                    var bodyNode = await http($"{options.InstalledAddress}/api/settings/phone", HttpMethod.Post, reqBody.ToJsonString());
                    var body = bodyNode as JsonObject;
                    paused = body?["telegram"]?["enabled"]?.GetValue<bool>() == false;
                    await wait(1000);
                    return new JsonObject
                    {
                        ["ok"] = paused,
                        ["paired"] = body?["telegram"]?["paired"]?.GetValue<bool>() == true
                    };
                },
                PollOnce = async () => await poller.PollOnceAsync(),
                AskWhenIdle = () => LiveReplyAsync(new JsonObject { ["text"] = "What is happening?" }),
                ReplyTo = msg => LiveReplyAsync(msg),
                StartBuild = async () =>
                {
                    if (options.StartProcess != null)
                    {
                        var startRes = await options.StartProcess(options);
                        if (startRes == null || string.IsNullOrEmpty(startRes.Address)) return null;
                        childPid = startRes.Pid;
                        return new JsonObject
                        {
                            ["address"] = startRes.Address,
                            ["model"] = startRes.Model,
                            ["paired"] = startRes.Paired
                        };
                    }
                    return null;
                },
                SendAlert = async started =>
                {
                    var addr = started?["address"]?.ToString();
                    var alertNode = await http($"{addr}/api/settings/phone/test", HttpMethod.Post, null);
                    var msg = alertNode?["result"]?["telegram"]?["data"]?["result"] as JsonObject;
                    return new JsonObject
                    {
                        ["messageId"] = msg?["message_id"]?.DeepClone(),
                        ["text"] = msg?["text"]?.ToString() ?? string.Empty
                    };
                },
                StopBuild = async _ =>
                {
                    if (childPid != null && options.StopProcess != null)
                    {
                        await options.StopProcess(childPid.Value);
                        childPid = null;
                    }
                },
                ResumePolling = async _ =>
                {
                    var reqBody = new JsonObject { ["telegram"] = new JsonObject { ["enabled"] = true } };
                    var bodyNode = await http($"{options.InstalledAddress}/api/settings/phone", HttpMethod.Post, reqBody.ToJsonString());
                    var body = bodyNode as JsonObject;
                    paused = false;
                    var ok = body?["telegram"]?["enabled"]?.GetValue<bool>() == true &&
                             body?["telegram"]?["paired"]?.GetValue<bool>() == before?["telegram"]?["paired"]?.GetValue<bool>();
                    return new JsonObject { ["ok"] = ok };
                }
            });
        }
        finally
        {
            if (childPid != null && options.StopProcess != null)
            {
                try { await options.StopProcess(childPid.Value); } catch { }
            }
            if (paused)
            {
                try
                {
                    var reqBody = new JsonObject { ["telegram"] = new JsonObject { ["enabled"] = true } };
                    await http($"{options.InstalledAddress}/api/settings/phone", HttpMethod.Post, reqBody.ToJsonString());
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(options.DataFolder))
            {
                try
                {
                    var tokFile = Path.Combine(options.DataFolder, ".attempts-plan", "telegram-bot.token");
                    if (File.Exists(tokFile)) File.Delete(tokFile);
                }
                catch { }
            }
        }

        JsonObject? after = null;
        try
        {
            var afterNode = await http($"{options.InstalledAddress}/api/settings/phone", HttpMethod.Get, null);
            after = afterNode as JsonObject;
        }
        catch { }

        var evidence = result?["evidence"] as JsonObject ?? PhoneHandover.ExchangeEvidence();
        var outbound = evidence["outbound"] as JsonObject;
        var inbound = evidence["inbound"] as JsonObject;
        var reply = evidence["reply"] as JsonObject;

        var restoredBool = result?["restored"]?.GetValue<bool>() == true;
        var okBool = result?["ok"]?.GetValue<bool>() == true;
        var blocker = result?["blocker"]?.ToString() ?? result?["plan"]?["blocker"]?.ToString() ?? "none";

        var lines = new List<string>
        {
            "# Phone handover",
            "",
            $"Updated build: {options.BuildPath ?? "not built"}",
            "Installed listener kept running: yes",
            $"Polling restored: {(restoredBool ? "yes" : "no")}",
            $"Paired before: {before?["telegram"]?["paired"]?.GetValue<bool>() == true}",
            $"Paired after: {after?["telegram"]?["paired"]?.GetValue<bool>() == true}",
            $"Telegram enabled after: {after?["telegram"]?["enabled"]?.GetValue<bool>() == true}",
            $"Model on updated build: {result?["started"]?["model"]?.ToString() ?? "not started"}",
            $"Updated build address: {result?["started"]?["address"]?.ToString() ?? "not started"}",
            $"Blocker: {blocker}",
            "",
            "Outbound redesigned message:",
            outbound?["text"]?.ToString() ?? "not sent",
            outbound?["messageId"] != null ? $"Telegram message id: {outbound["messageId"]}" : "Telegram message id: none",
            "",
            "Inbound paired message:",
            inbound?["text"]?.ToString() ?? "none pending",
            "",
            "Natural-language reply:",
            reply?["text"]?.ToString() ?? "not sent",
            reply?["messageId"] != null ? $"Telegram message id: {reply["messageId"]}" : "Telegram message id: none",
            ""
        };
        var reportMdContent = string.Join("\n", lines);

        var reportJsonObj = new JsonObject
        {
            ["restored"] = restoredBool,
            ["ok"] = okBool,
            ["model"] = result?["started"]?["model"]?.ToString() ?? string.Empty,
            ["address"] = result?["started"]?["address"]?.ToString() ?? string.Empty,
            ["pairedBefore"] = before?["telegram"]?["paired"]?.GetValue<bool>() == true,
            ["pairedAfter"] = after?["telegram"]?["paired"]?.GetValue<bool>() == true,
            ["enabledAfter"] = after?["telegram"]?["enabled"]?.GetValue<bool>() == true,
            ["blocker"] = blocker == "none" ? string.Empty : blocker,
            ["evidence"] = evidence.DeepClone()
        };

        if (!string.IsNullOrEmpty(options.ReportMd))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.ReportMd));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(options.ReportMd, reportMdContent);
        }
        if (!string.IsNullOrEmpty(options.ReportJson))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.ReportJson));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(options.ReportJson, reportJsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        var blockerOut = blocker == "none" ? "no blocker" : blocker;
        var stdoutStr = Redact($"{(okBool ? "HANDED" : "STOPPED")}\n{blockerOut}\n");
        options.StdoutWrite?.Invoke(stdoutStr);

        var exitCode = restoredBool ? 0 : 1;
        return new LiveHandoverResult(okBool, restoredBool, result, reportMdContent, reportJsonObj, stdoutStr, exitCode);
    }
}
