using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class AgyFakeTests : IDisposable
{
    private static readonly string AgyFakesDir = FakePaths.FakeDir("agy");
    private readonly string _tempDir;

    public AgyFakeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "planwharf-fake-agy-test-" + Guid.NewGuid().ToString("N"));
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
        var filePath = Path.Combine(AgyFakesDir, "worker-success.json");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var result = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(result);

        var identity = new JsonObject
        {
            ["resultId"] = "R-attempt-4",
            ["attemptId"] = "attempt-4",
            ["subTaskId"] = "MT-04-ST-01",
            ["megaTaskId"] = "MT-04"
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
    public void WorkerFail_ContainsResultErrorStatus()
    {
        var filePath = Path.Combine(AgyFakesDir, "worker-fail.jsonl");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath).Trim();
        var node = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal("result", node["event"]?.GetValue<string>());

        var result = node["result"] as JsonObject;
        Assert.NotNull(result);
        Assert.Equal("ERROR", result["status"]?.GetValue<string>());
        Assert.NotEmpty(result["error"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void LeadNormal_ContainsInitStepUpdateAndSuccessResult()
    {
        var filePath = Path.Combine(AgyFakesDir, "lead-normal.jsonl");
        Assert.True(File.Exists(filePath));

        var lines = File.ReadAllLines(filePath);
        Assert.True(lines.Length >= 3);

        var first = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("init", first["event"]?.GetValue<string>());

        var last = JsonNode.Parse(lines[^1]) as JsonObject;
        Assert.NotNull(last);
        Assert.Equal("result", last["event"]?.GetValue<string>());
        Assert.Equal("SUCCESS", last["result"]?["status"]?.GetValue<string>());
    }

    [Fact]
    public void RefusalQuota_MatchesDiagnosticQuotaPattern()
    {
        var filePath = Path.Combine(AgyFakesDir, "refusal-quota.jsonl");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var quotaRegex = new Regex(@"429|quota|credit|spending\.limit|rate\.limit", RegexOptions.IgnoreCase);
        Assert.Matches(quotaRegex, text);
    }

    [Fact]
    public void ModelList_MatchesEngineDiscoveryRegex()
    {
        var filePath = Path.Combine(AgyFakesDir, "models.txt");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var stripped = Regex.Replace(text, @"\x1b\[[0-9;]*m", "");
        var matches = Regex.Matches(stripped, @"\b(?:gemini|claude|gpt-oss)-[a-zA-Z0-9][a-zA-Z0-9._:-]*");
        Assert.NotEmpty(matches);

        var ids = new HashSet<string>();
        foreach (Match m in matches) ids.Add(m.Value);

        Assert.Contains("gemini-3.8-flash-high", ids);
        Assert.Contains("gemini-2.5-pro", ids);
    }

    [Fact]
    public void UsageJson_ContainsValidGeminiBuckets()
    {
        var filePath = Path.Combine(AgyFakesDir, "usage.json");
        Assert.True(File.Exists(filePath));

        var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal("SUCCESS", node["status"]?.GetValue<string>());

        var command = node["command"] as JsonObject;
        Assert.NotNull(command);
        Assert.Equal("usage", command["name"]?.GetValue<string>());

        var groups = command["data"]?["groups"] as JsonArray;
        Assert.NotNull(groups);
        Assert.NotEmpty(groups);

        foreach (var group in groups)
        {
            var buckets = group?["buckets"] as JsonArray;
            if (buckets == null) continue;
            foreach (var b in buckets)
            {
                var frac = b?["remaining_fraction"]?.GetValue<double>() ?? -1;
                Assert.InRange(frac, 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void AgyScenario_ExecutesViaFakeCli()
    {
        var scenarioPath = Path.Combine(AgyFakesDir, "scenario.json");
        Assert.True(File.Exists(scenarioPath));

        var resultFile = Path.Combine(_tempDir, "result.json");

        using var inReader = new StringReader("{\"event\":\"user\",\"message\":{\"content\":\"Implement task\"}}");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "--model", "gemini-3.8-flash-high", "--result", resultFile },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_AS"] = "agy",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        Assert.True(File.Exists(resultFile));

        var written = JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject;
        Assert.NotNull(written);
        Assert.Equal("COMPLETE", written["status"]?.GetValue<string>());
    }

    [Fact]
    public void AgyScenario_UsageCommand_ReturnsValidJson()
    {
        var scenarioPath = Path.Combine(AgyFakesDir, "scenario.json");
        Assert.True(File.Exists(scenarioPath));

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "-p", "/usage", "--output-format", "json" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_AS"] = "agy",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        var output = outWriter.ToString();
        var node = JsonNode.Parse(output) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal("SUCCESS", node["status"]?.GetValue<string>());
    }

    [Fact]
    public void AgyScenario_ModelsCommand_ReturnsExactIds()
    {
        var scenarioPath = Path.Combine(AgyFakesDir, "scenario.json");
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
                ["PLANWHARF_FAKE_AS"] = "agy",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        var output = outWriter.ToString();
        Assert.Contains("gemini-3.8-flash-high", output);
    }
}
