using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class GrokFakeTests : IDisposable
{
    private static readonly string GrokFakesDir = FakePaths.FakeDir("grok");
    private readonly string _tempDir;

    public GrokFakeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "planwharf-fake-grok-test-" + Guid.NewGuid().ToString("N"));
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
        var filePath = Path.Combine(GrokFakesDir, "worker-success.json");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var result = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(result);

        var identity = new JsonObject
        {
            ["resultId"] = "R-attempt-3",
            ["attemptId"] = "attempt-3",
            ["subTaskId"] = "MT-03-ST-01",
            ["megaTaskId"] = "MT-03"
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
    public void WorkerFail_ContainsFailureMessage()
    {
        var filePath = Path.Combine(GrokFakesDir, "worker-fail.txt");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        Assert.Contains("failed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeadNormal_ContainsStreamEventAndResult()
    {
        var filePath = Path.Combine(GrokFakesDir, "lead-normal.jsonl");
        Assert.True(File.Exists(filePath));

        var lines = File.ReadAllLines(filePath);
        Assert.True(lines.Length >= 2);

        var first = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("stream_event", first["type"]?.GetValue<string>());

        var last = JsonNode.Parse(lines[^1]) as JsonObject;
        Assert.NotNull(last);
        Assert.Equal("result", last["type"]?.GetValue<string>());
        Assert.True(last["duration_ms"]?.GetValue<double>() > 0);
    }

    [Fact]
    public void RefusalQuota_MatchesDiagnosticQuotaPattern()
    {
        var filePath = Path.Combine(GrokFakesDir, "refusal-quota.txt");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var quotaRegex = new Regex(@"429|quota|credit|spending\.limit|rate\.limit", RegexOptions.IgnoreCase);
        Assert.Matches(quotaRegex, text);
    }

    [Fact]
    public void ModelList_MatchesEngineDiscoveryRegex()
    {
        var filePath = Path.Combine(GrokFakesDir, "models.txt");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var stripped = Regex.Replace(text, @"\x1b\[[0-9;]*m", "");
        var matches = Regex.Matches(stripped, @"\bgrok-[a-zA-Z0-9][a-zA-Z0-9._:-]*");
        Assert.NotEmpty(matches);

        var ids = new HashSet<string>();
        foreach (Match m in matches) ids.Add(m.Value);

        Assert.Contains("grok-4.7", ids);
        Assert.Contains("grok-beta", ids);
    }

    [Fact]
    public void UsageJson_ContainsValidCreditUsage()
    {
        var filePath = Path.Combine(GrokFakesDir, "usage.json");
        Assert.True(File.Exists(filePath));

        var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        Assert.NotNull(node);

        var config = node["config"] as JsonObject;
        Assert.NotNull(config);
        var creditPct = config["creditUsagePercent"]?.GetValue<double>() ?? -1;
        Assert.InRange(creditPct, 0, 100);

        var currentPeriod = config["currentPeriod"] as JsonObject;
        Assert.NotNull(currentPeriod);
        Assert.NotNull(currentPeriod["start"]?.GetValue<string>());
        Assert.NotNull(currentPeriod["end"]?.GetValue<string>());
    }

    [Fact]
    public void GrokScenario_ExecutesViaFakeCli()
    {
        var scenarioPath = Path.Combine(GrokFakesDir, "scenario.json");
        Assert.True(File.Exists(scenarioPath));

        var resultFile = Path.Combine(_tempDir, "result.json");
        var promptFile = Path.Combine(_tempDir, "prompt.txt");
        File.WriteAllText(promptFile, "Implement grok worker task");

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "--cwd", _tempDir, "-m", "grok-4.7", "--prompt-file", promptFile, "--result", resultFile },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_AS"] = "grok",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        Assert.True(File.Exists(resultFile));

        var written = JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject;
        Assert.NotNull(written);
        Assert.Equal("COMPLETE", written["status"]?.GetValue<string>());
    }

    [Fact]
    public void GrokScenario_ModelsCommand_ReturnsExactIds()
    {
        var scenarioPath = Path.Combine(GrokFakesDir, "scenario.json");
        Assert.True(File.Exists(scenarioPath));

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "models" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_AS"] = "grok",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        var output = outWriter.ToString();
        Assert.Contains("grok-4.7", output);
    }
}
