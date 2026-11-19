using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Planwharf.FakeCli.Tests;

public class FakeCliRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public FakeCliRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "planwharf-fake-recovery-" + Guid.NewGuid().ToString("N"));
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
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private int Run(string[] args, string stdin, string? scenario, string? log, out string stdout, out string stderr)
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
    public void BadAnswerIsSkipped_AndALaterAnswerRuns()
    {
        var scenario = CreateScenario("""
        [
          1,
          { "callNumber": "second", "stdout": "bad type" },
          { "argContains": 12, "stdout": "bad arg" },
          { "fakeAs": "codex", "stdout": "wrong program" },
          { "stdout": "recovered" }
        ]
        """);

        var code = Run(new[] { "build" }, "", scenario, null, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Equal("recovered\n", Norm(stdout));
        Assert.Contains("skipped an answer", stderr);
    }

    [Fact]
    public void AnswersWrapper_AndWrongAnswersField()
    {
        var wrapped = CreateScenario("""{ "answers": [ { "stdout": "wrapped" } ] }""");
        var code = Run(Array.Empty<string>(), "", wrapped, null, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal("wrapped\n", Norm(stdout));

        foreach (var body in new[] { """{"answers":{"stdout":"no"}}""", """{"answers":[]}""", " \n\t", "[{ \"stdout\": \"x\" },]" })
        {
            var path = CreateScenario(body);
            code = Run(new[] { "run" }, "", path, null, out _, out stderr);
            Assert.Equal(2, code);
            Assert.Contains("Planwharf.FakeCli:", stderr);
        }
    }

    [Fact]
    public void ScenarioWithBom_AndLockedFile_AreHandled()
    {
        var path = Path.Combine(_tempDir, "bom.json");
        var json = """[{ "stdout": "bom ok" }]"""u8.ToArray();
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray());
        var code = Run(Array.Empty<string>(), "", path, null, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal("bom ok\n", Norm(stdout));

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            code = Run(Array.Empty<string>(), "", path, null, out stdout, out stderr);
            Assert.Equal(2, code);
            Assert.Contains("failed to parse", stderr);
            Assert.Equal("", stdout);
            GC.KeepAlive(locked);
        }
    }

    [Fact]
    public void CallIndex_AndCallNumberWinsOverNthCall()
    {
        var log = Path.Combine(_tempDir, "index.log");
        var scenario = CreateScenario("""
        [
          { "callNumber": 1, "nthCall": 9, "stdout": "number wins" },
          { "callIndex": 1, "stdout": "index one" }
        ]
        """);

        var code = Run(new[] { "a" }, "", scenario, log, out var stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("number wins\n", Norm(stdout));

        code = Run(new[] { "b" }, "", scenario, log, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("index one\n", Norm(stdout));
    }

    [Fact]
    public void WithoutALog_EveryProcessIsCallOne()
    {
        var scenario = CreateScenario("""
        [
          { "callNumber": 1, "stdout": "first" },
          { "callNumber": 2, "stdout": "second" }
        ]
        """);

        var code = Run(new[] { "a" }, "", scenario, null, out var stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("first\n", Norm(stdout));

        code = Run(new[] { "b" }, "", scenario, null, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("first\n", Norm(stdout));
    }

    [Fact]
    public void FailedMatch_StillAdvancesTheNextCall()
    {
        var log = Path.Combine(_tempDir, "advance.log");
        var scenario = CreateScenario("""
        [
          { "argContains": "missing", "stdout": "no" },
          { "callNumber": 2, "stdout": "after failure" }
        ]
        """);

        var code = Run(new[] { "other" }, "", scenario, log, out _, out var stderr);
        Assert.Equal(2, code);
        Assert.Contains("no answer applies", stderr);

        code = Run(new[] { "other" }, "", scenario, log, out var stdout, out stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal("after failure\n", Norm(stdout));
    }

    [Fact]
    public void AlternateMatchers_AreCaseInsensitive_AndAllRequired()
    {
        var scenario = CreateScenario("""
        [
          { "fakeAs": "CODEX", "stdout": "wrong" },
          { "argumentContains": "BUILD", "argsContain": ["app", "--flag"], "stdout": "args" },
          { "stdinContains": ["please", "SHIP"], "stdout": "stdin" },
          { "promptContains": ["from", "file"], "stdout": "file" }
        ]
        """);

        var code = Run(new[] { "--flag", "Build", "App" }, "", scenario, null, out var stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("args\n", Norm(stdout));

        code = Run(new[] { "run" }, "Please ship it", scenario, null, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("stdin\n", Norm(stdout));

        var prompt = Path.Combine(_tempDir, "prompt file.txt");
        File.WriteAllText(prompt, "loaded FROM the file");
        code = Run(new[] { "--prompt-file", prompt }, "", scenario, null, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("file\n", Norm(stdout));

        var empty = CreateScenario("""
        [
          { "argContains": "", "stdout": "empty should not win" },
          { "stdout": "fallback" }
        ]
        """);
        code = Run(new[] { "anything" }, "", empty, null, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("fallback\n", Norm(stdout));
    }

    [Fact]
    public void PromptNeedle_DoesNotMatchTheInsertedSeparator()
    {
        var scenario = CreateScenario("""
        [
          { "promptContains": "\n", "stdout": "separator" },
          { "promptContains": "real-newline", "stdout": "real" },
          { "stdout": "fallback" }
        ]
        """);

        var code = Run(Array.Empty<string>(), "", scenario, null, out var stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("fallback\n", Norm(stdout));

        var real = CreateScenario("""
        [
          { "promptContains": "real-newline", "stdout": "real" },
          { "stdout": "fallback" }
        ]
        """);
        code = Run(Array.Empty<string>(), "has a real-newline\ninside", real, null, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("real\n", Norm(stdout));

        var hidden = Path.Combine(_tempDir, "hidden-prompt.txt");
        File.WriteAllText(hidden, "hidden");
        var lockedScenario = CreateScenario("""
        [
          { "promptContains": "hidden", "stdout": "from file" },
          { "stdout": "fallback" }
        ]
        """);
        using (var locked = new FileStream(hidden, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            code = Run(new[] { "--prompt-file", hidden }, "", lockedScenario, null, out stdout, out var stderr);
            Assert.Equal(0, code);
            Assert.Equal("", stderr);
            Assert.Equal("fallback\n", Norm(stdout));
            GC.KeepAlive(locked);
        }

        var first = Path.Combine(_tempDir, "first-prompt.txt");
        var second = Path.Combine(_tempDir, "second-prompt.txt");
        File.WriteAllText(first, "choose first");
        File.WriteAllText(second, "choose second");
        var prompts = CreateScenario("""
        [
          { "promptContains": "second", "stdout": "second prompt" },
          { "promptContains": "first", "stdout": "first prompt" }
        ]
        """);
        code = Run(new[] { "--prompt-file", first, "--prompt-file", second }, "", prompts, null, out stdout, out _);
        Assert.Equal(0, code);
        Assert.Equal("second prompt\n", Norm(stdout));
    }

    [Fact]
    public void NullFields_AreAbsent_AndABadValueDoesNotFallThrough()
    {
        var scenario = CreateScenario("""
        [
          {
            "pauseMs": null,
            "files": null,
            "stdout": null,
            "stderr": null,
            "exitCode": null
          }
        ]
        """);
        var code = Run(Array.Empty<string>(), "", scenario, null, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);

        var broken = CreateScenario("""
        [
          { "stdout": 5 },
          { "stdout": "should not run" }
        ]
        """);
        code = Run(Array.Empty<string>(), "", broken, null, out stdout, out stderr);
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Contains("failed to run", stderr);
    }

    [Fact]
    public void FileWrites_MapContent_PartialFailure_AndExactArgument()
    {
        var folder = Path.Combine(_tempDir, "out put");
        var mapped = Path.Combine(folder, "mapped.txt");
        var jsonFile = Path.Combine(folder, "body.json");
        var emptyFile = Path.Combine(folder, "empty.txt");
        var scenario = CreateScenario($$"""
        [
          {
            "files": {
              {{JsonValue.Create(mapped)!.ToJsonString()}}: "名前",
              {{JsonValue.Create(jsonFile)!.ToJsonString()}}: { "status": "COMPLETE" }
            },
            "stdout": "mapped"
          }
        ]
        """);
        var code = Run(Array.Empty<string>(), "", scenario, null, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal("mapped\n", Norm(stdout));
        var mappedBytes = File.ReadAllBytes(mapped);
        Assert.Equal(Encoding.UTF8.GetBytes("名前"), mappedBytes);
        var body = JsonNode.Parse(File.ReadAllText(jsonFile)) as JsonObject;
        Assert.Equal("COMPLETE", body?["status"]?.GetValue<string>());

        var exact = CreateScenario($$"""
        [
          {
            "files": [
              { "pathAfterArg": "--result", "content": "first-path" },
              { "path": {{JsonValue.Create(emptyFile)!.ToJsonString()}}, "content": "" }
            ],
            "stdout": "exact"
          }
        ]
        """);
        var skipped = Path.Combine(folder, "not-written.txt");
        var firstPath = Path.Combine(folder, "first.txt");
        var secondPath = Path.Combine(folder, "second.txt");
        code = Run(new[] { "--result-file", skipped, "--result", firstPath, "--result", secondPath }, "", exact, null, out stdout, out stderr);
        Assert.Equal(0, code);
        Assert.Equal("exact\n", Norm(stdout));
        Assert.False(File.Exists(skipped));
        Assert.Equal("first-path", File.ReadAllText(firstPath));
        Assert.False(File.Exists(secondPath));
        Assert.Equal("", File.ReadAllText(emptyFile));

        var blocked = Path.Combine(_tempDir, "blocked-dir");
        Directory.CreateDirectory(blocked);
        var kept = Path.Combine(folder, "kept.txt");
        var partial = CreateScenario($$"""
        [
          {
            "pauseMs": "nope",
            "files": [ { "path": {{JsonValue.Create(kept)!.ToJsonString()}}, "content": "kept" } ],
            "stdout": "not reached"
          }
        ]
        """);
        code = Run(Array.Empty<string>(), "", partial, null, out stdout, out stderr);
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Contains("failed to run", stderr);
        Assert.False(File.Exists(kept));

        var half = CreateScenario($$"""
        [
          {
            "files": [
              { "path": {{JsonValue.Create(kept)!.ToJsonString()}}, "content": "kept" },
              { "path": {{JsonValue.Create(blocked)!.ToJsonString()}}, "content": "nope" }
            ],
            "stdout": "not reached"
          }
        ]
        """);
        code = Run(Array.Empty<string>(), "", half, null, out stdout, out stderr);
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Contains("failed to run", stderr);
        Assert.Equal("kept", File.ReadAllText(kept));
    }

    [Fact]
    public void PauseSeconds_AndNegativePause_DoNotChangeTheAnswer()
    {
        var scenario = CreateScenario("""[{ "pauseSeconds": 0.2, "stdout": "waited", "exitCode": 0 }]""");
        var watch = Stopwatch.StartNew();
        var code = Run(Array.Empty<string>(), "", scenario, null, out var stdout, out _);
        watch.Stop();
        Assert.Equal(0, code);
        Assert.Equal("waited\n", Norm(stdout));
        Assert.True(watch.ElapsedMilliseconds >= 150, $"Elapsed was {watch.ElapsedMilliseconds} ms");

        scenario = CreateScenario("""[{ "pauseMs": -50, "pauseSeconds": -2, "stderr": ["only"], "exitCode": 4 }]""");
        watch.Restart();
        code = Run(Array.Empty<string>(), "", scenario, null, out stdout, out var stderr);
        watch.Stop();
        Assert.Equal(4, code);
        Assert.Equal("", stdout);
        Assert.Equal("only\n", Norm(stderr));
        Assert.True(watch.ElapsedMilliseconds < 150, $"Elapsed was {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void LogSurvivesMissingScenario_SpecialStdin_AndUnwritablePath()
    {
        var log = Path.Combine(_tempDir, "calls.log");
        var code = Run(new[] { "arg \"one\"", "名前" }, "line1\nline2 \"q\"", null, log, out _, out var stderr);
        Assert.Equal(2, code);
        Assert.Contains("no scenario file", stderr);
        var lines = File.ReadAllLines(log);
        Assert.Single(lines);
        var entry = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(entry);
        Assert.Equal("line1\nline2 \"q\"", entry["stdin"]?.GetValue<string>());
        Assert.Equal("名前", (entry["args"] as JsonArray)?[1]?.GetValue<string>());
        Assert.Equal("claude", entry["fakeAs"]?.GetValue<string>());

        File.SetAttributes(log, FileAttributes.ReadOnly);
        // An administrator (root) can still write a read-only file: that part checks nothing there.
        var readOnlyHolds = !CanWrite(log);
        try
        {
            if (readOnlyHolds) {
            var scenario = CreateScenario("""
            [
              { "callNumber": 1, "stdout": "stale" },
              { "callNumber": 2, "stdout": "next" }
            ]
            """);
            code = Run(new[] { "again" }, "more", scenario, log, out var stdout, out stderr);
            Assert.Equal(0, code);
            Assert.Equal("next\n", Norm(stdout));
            code = Run(new[] { "third" }, "more", scenario, log, out stdout, out _);
            Assert.Equal(0, code);
            Assert.Equal("next\n", Norm(stdout));
            Assert.Single(File.ReadAllLines(log));
            }
        }
        finally
        {
            File.SetAttributes(log, FileAttributes.Normal);
        }

        var parentIsFile = Path.Combine(_tempDir, "not-a-directory");
        File.WriteAllText(parentIsFile, "file");
        var blockedLog = Path.Combine(parentIsFile, "calls.log");
        var once = CreateScenario("""[{ "callNumber": 1, "stdout": "still answers" }]""");
        code = Run(new[] { "run" }, "", once, blockedLog, out var answered, out stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal("still answers\n", Norm(answered));
        Assert.False(File.Exists(blockedLog));
    }

    [Fact]
    public void ConcurrentCalls_UseEachNumberOnce_AndKeepArgsWithStdin()
    {
        var log = Path.Combine(_tempDir, "concurrent.log");
        var scenario = CreateScenario("""[{ "stdout": "ok" }]""");
        const int count = 8;
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
            var arg = (entry["args"] as JsonArray)?[1]?.GetValue<string>();
            Assert.Equal("stdin-" + arg, entry["stdin"]?.GetValue<string>());
        }

        Assert.Equal(Enumerable.Range(1, count).ToHashSet(), seen);
    }

    static bool CanWrite(string file)
    {
        try { using var stream = new FileStream(file, FileMode.Append, FileAccess.Write); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
