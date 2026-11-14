using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class FakeCliEdgeTests : IDisposable
{
    private readonly string _tempDir;

    public FakeCliEdgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "planwharf-fake-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private string CreateScenario(string json)
    {
        var path = Path.Combine(_tempDir, "scenario-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static int Run(string[] args, string stdin, string scenario, string? log, out string stdout, out string stderr)
    {
        using var inReader = new StringReader(stdin);
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        var env = new Dictionary<string, string?>
        {
            ["PLANWHARF_FAKE_AS"] = "claude",
            ["PLANWHARF_FAKE_SCENARIO"] = scenario
        };
        if (log != null) env["PLANWHARF_FAKE_LOG"] = log;
        var code = Program.Run(args, inReader, outWriter, errWriter, env);
        stdout = outWriter.ToString();
        stderr = errWriter.ToString();
        return code;
    }

    [Fact]
    public void BrokenScenario_Exits2AndExplains()
    {
        var missing = Path.Combine(_tempDir, "missing.json");
        var code = Run(new[] { "run" }, "", missing, null, out _, out var err);
        Assert.Equal(2, code);
        Assert.Contains("no scenario file", err);

        var directory = Path.Combine(_tempDir, "scenario-dir");
        Directory.CreateDirectory(directory);
        code = Run(new[] { "run" }, "", directory, null, out _, out err);
        Assert.Equal(2, code);
        Assert.Contains("no scenario file", err);

        var invalid = CreateScenario("{ this is not json");
        code = Run(new[] { "run" }, "", invalid, null, out _, out err);
        Assert.Equal(2, code);
        Assert.Contains("failed to parse", err);

        foreach (var body in new[] { "[]", "null", "12", """{"note":"no answers field"}""" })
        {
            var path = CreateScenario(body);
            code = Run(new[] { "run" }, "", path, null, out _, out err);
            Assert.Equal(2, code);
            Assert.Contains("no answers", err);
        }
    }

    [Fact]
    public void UnreadableAnswer_DoesNotEscapeRun()
    {
        var scenario = CreateScenario("""
        [
          { "callNumber": "second", "stdout": "bad" },
          { "exitCode": "nope", "stdout": "also bad" }
        ]
        """);

        var code = Run(new[] { "run" }, "", scenario, null, out _, out var err);
        Assert.Equal(2, code);
        Assert.Contains("Planwharf.FakeCli:", err);
    }

    [Fact]
    public void FirstMatchingAnswerWins_AndEveryConditionIsRequired()
    {
        var scenario = CreateScenario("""
        [
          { "argContains": "build", "promptContains": "missing-prompt", "stdout": "too specific" },
          { "argContains": "build", "stdout": "first build" },
          { "argContains": "build", "stdout": "later build" }
        ]
        """);

        var code = Run(new[] { "build", "app" }, "unrelated", scenario, null, out var stdout, out var err);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Equal("first build\n", Norm(stdout));

        scenario = CreateScenario("""
        [
          { "argContains": "build", "promptContains": "ship it", "callNumber": 1, "stdout": "all three" },
          { "argContains": "build", "stdout": "arg only" }
        ]
        """);
        var log = Path.Combine(_tempDir, "and.log");
        code = Run(new[] { "build" }, "please ship it", scenario, log, out stdout, out err);
        Assert.Equal(0, code);
        Assert.Equal("all three\n", Norm(stdout));
    }

    [Fact]
    public void OutputLines_AreRawText_NotJsonEncoded()
    {
        var scenario = CreateScenario("""
        [
          {
            "stdout": ["say \"hi\"", "second"],
            "stderr": ["err \"one\"", "err two"]
          }
        ]
        """);

        var code = Run(Array.Empty<string>(), "", scenario, null, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Equal("say \"hi\"\nsecond\n", Norm(stdout));
        Assert.Equal("err \"one\"\nerr two\n", Norm(stderr));
    }

    [Fact]
    public void NthCall_AcrossInvocations_AndLogSurvivesAPartialLine()
    {
        var log = Path.Combine(_tempDir, "nested missing", "calls.log");
        var scenario = CreateScenario("""
        [
          { "nthCall": 1, "stdout": "one" },
          { "nthCall": 2, "stdout": "two" },
          { "nthCall": 4, "stdout": "four" }
        ]
        """);

        var code = Run(new[] { "a" }, "in-1", scenario, log, out var stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("one\n", Norm(stdout));

        code = Run(new[] { "b" }, "clean-2", scenario, log, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("two\n", Norm(stdout));

        File.AppendAllText(log, "{\"trunc\"");
        code = Run(new[] { "c" }, "in-4", scenario, log, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("four\n", Norm(stdout));

        var lines = Norm(File.ReadAllText(log)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("{\"trunc\"", lines[2]);
        var first = JsonNode.Parse(lines[0]) as JsonObject;
        var second = JsonNode.Parse(lines[1]) as JsonObject;
        var fourth = JsonNode.Parse(lines[3]) as JsonObject;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(fourth);
        Assert.Equal("in-1", first["stdin"]?.GetValue<string>());
        Assert.Equal("clean-2", second["stdin"]?.GetValue<string>());
        Assert.Equal("in-4", fourth["stdin"]?.GetValue<string>());
        Assert.Equal(4, fourth["callNumber"]?.GetValue<int>());
    }

    [Fact]
    public void Files_CreateMissingFolders_ForSpacedAndNonAsciiPaths()
    {
        var folder = Path.Combine(_tempDir, "out put", "名前");
        var target = Path.Combine(folder, "result.json");
        var scenario = CreateScenario("""
        [
          {
            "files": [
              { "pathAfterArg": "--result", "content": "{\"status\":\"COMPLETE\"}" }
            ],
            "stdout": "wrote"
          }
        ]
        """);

        var code = Run(new[] { "--result", target }, "", scenario, null, out _, out var err);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Equal("{\"status\":\"COMPLETE\"}", File.ReadAllText(target));
    }

    [Fact]
    public void FileWriteFailure_IsReported_AndDoesNotThrow()
    {
        var blocked = Path.Combine(_tempDir, "blocked");
        Directory.CreateDirectory(blocked);
        var scenario = CreateScenario($$"""
        [
          {
            "files": [ { "path": {{JsonValue.Create(blocked)!.ToJsonString()}}, "content": "nope" } ],
            "stdout": "should not claim success"
          }
        ]
        """);

        var code = Run(new[] { "run" }, "", scenario, null, out _, out var err);
        Assert.NotEqual(0, code);
        Assert.Contains("Planwharf.FakeCli:", err);
    }

    [Fact]
    public void FailedCall_IsStillOneJsonLineInTheLog()
    {
        var log = Path.Combine(_tempDir, "fail.log");
        var scenario = CreateScenario("""[{ "argContains": "only-this", "stdout": "no" }]""");
        var code = Run(new[] { "other", "café" }, "stdin café", scenario, log, out _, out var err);
        Assert.Equal(2, code);
        Assert.Contains("no answer applies", err);

        var line = JsonNode.Parse(File.ReadAllText(log).Trim()) as JsonObject;
        Assert.NotNull(line);
        Assert.Equal("stdin café", line["stdin"]?.GetValue<string>());
        Assert.Equal("café", (line["args"] as JsonArray)?[1]?.GetValue<string>());
    }

    [Fact]
    public void ConcurrentCalls_AppendDistinctParseableLines()
    {
        var log = Path.Combine(_tempDir, "concurrent.log");
        var scenario = CreateScenario("""[{ "stdout": "ok" }]""");
        const int count = 12;
        var codes = new int[count];
        Parallel.For(0, count, i =>
        {
            codes[i] = Run(new[] { "job", i.ToString() }, "stdin-" + i, scenario, log, out _, out _);
        });

        Assert.All(codes, code => Assert.Equal(0, code));
        var lines = Norm(File.ReadAllText(log)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(count, lines.Length);

        var seen = new HashSet<int>();
        foreach (var line in lines)
        {
            var entry = JsonNode.Parse(line) as JsonObject;
            Assert.NotNull(entry);
            var number = entry["callNumber"]!.GetValue<int>();
            Assert.True(seen.Add(number));
            Assert.StartsWith("stdin-", entry["stdin"]?.GetValue<string>());
        }

        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public void Process_UsesSpacedNonAsciiPaths_AndKeepsASharedLog()
    {
        var home = Path.Combine(_tempDir, "work space", "名前");
        Directory.CreateDirectory(home);
        var scenario = Path.Combine(home, "scenario.json");
        var log = Path.Combine(home, "calls.log");
        var result = Path.Combine(home, "out file.json");
        File.WriteAllText(scenario, """
        [
          {
            "callNumber": 1,
            "stdout": ["first 名前"],
            "files": [ { "pathAfterArg": "--result", "content": "one" } ]
          },
          { "callNumber": 2, "stdout": ["second 名前"] },
          { "stdout": "later" }
        ]
        """);

        var first = StartFake(home, scenario, log, new[] { "--result", result }, "prompt 名前");
        Assert.Equal(0, first.Code);
        Assert.Equal("", first.Stderr);
        Assert.Equal("first 名前\n", Norm(first.Stdout));
        Assert.Equal("one", File.ReadAllText(result));

        var second = StartFake(home, scenario, log, new[] { "again" }, "second stdin");
        Assert.Equal(0, second.Code);
        Assert.Equal("second 名前\n", Norm(second.Stdout));

        var parallel = new (int Code, string Stdout, string Stderr)[4];
        Parallel.For(0, 4, i =>
        {
            parallel[i] = StartFake(home, scenario, log, new[] { "extra", i.ToString() }, "p-" + i);
        });
        Assert.All(parallel, item =>
        {
            Assert.Equal(0, item.Code);
            Assert.Equal("later\n", Norm(item.Stdout));
        });

        var lines = Norm(File.ReadAllText(log)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        var seen = new HashSet<int>();
        foreach (var line in lines)
        {
            var entry = JsonNode.Parse(line) as JsonObject;
            Assert.NotNull(entry);
            Assert.True(seen.Add(entry["callNumber"]!.GetValue<int>()));
            Assert.Equal("from-parent", entry["env"]?["PLANWHARF_SAMPLE"]?.GetValue<string>());
            // On macOS the temp folder /var/... is a link to /private/var/...: the program reports the real path.
            Assert.Equal(
                RealPath(home),
                RealPath(entry["cwd"]!.GetValue<string>()),
                ignoreCase: OperatingSystem.IsWindows());
        }

        var firstEntry = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.Equal("prompt 名前", firstEntry?["stdin"]?.GetValue<string>());
        Assert.Equal(result, (firstEntry?["args"] as JsonArray)?[1]?.GetValue<string>());
        Assert.Equal(6, seen.Count);
    }

    private static string FakeExe()
    {
        var name = OperatingSystem.IsWindows() ? "Planwharf.FakeCli.exe" : "Planwharf.FakeCli";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), path);
        return path;
    }

    private static (int Code, string Stdout, string Stderr) StartFake(
        string cwd, string scenario, string log, string[] args, string stdin)
    {
        var start = new ProcessStartInfo(FakeExe())
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["PLANWHARF_FAKE_AS"] = "grok";
        start.Environment["PLANWHARF_FAKE_SCENARIO"] = scenario;
        start.Environment["PLANWHARF_FAKE_LOG"] = log;
        start.Environment["PLANWHARF_SAMPLE"] = "from-parent";

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(20000), "fake CLI did not exit");
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    // The same folder reached through a link has one real path (macOS: /var -> /private/var).
    static string RealPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsMacOS() && full.StartsWith("/var/", StringComparison.Ordinal)) full = "/private" + full;
        return full;
    }
}
