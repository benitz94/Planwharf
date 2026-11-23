// Tests for PhoneActivation.cs.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class PhoneActivationTests : IDisposable
{
    private readonly string _tempDir;

    public PhoneActivationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pw-activation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void SamePath_CaseInsensitiveOnWindows_MatchesNormalizedPaths()
    {
        var abs = Path.GetFullPath("validation/phone-activation-native/Planwharf.exe");
        var rel = "validation/phone-activation-native/Planwharf.exe";
        var withWhitespace = $"  {rel}  ";
        var windowsRel = rel.Replace('/', '\\');

        Assert.True(PhoneActivation.SamePath(rel, abs));
        Assert.True(PhoneActivation.SamePath(withWhitespace, abs));
        Assert.True(PhoneActivation.SamePath(windowsRel, abs));
        Assert.True(PhoneActivation.SamePath(rel, windowsRel));
        Assert.False(PhoneActivation.SamePath("", abs));
        Assert.False(PhoneActivation.SamePath(null, abs));
        Assert.False(PhoneActivation.SamePath("D:/another/path/Planwharf.exe", abs));
    }

    [Fact]
    public void AssessPairedActivation_ActiveInstalledListener_BlocksLiveExchange()
    {
        const string installed = @"C:\Users\benit\Downloads\Planwharf-native-win-x64-v0.2.85\Planwharf.exe";
        var blocked = PhoneActivation.AssessPairedActivation(new JsonObject
        {
            ["listeners"] = new JsonArray
            {
                new JsonObject
                {
                    ["running"] = true,
                    ["pollsTelegram"] = true,
                    ["path"] = installed,
                    ["pid"] = 13540
                }
            },
            ["thisBuildPath"] = @"D:\benit\Desktop\Projects\Planwharf-engine\validation\phone-activation-native\Planwharf.exe",
            ["tokenPresent"] = true,
            ["isolatedHasToken"] = false
        });

        Assert.False(blocked.Allowed);
        Assert.Contains("already polling Telegram", blocked.Blocker);
        Assert.Contains("getUpdates", blocked.Blocker);
        Assert.Single(blocked.InstalledListeners);
        Assert.Equal(13540, blocked.InstalledListeners[0]["pid"]?.GetValue<int>());
        Assert.Equal(PhoneActivation.DefaultOllamaModel, blocked.Model);

        var asObject = PhoneActivation.AssessPairedActivation(new JsonObject
        {
            ["listeners"] = new JsonObject
            {
                ["running"] = true,
                ["pollsTelegram"] = true,
                ["path"] = installed,
                ["pid"] = 13540
            },
            ["tokenPresent"] = true
        });
        Assert.False(asObject.Allowed);
        Assert.Single(asObject.InstalledListeners);

        var clear = PhoneActivation.AssessPairedActivation(new JsonObject
        {
            ["listeners"] = new JsonArray
            {
                new JsonObject
                {
                    ["running"] = false,
                    ["pollsTelegram"] = true,
                    ["path"] = installed,
                    ["pid"] = 1
                }
            },
            ["tokenPresent"] = true,
            ["isolatedHasToken"] = false,
            ["model"] = "qwen2.5:3b"
        });
        Assert.True(clear.Allowed);
        Assert.Empty(clear.Blocker);
    }

    [Fact]
    public void AssessPairedActivation_TokenConditions()
    {
        var withToken = PhoneActivation.AssessPairedActivation(new JsonObject
        {
            ["tokenPresent"] = true,
            ["isolatedHasToken"] = true,
            ["listeners"] = new JsonArray()
        });
        Assert.False(withToken.Allowed);
        Assert.Contains("isolated validation notebook contains a bot token", withToken.Blocker);

        var noToken = PhoneActivation.AssessPairedActivation(new JsonObject
        {
            ["tokenPresent"] = false,
            ["isolatedHasToken"] = false,
            ["listeners"] = new JsonArray()
        });
        Assert.False(noToken.Allowed);
        Assert.Contains("No bot token", noToken.Blocker);

        var ownProcess = PhoneActivation.AssessPairedActivation(new JsonObject
        {
            ["listeners"] = new JsonArray
            {
                new JsonObject
                {
                    ["running"] = true,
                    ["pollsTelegram"] = true,
                    ["path"] = "D:/build/Planwharf.exe",
                    ["pid"] = 9
                }
            },
            ["thisBuildPath"] = @"D:\build\Planwharf.exe",
            ["tokenPresent"] = true
        });
        Assert.True(ownProcess.Allowed);
    }

    [Fact]
    public void WriteIsolatedNotebook_GeneratesIsolatedSettingsWithoutToken()
    {
        var notebook = Path.Combine(_tempDir, "notebook");
        var starter = Path.Combine(_tempDir, "starter.json");
        File.WriteAllText(starter, "{\"schema\":2,\"projects\":[]}\n");

        var made = PhoneActivation.WriteIsolatedNotebook(notebook, starter);
        Assert.True(File.Exists(made.Plan));

        var settingsPath = Path.Combine(made.Attempts, "phone-settings.json");
        Assert.True(File.Exists(settingsPath));
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(settings);
        Assert.Equal("qwen2.5:3b", settings!["ollama"]?["model"]?.ToString());
        Assert.Equal(false, settings["telegram"]?["enabled"]?.GetValue<bool>());

        var tokenPath = Path.Combine(made.Attempts, "telegram-bot.token");
        Assert.False(File.Exists(tokenPath));

        File.WriteAllText(tokenPath, "secret");
        PhoneActivation.WriteIsolatedNotebook(notebook, starter);
        Assert.False(File.Exists(tokenPath));
    }

    [Fact]
    public void CopyBackup_CopiesPlanPhoneSettingsAndTokenBetweenNotebooks()
    {
        var from = Path.Combine(_tempDir, "from");
        var backup = Path.Combine(_tempDir, "backup");
        Directory.CreateDirectory(from);

        File.WriteAllText(Path.Combine(from, "plan.json"), "{\"schema\":2,\"projects\":[]}\n");
        File.WriteAllText(Path.Combine(from, "phone-settings.json"), "{\"telegram\":{\"enabled\":true}}\n");
        File.WriteAllText(Path.Combine(from, "telegram-bot.token"), "123456:secret\n");

        var copied = PhoneActivation.CopyBackup(from, backup);
        Assert.Equal(3, copied.Files.Count);
        Assert.Equal(File.ReadAllText(Path.Combine(from, "plan.json")), File.ReadAllText(Path.Combine(backup, "plan.json")));

        var manifestPath = Path.Combine(backup, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        Assert.NotNull(manifest);
        var filesArr = manifest!["files"] as JsonArray;
        Assert.NotNull(filesArr);
        Assert.Equal(3, filesArr!.Count);
        Assert.All(filesArr, f => Assert.Equal(64, f!["sha256"]?.ToString().Length));
    }

    [Fact]
    public async Task LiveExchangeAsync_TestsBlockedAndOpen()
    {
        var called = 0;
        var blocked = await PhoneActivation.LiveExchangeAsync(
            allowed: false,
            token: "123456:secret",
            fetchImpl: _ => { called++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        Assert.False(blocked.Ran);
        Assert.Equal(0, called);

        var open = await PhoneActivation.LiveExchangeAsync(
            allowed: true,
            token: "123456:secret",
            fetchImpl: url =>
            {
                called++;
                Assert.Contains("bot123456:secret/getUpdates", url);
                var content = new StringContent("{\"ok\":true,\"result\":[{\"update_id\":1}]}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
        Assert.True(open.Ran);
        Assert.True(open.Ok);
        Assert.Equal(1, open.Updates);
    }

    [Fact]
    public void ReportMarkdown_GeneratesReport()
    {
        var report = PhoneActivation.ReportMarkdown(new JsonObject
        {
            ["assessment"] = new JsonObject
            {
                ["allowed"] = false,
                ["model"] = "qwen2.5:3b",
                ["blocker"] = "listener"
            },
            ["probe"] = new JsonObject { ["installed"] = true },
            ["backup"] = new JsonObject
            {
                ["files"] = new JsonArray
                {
                    new JsonObject { ["name"] = "plan.json" }
                }
            },
            ["exchange"] = new JsonObject { ["ran"] = false },
            ["build"] = "native"
        });

        Assert.Contains("qwen2.5:3b", report);
        Assert.Contains("does not call Telegram getUpdates", report);
        Assert.Contains("plan.json", report);
        Assert.Contains("Native build: native", report);
    }
}
