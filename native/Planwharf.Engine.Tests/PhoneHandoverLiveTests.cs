// Tests for Planwharf.Engine.PhoneHandoverLive.

using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PhoneHandoverLiveTests
{
    [Fact]
    public void Redact_masks_token_shaped_values_and_preserves_plain_text()
    {
        var input = "Token 123456:ABCDEF_secret in message, and safe text 1234:short";
        var output = PhoneHandoverLive.Redact(input);
        Assert.DoesNotContain("ABCDEF_secret", output);
        Assert.Contains("[redacted]", output);
        Assert.Contains("1234:short", output);
    }

    [Fact]
    public void ProjectsFromPlan_extracts_schema2_and_schema1_projects()
    {
        var schema2 = new JsonObject
        {
            ["schema"] = 2,
            ["projects"] = new JsonArray
            {
                new JsonObject
                {
                    ["project"] = new JsonObject { ["id"] = "proj-1", ["name"] = "First" },
                    ["status"] = "working",
                    ["goal"] = "Build app"
                },
                new JsonObject
                {
                    ["id"] = "proj-2",
                    ["name"] = "Second",
                    ["direction"] = "Ship feature"
                }
            }
        };

        var list2 = PhoneHandoverLive.ProjectsFromPlan(schema2);
        Assert.Equal(2, list2.Count);
        Assert.Equal("proj-1", list2[0]?["id"]?.ToString());
        Assert.Equal("First", list2[0]?["name"]?.ToString());
        Assert.Equal("working", list2[0]?["status"]?.ToString());
        Assert.Equal("Build app", list2[0]?["goal"]?.ToString());
        Assert.Equal("proj-2", list2[1]?["id"]?.ToString());
        Assert.Equal("idle", list2[1]?["status"]?.ToString());
        Assert.Equal("Ship feature", list2[1]?["goal"]?.ToString());

        var schema1 = new JsonObject
        {
            ["id"] = "solo",
            ["name"] = "Solo",
            ["status"] = "done"
        };
        var list1 = PhoneHandoverLive.ProjectsFromPlan(schema1);
        Assert.Single(list1);
        Assert.Equal("solo", list1[0]?["id"]?.ToString());
    }

    [Fact]
    public async Task LivePoller_retries_409_eight_times_and_stops_with_blocked()
    {
        var pollAttempts = 0;
        var waits = new List<int>();
        var stored = new JsonObject
        {
            ["telegramToken"] = "123456:secret",
            ["telegram"] = new JsonObject { ["updateOffset"] = 0 }
        };

        var poller = PhoneHandoverLive.CreateLivePoller(
            stored,
            pollTelegramUpdatesImpl: _ =>
            {
                pollAttempts++;
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["ok"] = false,
                    ["updates"] = new JsonArray(),
                    ["reason"] = "HTTP 409 Conflict: terminated by other getUpdates request"
                });
            },
            waitImpl: ms => { waits.Add(ms); return Task.CompletedTask; });

        var polled = await poller.PollOnceAsync();
        Assert.Equal(8, pollAttempts);
        Assert.Equal(8, waits.Count);
        Assert.True(polled["blocked"]?.GetValue<bool>());
        Assert.Contains("409", polled["error"]?.ToString());
        Assert.Equal(0, poller.GetPendingAck());
    }

    [Fact]
    public async Task LivePoller_retries_409_and_recovers_updating_pending_ack()
    {
        var pollAttempts = 0;
        var waits = new List<int>();
        var stored = new JsonObject
        {
            ["telegramToken"] = "123456:secret",
            ["telegram"] = new JsonObject { ["updateOffset"] = 4 }
        };

        var poller = PhoneHandoverLive.CreateLivePoller(
            stored,
            pollTelegramUpdatesImpl: _ =>
            {
                pollAttempts++;
                if (pollAttempts < 3)
                {
                    return Task.FromResult<JsonObject?>(new JsonObject
                    {
                        ["ok"] = false,
                        ["updates"] = new JsonArray(),
                        ["reason"] = "HTTP 409 Conflict"
                    });
                }
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["ok"] = true,
                    ["updates"] = new JsonArray
                    {
                        new JsonObject { ["update_id"] = 7 },
                        new JsonObject { ["update_id"] = 11 }
                    }
                });
            },
            waitImpl: ms => { waits.Add(ms); return Task.CompletedTask; });

        var polled = await poller.PollOnceAsync();
        Assert.Equal(3, pollAttempts);
        Assert.Equal(2, waits.Count);
        Assert.True(polled["ok"]?.GetValue<bool>());
        Assert.Equal(12, poller.GetPendingAck());
    }

    [Fact]
    public async Task LivePoller_stops_immediately_on_non_409_error()
    {
        var pollAttempts = 0;
        var stored = new JsonObject
        {
            ["telegramToken"] = "123456:secret",
            ["telegram"] = new JsonObject { ["updateOffset"] = 0 }
        };

        var poller = PhoneHandoverLive.CreateLivePoller(
            stored,
            pollTelegramUpdatesImpl: _ =>
            {
                pollAttempts++;
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["ok"] = false,
                    ["updates"] = new JsonArray(),
                    ["reason"] = "HTTP 401 Unauthorized"
                });
            },
            waitImpl: _ => throw new InvalidOperationException("must not wait on 401"));

        var polled = await poller.PollOnceAsync();
        Assert.Equal(1, pollAttempts);
        Assert.False(polled.ContainsKey("ok") && polled["ok"]?.GetValue<bool>() == true);
        Assert.Equal("HTTP 401 Unauthorized", polled["error"]?.ToString());
        Assert.Equal(0, poller.GetPendingAck());
    }

    [Fact]
    public async Task LivePoller_redacts_error_when_poll_throws()
    {
        var stored = new JsonObject
        {
            ["telegramToken"] = "123456:ABCDEF_secret",
            ["telegram"] = new JsonObject { ["updateOffset"] = 4 }
        };

        var poller = PhoneHandoverLive.CreateLivePoller(
            stored,
            pollTelegramUpdatesImpl: _ => throw new InvalidOperationException("fail 123456:ABCDEF_secret"),
            waitImpl: _ => throw new InvalidOperationException("must not wait"));

        var polled = await poller.PollOnceAsync();
        Assert.Equal("fail [redacted]", polled["error"]?.ToString());
        Assert.Equal(0, poller.GetPendingAck());
    }

    [Fact]
    public async Task RunLiveHandoverAsync_orchestrates_full_workflow_and_restores_listener()
    {
        using var tempDir = new DisposableTempDirectory();
        var attemptsDir = Path.Combine(tempDir.Path, "source", ".attempts-plan");
        Directory.CreateDirectory(attemptsDir);
        File.WriteAllText(Path.Combine(tempDir.Path, "source", "plan.json"), "{\"schema\":2,\"projects\":[]}\n");
        File.WriteAllText(Path.Combine(attemptsDir, "phone-settings.json"), "{\"telegram\":{\"enabled\":true,\"startCode\":\"test\",\"updateOffset\":5},\"ollama\":{\"model\":\"qwen2.5:3b\"}}\n");
        File.WriteAllText(Path.Combine(attemptsDir, "telegram-bot.token"), "123456:secret\n");

        var backupTo = Path.Combine(tempDir.Path, "backup");
        var dataFolder = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(Path.Combine(dataFolder, ".attempts-plan"));
        File.WriteAllText(Path.Combine(dataFolder, ".attempts-plan", "telegram-bot.token"), "copied-token\n");

        var reportMd = Path.Combine(tempDir.Path, "report.md");
        var reportJson = Path.Combine(tempDir.Path, "report.json");

        var httpCalls = new List<string>();
        var killedPid = 0;
        var stdoutRecorded = new List<string>();

        var options = new LiveHandoverOptions
        {
            InstalledAddress = "http://127.0.0.1:4173",
            InstalledExe = @"C:\Installed\Planwharf.exe",
            BuildPath = @"C:\Build\Planwharf.exe",
            BackupTo = backupTo,
            DataFolder = dataFolder,
            ReportMd = reportMd,
            ReportJson = reportJson,
            StdoutWrite = s => stdoutRecorded.Add(s),
            HttpJson = (url, method, body) =>
            {
                httpCalls.Add($"{method} {url}");
                if (url.EndsWith("/api/app"))
                {
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["pid"] = 1234,
                        ["attemptsDir"] = attemptsDir,
                        ["planFile"] = Path.Combine(tempDir.Path, "source", "plan.json")
                    });
                }
                if (url.EndsWith("/api/settings/phone") && method == HttpMethod.Get)
                {
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["telegram"] = new JsonObject { ["enabled"] = true, ["paired"] = true }
                    });
                }
                if (url.EndsWith("/api/settings/phone") && method == HttpMethod.Post)
                {
                    var isEnabled = body != null && body.Contains("\"enabled\":true");
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["telegram"] = new JsonObject { ["enabled"] = isEnabled, ["paired"] = true }
                    });
                }
                if (url.EndsWith("/api/settings/phone/test"))
                {
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["result"] = new JsonObject
                        {
                            ["telegram"] = new JsonObject
                            {
                                ["data"] = new JsonObject
                                {
                                    ["result"] = new JsonObject { ["message_id"] = 101, ["text"] = "Alert sent" }
                                }
                            }
                        }
                    });
                }
                if (url.EndsWith("/api/plan"))
                {
                    return Task.FromResult<JsonNode?>(new JsonObject { ["plan"] = new JsonObject { ["schema"] = 2, ["projects"] = new JsonArray() } });
                }
                return Task.FromResult<JsonNode?>(new JsonObject());
            },
            PollTelegramUpdatesImpl = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = true,
                ["updates"] = new JsonArray()
            }),
            WaitImpl = _ => Task.CompletedTask,
            StartProcess = opt => Task.FromResult<LiveProcessStartResult?>(new LiveProcessStartResult(9999, "http://127.0.0.1:4174", "qwen2.5:3b", true)),
            StopProcess = pid => { killedPid = pid; return Task.CompletedTask; }
        };

        var result = await PhoneHandoverLive.RunLiveHandoverAsync(options);

        Assert.True(result.Ok);
        Assert.True(result.Restored);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(9999, killedPid);
        Assert.False(File.Exists(Path.Combine(dataFolder, ".attempts-plan", "telegram-bot.token")));
        Assert.True(File.Exists(reportMd));
        Assert.True(File.Exists(reportJson));
        Assert.Contains("HANDED", string.Join("\n", stdoutRecorded));
    }

    [Fact]
    public async Task RunLiveHandoverAsync_restores_and_cleans_token_on_build_failure()
    {
        using var tempDir = new DisposableTempDirectory();
        var attemptsDir = Path.Combine(tempDir.Path, "source", ".attempts-plan");
        Directory.CreateDirectory(attemptsDir);
        File.WriteAllText(Path.Combine(tempDir.Path, "source", "plan.json"), "{\"schema\":2,\"projects\":[]}\n");
        File.WriteAllText(Path.Combine(attemptsDir, "phone-settings.json"), "{\"telegram\":{\"enabled\":true,\"startCode\":\"test\"},\"ollama\":{\"model\":\"qwen2.5:3b\"}}\n");
        File.WriteAllText(Path.Combine(attemptsDir, "telegram-bot.token"), "123456:secret\n");

        var backupTo = Path.Combine(tempDir.Path, "backup");
        var dataFolder = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(Path.Combine(dataFolder, ".attempts-plan"));
        var copiedToken = Path.Combine(dataFolder, ".attempts-plan", "telegram-bot.token");
        File.WriteAllText(copiedToken, "copied-token\n");

        var options = new LiveHandoverOptions
        {
            InstalledAddress = "http://127.0.0.1:4173",
            InstalledExe = @"C:\Installed\Planwharf.exe",
            BuildPath = @"C:\Build\Planwharf.exe",
            BackupTo = backupTo,
            DataFolder = dataFolder,
            HttpJson = (url, method, body) =>
            {
                if (url.EndsWith("/api/app"))
                {
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["pid"] = 1234,
                        ["attemptsDir"] = attemptsDir,
                        ["planFile"] = Path.Combine(tempDir.Path, "source", "plan.json")
                    });
                }
                if (url.EndsWith("/api/settings/phone") && method == HttpMethod.Post)
                {
                    var isEnabled = body != null && body.Contains("\"enabled\":true");
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["telegram"] = new JsonObject { ["enabled"] = isEnabled, ["paired"] = true }
                    });
                }
                if (url.EndsWith("/api/settings/phone"))
                {
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["telegram"] = new JsonObject { ["enabled"] = true, ["paired"] = true }
                    });
                }
                return Task.FromResult<JsonNode?>(new JsonObject());
            },
            PollTelegramUpdatesImpl = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["updates"] = new JsonArray() }),
            WaitImpl = _ => Task.CompletedTask,
            StartProcess = _ => Task.FromResult<LiveProcessStartResult?>(null) // Build fails to start
        };

        var result = await PhoneHandoverLive.RunLiveHandoverAsync(options);

        Assert.False(result.Ok);
        Assert.True(result.Restored);
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(copiedToken)); // Token removed in finally
    }

    private sealed class DisposableTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pw-live-test-" + Guid.NewGuid().ToString("n"));

        public DisposableTempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
