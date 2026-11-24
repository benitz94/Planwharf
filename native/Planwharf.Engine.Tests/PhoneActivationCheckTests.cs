// Tests for Planwharf.Engine.PhoneActivationCheck.

using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PhoneActivationCheckTests
{
    [Fact]
    public async Task Blocked_when_no_token_present()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        var report = Path.Combine(temp.Path, "report.md");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--report", report, "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = true,
                ["models"] = new JsonArray()
            })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Allowed);
        Assert.Contains("No bot token is available", result.Blocker);
        Assert.StartsWith("BLOCKED", result.StdoutText);
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(Path.Combine(temp.Path, "report.json")));
        var reportContent = File.ReadAllText(report);
        Assert.Contains("Local model reply: not run", reportContent);
        Assert.Contains("Paired-bot verification allowed: no", reportContent);
    }

    [Fact]
    public async Task Allowed_when_token_present_and_no_competing_listener()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "123456:secret\n");
        var report = Path.Combine(temp.Path, "report.md");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--report", report, "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = true,
                ["models"] = new JsonArray()
            })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Allowed);
        Assert.StartsWith("ALLOWED\nno blocker", result.StdoutText);
        Assert.True(File.Exists(report));
        var reportJson = File.ReadAllText(Path.Combine(temp.Path, "report.json"));
        Assert.Contains("\"allowed\": true", reportJson);
    }

    [Fact]
    public async Task Blocked_when_listener_already_running_and_polling()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "123456:secret\n");
        var listenersFile = Path.Combine(temp.Path, "listeners.json");
        File.WriteAllText(listenersFile, "[{\"path\":\"C:\\\\Installed\\\\Planwharf.exe\",\"running\":true,\"pollsTelegram\":true,\"pid\":42}]\n");
        var report = Path.Combine(temp.Path, "report.md");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--listeners", listenersFile, "--report", report, "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["models"] = new JsonArray() })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Allowed);
        Assert.Contains("already polling", result.Blocker);
        Assert.StartsWith("BLOCKED", result.StdoutText);
    }

    [Fact]
    public async Task Model_probe_and_local_inference_when_installed()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "123456:secret\n");
        var report = Path.Combine(temp.Path, "report.md");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--report", report, "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = true,
                ["models"] = new JsonArray { (JsonNode)"qwen2.5:3b" }
            }),
            AskOllama = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = true,
                ["model"] = "qwen2.5:3b",
                ["text"] = "ready"
            })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Allowed);
        Assert.True(result.Local?["ok"]?.GetValue<bool>());
        Assert.Equal("ready", result.Local?["text"]?.ToString());

        var reportContent = File.ReadAllText(report);
        Assert.Contains("Local model reply ok: true", reportContent);
        Assert.Contains("Local model: qwen2.5:3b", reportContent);
        Assert.Contains("Local reply:\nready", reportContent);
    }

    [Fact]
    public async Task Local_model_failure_recorded_properly()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "123456:secret\n");
        var report = Path.Combine(temp.Path, "report.md");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--report", report, "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = true,
                ["models"] = new JsonArray { (JsonNode)"qwen2.5:3b" }
            }),
            AskOllama = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["ok"] = false,
                ["model"] = "qwen2.5:3b",
                ["reason"] = "timeout"
            })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Local?["ok"]?.GetValue<bool>());
        var reportContent = File.ReadAllText(report);
        Assert.Contains("Local model reply ok: false", reportContent);
        Assert.DoesNotContain("Local reply:\n", reportContent);
    }

    [Fact]
    public async Task Live_exchange_requested_and_executed()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "123456:secret\n");

        var fetchCalled = false;
        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--live", "yes", "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["models"] = new JsonArray() }),
            FetchImpl = url =>
            {
                fetchCalled = true;
                var res = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true,\"result\":[]}")
                };
                return Task.FromResult(res);
            }
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.True(fetchCalled);
        Assert.True(result.Exchange?.Ran);
        Assert.True(result.Exchange?.Ok);
    }

    [Fact]
    public async Task Live_exchange_blocked_when_not_allowed()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        // No token, so not allowed

        var fetchCalled = false;
        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--live", "yes", "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["models"] = new JsonArray() }),
            FetchImpl = url => { fetchCalled = true; throw new InvalidOperationException("must not fetch"); }
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.False(fetchCalled);
        Assert.False(result.Exchange?.Ran);
        Assert.Equal("blocked", result.Exchange?.Reason);
    }

    [Fact]
    public async Task Report_collision_when_report_extension_is_not_md()
    {
        using var temp = new DisposableTempDirectory();
        var attempts = Path.Combine(temp.Path, "attempts");
        Directory.CreateDirectory(attempts);
        File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "123456:secret\n");
        var report = Path.Combine(temp.Path, "report.txt");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--attempts", attempts, "--report", report, "--build", @"C:\Build\Planwharf.exe" },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["models"] = new JsonArray() })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        // report.txt was overwritten by JSON because Regex replaces .md with .json
        var content = File.ReadAllText(report);
        Assert.StartsWith("{", content.TrimStart());
        Assert.Contains("\"allowed\": true", content);
    }

    [Fact]
    public async Task Bad_listeners_file_fails_with_exit_code_1_and_stderr()
    {
        using var temp = new DisposableTempDirectory();
        var listenersFile = Path.Combine(temp.Path, "listeners.json");
        File.WriteAllText(listenersFile, "{\n"); // Malformed JSON

        var stderr = new List<string>();
        var options = new PhoneActivationCheckOptions
        {
            Argv = new[] { "--listeners", listenersFile },
            StderrWrite = s => stderr.Add(s)
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public async Task Backup_and_isolated_notebook_created()
    {
        using var temp = new DisposableTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plan.json"), "{\"schema\":2,\"projects\":[]}\n");

        var starter = Path.Combine(temp.Path, "starter.json");
        File.WriteAllText(starter, "{\"schema\":2,\"projects\":[]}\n");

        var backupTo = Path.Combine(temp.Path, "backup");
        var notebook = Path.Combine(temp.Path, "isolated");

        var options = new PhoneActivationCheckOptions
        {
            Argv = new[]
            {
                "--from", source,
                "--backup-to", backupTo,
                "--notebook", notebook,
                "--starter", starter,
                "--build", @"C:\Build\Planwharf.exe"
            },
            ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["models"] = new JsonArray() })
        };

        var result = await PhoneActivationCheck.RunAsync(options);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(backupTo, "plan.json")));
        Assert.True(File.Exists(Path.Combine(notebook, "plan.json")));
        Assert.True(File.Exists(Path.Combine(notebook, ".attempts-plan", "phone-settings.json")));
    }

    [Fact]
    public async Task Concurrent_isolated_checks_run_without_interference()
    {
        using var temp = new DisposableTempDirectory();
        var tasks = new[] { "lane-1", "lane-2" }.Select(async lane =>
        {
            var laneDir = Path.Combine(temp.Path, lane);
            Directory.CreateDirectory(laneDir);
            var report = Path.Combine(laneDir, "report.md");
            var options = new PhoneActivationCheckOptions
            {
                Argv = new[] { "--attempts", laneDir, "--report", report },
                ListInstalledModels = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["models"] = new JsonArray() })
            };
            return await PhoneActivationCheck.RunAsync(options);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.Equal(0, r.ExitCode));
        Assert.All(results, r => Assert.False(r.Allowed));
    }

    private sealed class DisposableTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pw-act-test-" + Guid.NewGuid().ToString("n"));

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
