using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class FakeCliCoreTests : IDisposable
{
    private readonly string _tempDir;

    public FakeCliCoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "planwharf-fake-core-test-" + Guid.NewGuid().ToString("N"));
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

    private string CreateScenario(string json)
    {
        var path = Path.Combine(_tempDir, "scenario-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void NoScenario_ExitsWithCode2AndExplainsOnStderr()
    {
        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "arg1" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>());

        Assert.Equal(2, code);
        Assert.Contains("no scenario file specified or found", errWriter.ToString());
    }

    [Fact]
    public void NoAnswerApplies_ExitsWithCode2AndExplainsOnStderr()
    {
        var scenario = CreateScenario("""
        [
          {
            "argContains": "expected-token",
            "stdout": "Hello"
          }
        ]
        """);

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "different-arg" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_SCENARIO"] = scenario
            });

        Assert.Equal(2, code);
        Assert.Contains("no answer applies for call #1", errWriter.ToString());
    }

    [Fact]
    public void ChooseAnswer_ByArgument()
    {
        var scenario = CreateScenario("""
        [
          {
            "argContains": "first-command",
            "stdout": "First answer"
          },
          {
            "argContains": "second-command",
            "stdout": "Second answer"
          }
        ]
        """);

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "run", "second-command", "--flag" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_SCENARIO"] = scenario
            });

        Assert.Equal(0, code);
        Assert.Equal("Second answer\r\n", outWriter.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    [Fact]
    public void ChooseAnswer_ByPrompt_FromStdin()
    {
        var scenario = CreateScenario("""
        [
          {
            "promptContains": "question A",
            "stdout": "Answer A"
          },
          {
            "promptContains": "question B",
            "stdout": "Answer B"
          }
        ]
        """);

        using var inReader = new StringReader("Please resolve question B immediately");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "-p" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_SCENARIO"] = scenario
            });

        Assert.Equal(0, code);
        Assert.Contains("Answer B", outWriter.ToString());
    }

    [Fact]
    public void ChooseAnswer_ByPrompt_FromPromptFile()
    {
        var promptPath = Path.Combine(_tempDir, "prompt.txt");
        File.WriteAllText(promptPath, "Detailed prompt mentioning secret-key-42");

        var scenario = CreateScenario("""
        [
          {
            "promptContains": "secret-key-42",
            "stdout": "Found prompt from file"
          }
        ]
        """);

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "--prompt-file", promptPath },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_SCENARIO"] = scenario
            });

        Assert.Equal(0, code);
        Assert.Contains("Found prompt from file", outWriter.ToString());
    }

    [Fact]
    public void ChooseAnswer_ByCallNumber()
    {
        var logFile = Path.Combine(_tempDir, "call.log");
        var scenario = CreateScenario("""
        [
          {
            "callNumber": 1,
            "stdout": "First call response"
          },
          {
            "callNumber": 2,
            "stdout": "Second call response"
          }
        ]
        """);

        var env = new Dictionary<string, string?>
        {
            ["PLANWHARF_FAKE_SCENARIO"] = scenario,
            ["PLANWHARF_FAKE_LOG"] = logFile
        };

        // First call
        using (var inReader1 = new StringReader(""))
        using (var outWriter1 = new StringWriter())
        using (var errWriter1 = new StringWriter())
        {
            var code1 = Program.Run(new[] { "call" }, inReader1, outWriter1, errWriter1, env);
            Assert.Equal(0, code1);
            Assert.Contains("First call response", outWriter1.ToString());
        }

        // Second call
        using (var inReader2 = new StringReader(""))
        using (var outWriter2 = new StringWriter())
        using (var errWriter2 = new StringWriter())
        {
            var code2 = Program.Run(new[] { "call" }, inReader2, outWriter2, errWriter2, env);
            Assert.Equal(0, code2);
            Assert.Contains("Second call response", outWriter2.ToString());
        }
    }

    [Fact]
    public void FilesWritten_DirectPath_And_PathAfterArg()
    {
        var directPath = Path.Combine(_tempDir, "direct-out.txt");
        var afterArgPath = Path.Combine(_tempDir, "after-arg.json");

        var scenario = CreateScenario($$"""
        [
          {
            "files": [
              {
                "path": {{JsonValue.Create(directPath)!.ToJsonString()}},
                "content": "direct file content"
              },
              {
                "pathAfterArg": "--result",
                "content": "{\"status\":\"COMPLETE\"}"
              }
            ],
            "stdout": "Files written"
          }
        ]
        """);

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var code = Program.Run(
            new[] { "--task", "work", "--result", afterArgPath },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_SCENARIO"] = scenario
            });

        Assert.Equal(0, code);
        Assert.True(File.Exists(directPath));
        Assert.Equal("direct file content", File.ReadAllText(directPath));

        Assert.True(File.Exists(afterArgPath));
        Assert.Equal("{\"status\":\"COMPLETE\"}", File.ReadAllText(afterArgPath));
    }

    [Fact]
    public void CallLog_AppendsJsonLine_WithExpectedFields()
    {
        var logFile = Path.Combine(_tempDir, "audit.log");
        var scenario = CreateScenario("""
        [
          {
            "stdout": "logged"
          }
        ]
        """);

        using var inReader = new StringReader("test standard input");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var env = new Dictionary<string, string?>
        {
            ["PLANWHARF_FAKE_SCENARIO"] = scenario,
            ["PLANWHARF_FAKE_LOG"] = logFile,
            ["PLANWHARF_FAKE_AS"] = "claude",
            ["PLANWHARF_TEST_FLAG"] = "planwharf-value",
            ["CODEX_KEY"] = "codex-value",
            ["CLAUDE_TOKEN"] = "claude-value",
            ["OTHER_VAR"] = "should-not-be-in-env"
        };

        var code = Program.Run(new[] { "--foo", "bar with space" }, inReader, outWriter, errWriter, env);
        Assert.Equal(0, code);

        Assert.True(File.Exists(logFile));
        var lines = File.ReadAllLines(logFile);
        Assert.Single(lines);

        var logEntry = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(logEntry);

        Assert.Equal("claude", logEntry["fakeAs"]?.GetValue<string>());
        Assert.Equal("test standard input", logEntry["stdin"]?.GetValue<string>());
        Assert.Equal(1, logEntry["callNumber"]?.GetValue<int>());
        Assert.NotNull(logEntry["cwd"]?.GetValue<string>());

        var argsNode = logEntry["args"] as JsonArray;
        Assert.NotNull(argsNode);
        Assert.Equal(2, argsNode.Count);
        Assert.Equal("--foo", argsNode[0]?.GetValue<string>());
        Assert.Equal("bar with space", argsNode[1]?.GetValue<string>());

        var envNode = logEntry["env"] as JsonObject;
        Assert.NotNull(envNode);
        Assert.Equal("planwharf-value", envNode["PLANWHARF_TEST_FLAG"]?.GetValue<string>());
        Assert.Equal("codex-value", envNode["CODEX_KEY"]?.GetValue<string>());
        Assert.Equal("claude-value", envNode["CLAUDE_TOKEN"]?.GetValue<string>());
        Assert.False(envNode.ContainsKey("OTHER_VAR"));
    }

    [Fact]
    public void ExitCodes_And_PauseBeforeAnswering()
    {
        var scenario = CreateScenario("""
        [
          {
            "pauseMs": 150,
            "stdout": "Delayed answer",
            "stderr": "Delayed warning",
            "exitCode": 7
          }
        ]
        """);

        using var inReader = new StringReader("");
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var sw = Stopwatch.StartNew();
        var code = Program.Run(
            new[] { "run" },
            inReader,
            outWriter,
            errWriter,
            new Dictionary<string, string?>
            {
                ["PLANWHARF_FAKE_SCENARIO"] = scenario
            });
        sw.Stop();

        Assert.Equal(7, code);
        Assert.Contains("Delayed answer", outWriter.ToString());
        Assert.Contains("Delayed warning", errWriter.ToString());
        Assert.True(sw.ElapsedMilliseconds >= 120, $"Elapsed was {sw.ElapsedMilliseconds} ms");
    }
}
