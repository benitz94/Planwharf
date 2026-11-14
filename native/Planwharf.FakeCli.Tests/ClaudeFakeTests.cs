using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class ClaudeFakeTests
{
    private static readonly string ClaudeFakesDir = FakePaths.FakeDir("claude");

    [Fact]
    public void WorkerSuccess_IsAcceptedByStructuredFinalReadFinalResult()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "worker-success.jsonl");
        Assert.True(File.Exists(filePath), $"File not found: {filePath}");

        var output = File.ReadAllText(filePath);
        var result = StructuredFinal.ReadFinalResult("claude", output);
        Assert.NotNull(result);

        Assert.Equal("R-attempt-1", result["resultId"]?.GetValue<string>());
        Assert.Equal("attempt-1", result["attemptId"]?.GetValue<string>());
        Assert.Equal("MT-01-ST-01", result["subTaskId"]?.GetValue<string>());
        Assert.Equal("MT-01", result["megaTaskId"]?.GetValue<string>());
        Assert.Equal("COMPLETE", result["status"]?.GetValue<string>());
        Assert.Equal("Implemented unit tests and validated behavior.", result["summary"]?.GetValue<string>());

        var files = result["filesChanged"] as JsonArray;
        Assert.NotNull(files);
        Assert.Equal(2, files.Count);

        var tests = result["tests"] as JsonArray;
        Assert.NotNull(tests);
        Assert.Single(tests);
        var testObj = tests[0] as JsonObject;
        Assert.NotNull(testObj);
        Assert.Equal("dotnet test", testObj["command"]?.GetValue<string>());
        Assert.Equal("PASSED", testObj["status"]?.GetValue<string>());
    }

    [Fact]
    public void WorkerSuccess_ConformsToStructuredFinalResultSchema()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "worker-success.jsonl");
        var output = File.ReadAllText(filePath);
        var result = StructuredFinal.ReadFinalResult("claude", output);
        Assert.NotNull(result);

        var identity = new JsonObject
        {
            ["resultId"] = "R-attempt-1",
            ["attemptId"] = "attempt-1",
            ["subTaskId"] = "MT-01-ST-01",
            ["megaTaskId"] = "MT-01"
        };
        var schema = StructuredFinal.ResultSchema(identity);
        Assert.NotNull(schema);

        var required = schema["required"] as JsonArray;
        Assert.NotNull(required);
        foreach (var req in required)
        {
            var key = req?.GetValue<string>()!;
            Assert.True(result[key] != null, $"Missing required property in structured_output: {key}");
        }
    }

    [Fact]
    public void WorkerFail_RejectedByStructuredFinalReadFinalResult()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "worker-fail.jsonl");
        Assert.True(File.Exists(filePath));

        var output = File.ReadAllText(filePath);
        var result = StructuredFinal.ReadFinalResult("claude", output);
        Assert.Null(result);

        var lines = output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var last = JsonNode.Parse(lines[^1]) as JsonObject;
        Assert.NotNull(last);
        Assert.Equal(true, last["is_error"]?.GetValue<bool>());
    }

    [Fact]
    public void LeadNormal_ParsesValidStreamJson()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "lead-normal.jsonl");
        Assert.True(File.Exists(filePath));

        var lines = File.ReadAllLines(filePath);
        Assert.True(lines.Length >= 2);

        var first = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("system", first["type"]?.GetValue<string>());
        Assert.Equal("init", first["subtype"]?.GetValue<string>());
        Assert.Matches(@"^claude-", first["model"]?.GetValue<string>() ?? "");

        var last = JsonNode.Parse(lines[^1]) as JsonObject;
        Assert.NotNull(last);
        Assert.Equal("result", last["type"]?.GetValue<string>());
        Assert.Equal(false, last["is_error"]?.GetValue<bool>());
    }

    [Fact]
    public void RefusalQuota_MatchesDiagnosticQuotaPattern()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "refusal-quota.jsonl");
        Assert.True(File.Exists(filePath));

        var text = File.ReadAllText(filePath);
        var quotaRegex = new Regex(@"429|quota|credit|spending\.limit|rate\.limit", RegexOptions.IgnoreCase);
        Assert.Matches(quotaRegex, text);
    }

    [Fact]
    public void ModelList_MatchesClaudeModelRegex()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "models.jsonl");
        Assert.True(File.Exists(filePath));

        var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal("control_response", node["type"]?.GetValue<string>());

        var response = node["response"]?["response"] as JsonObject;
        Assert.NotNull(response);
        var models = response["models"] as JsonArray;
        Assert.NotNull(models);
        Assert.NotEmpty(models);

        var nativeModelRegex = new Regex(@"^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$");
        var matched = 0;
        foreach (var m in models)
        {
            var cli = m?["resolvedModel"]?.GetValue<string>() ?? m?["value"]?.GetValue<string>();
            if (cli != null && nativeModelRegex.IsMatch(cli)) matched++;
        }
        Assert.True(matched > 0, "At least one model must match native claude pattern");
    }

    [Fact]
    public void UsageJson_ParsesValidWindows()
    {
        var filePath = Path.Combine(ClaudeFakesDir, "usage.json");
        Assert.True(File.Exists(filePath));

        var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        Assert.NotNull(node);

        var fiveHour = node["five_hour"] as JsonObject;
        Assert.NotNull(fiveHour);
        var util5h = fiveHour["utilization"]?.GetValue<double>() ?? -1;
        Assert.InRange(util5h, 0, 100);

        var sevenDay = node["seven_day"] as JsonObject;
        Assert.NotNull(sevenDay);
        var util7d = sevenDay["utilization"]?.GetValue<double>() ?? -1;
        Assert.InRange(util7d, 0, 100);
    }

    [Fact]
    public void ClaudeScenario_ExecutesViaFakeCli()
    {
        var scenarioPath = Path.Combine(ClaudeFakesDir, "scenario.json");
        Assert.True(File.Exists(scenarioPath));

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "-p", "--model", "claude-3-7-sonnet-20250219", "--json-schema", "{}" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_AS"] = "claude",
                ["PLANWHARF_FAKE_SCENARIO"] = scenarioPath
            });

        Assert.Equal(0, code);
        var output = outWriter.ToString();
        var result = StructuredFinal.ReadFinalResult("claude", output);
        Assert.NotNull(result);
        Assert.Equal("COMPLETE", result["status"]?.GetValue<string>());
    }
}
