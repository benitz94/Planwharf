using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class CodexFakeTests : IDisposable
{
    private static readonly string CodexFakesDir = FakePaths.FakeDir("codex");
    private readonly string _tempDir;

    public CodexFakeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "planwharf-fake-codex-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void WorkerSuccess_ConformsToStructuredFinalResultSchema()
    {
        var filePath = Path.Combine(CodexFakesDir, "worker-success.json");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var result = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(result);

        var identity = new JsonObject
        {
            ["resultId"] = "R-attempt-2",
            ["attemptId"] = "attempt-2",
            ["subTaskId"] = "MT-02-ST-01",
            ["megaTaskId"] = "MT-02"
        };
        var schema = StructuredFinal.ResultSchema(identity);
        var required = schema["required"] as JsonArray;
        Assert.NotNull(required);
        foreach (var req in required)
        {
            var key = req?.GetValue<string>()!;
            Assert.True(result[key] != null, $"Missing required property in result: {key}");
        }

        Assert.Equal("COMPLETE", result["status"]?.GetValue<string>());
        var tests = result["tests"] as JsonArray;
        Assert.NotNull(tests);
        Assert.NotEmpty(tests);
    }

    [Fact]
    public void WorkerFail_ContainsTurnFailed()
    {
        var filePath = Path.Combine(CodexFakesDir, "worker-fail.jsonl");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath).Trim();
        var node = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal("turn.failed", node["type"]?.GetValue<string>());

        var error = node["error"] as JsonObject;
        Assert.NotNull(error);
        Assert.NotEmpty(error["message"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void LeadNormal_ContainsAgentMessageAndTurnCompleted()
    {
        var filePath = Path.Combine(CodexFakesDir, "lead-normal.jsonl");
        Assert.True(File.Exists(filePath));

        var lines = File.ReadAllLines(filePath);
        Assert.True(lines.Length >= 2);

        var first = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("item.completed", first["type"]?.GetValue<string>());
        Assert.Equal("agent_message", first["item"]?["type"]?.GetValue<string>());
        Assert.NotEmpty(first["item"]?["text"]?.GetValue<string>() ?? "");

        var second = JsonNode.Parse(lines[1]) as JsonObject;
        Assert.NotNull(second);
        Assert.Equal("turn.completed", second["type"]?.GetValue<string>());
        Assert.NotNull(second["usage"]?["input_tokens"]);
    }

    [Fact]
    public void RefusalQuota_MatchesDiagnosticQuotaPattern()
    {
        var filePath = Path.Combine(CodexFakesDir, "refusal-quota.jsonl");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var quotaRegex = new Regex(@"429|quota|credit|spending\.limit|rate\.limit", RegexOptions.IgnoreCase);
        Assert.Matches(quotaRegex, text);
    }

    [Fact]
    public void ModelList_MatchesNativeCodexModelPrefix()
    {
        var filePath = Path.Combine(CodexFakesDir, "models.json");
        Assert.True(File.Exists(filePath));

        var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        Assert.NotNull(node);
        var models = node["models"] as JsonArray;
        Assert.NotNull(models);
        Assert.NotEmpty(models);

        var gptRegex = new Regex(@"^gpt-");
        var matched = 0;
        foreach (var m in models)
        {
            var slug = m?["slug"]?.GetValue<string>();
            if (slug != null && gptRegex.IsMatch(slug)) matched++;
        }
        Assert.True(matched > 0, "At least one model must start with gpt-");
    }

    [Fact]
    public void UsageJson_ContainsValidRateLimitWindows()
    {
        var filePath = Path.Combine(CodexFakesDir, "usage.json");
        Assert.True(File.Exists(filePath));

        var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        Assert.NotNull(node);

        var rl = node["rate_limit"] as JsonObject;
        Assert.NotNull(rl);
        Assert.Equal(false, rl["limit_reached"]?.GetValue<bool>());

        var primary = rl["primary_window"] as JsonObject;
        Assert.NotNull(primary);
        var pct1 = primary["used_percent"]?.GetValue<double>() ?? -1;
        Assert.InRange(pct1, 0, 100);

        var secondary = rl["secondary_window"] as JsonObject;
        Assert.NotNull(secondary);
        var pct2 = secondary["used_percent"]?.GetValue<double>() ?? -1;
        Assert.InRange(pct2, 0, 100);
    }

    [Fact]
    public void CodexScenario_ExecutesViaFakeCli()
    {
        var scenarioPath = Path.Combine(CodexFakesDir, "scenario.json");
        Assert.True(File.Exists(scenarioPath));

        var resultFile = Path.Combine(_tempDir, "result-output.json");

        using var inReader = new StringReader("Implement feature task");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "exec", "-C", _tempDir, "-m", "gpt-5-codex", "--result", resultFile, "-" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_AS"] = "codex",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        Assert.True(File.Exists(resultFile));

        var written = JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject;
        Assert.NotNull(written);
        Assert.Equal("COMPLETE", written["status"]?.GetValue<string>());
    }
}
