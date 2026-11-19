using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Planwharf.FakeCli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        return Run(args, Console.In, Console.Out, Console.Error, null);
    }

    public static int Run(
        string[] args,
        TextReader inReader,
        TextWriter outWriter,
        TextWriter errWriter,
        IReadOnlyDictionary<string, string?>? envOverride)
    {
        string? GetEnv(string name)
        {
            if (envOverride != null && envOverride.TryGetValue(name, out var val))
            {
                return val;
            }
            return Environment.GetEnvironmentVariable(name);
        }

        var fakeAs = GetEnv("PLANWHARF_FAKE_AS") ?? "";
        var scenarioPath = GetEnv("PLANWHARF_FAKE_SCENARIO");
        var logPath = GetEnv("PLANWHARF_FAKE_LOG");

        var stdin = "";
        try
        {
            stdin = inReader.ReadToEnd();
        }
        catch { }

        var promptFromFile = "";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--prompt-file" && File.Exists(args[i + 1]))
            {
                try { promptFromFile = File.ReadAllText(args[i + 1]); }
                catch { }
            }
        }

        var callNumber = 1;
        if (!string.IsNullOrEmpty(logPath))
        {
            callNumber = RecordCall(logPath, args, stdin, fakeAs, envOverride);
        }

        if (string.IsNullOrEmpty(scenarioPath) || !File.Exists(scenarioPath))
        {
            errWriter.WriteLine($"Planwharf.FakeCli: no scenario file specified or found: '{scenarioPath}'");
            return 2;
        }

        JsonArray? answers = null;
        try
        {
            var scenarioText = File.ReadAllText(scenarioPath);
            var parsed = JsonNode.Parse(scenarioText);
            if (parsed is JsonArray array)
            {
                answers = array;
            }
            else if (parsed is JsonObject obj && obj["answers"] is JsonArray nested)
            {
                answers = nested;
            }
        }
        catch (Exception ex)
        {
            errWriter.WriteLine($"Planwharf.FakeCli: failed to parse scenario file: {ex.Message}");
            return 2;
        }

        if (answers == null || answers.Count == 0)
        {
            errWriter.WriteLine("Planwharf.FakeCli: scenario contains no answers");
            return 2;
        }

        JsonObject? matchedAnswer = null;
        foreach (var item in answers)
        {
            if (item is not JsonObject candidate) continue;
            bool matched;
            try
            {
                matched = Matches(candidate, args, stdin, promptFromFile, callNumber, fakeAs);
            }
            catch (Exception ex)
            {
                errWriter.WriteLine($"Planwharf.FakeCli: skipped an answer that could not be read: {ex.Message}");
                continue;
            }

            if (matched)
            {
                matchedAnswer = candidate;
                break;
            }
        }

        if (matchedAnswer == null)
        {
            var argsJoined = string.Join(" ", args);
            errWriter.WriteLine($"Planwharf.FakeCli: no answer applies for call #{callNumber} (fakeAs='{fakeAs}', args='{argsJoined}')");
            return 2;
        }

        try
        {
            return ExecuteAnswer(matchedAnswer, args, outWriter, errWriter);
        }
        catch (Exception ex)
        {
            errWriter.WriteLine($"Planwharf.FakeCli: failed to run the answer: {ex.Message}");
            return 2;
        }
    }

    private static string LogMutexName(string logPath)
    {
        var full = Path.GetFullPath(logPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        return "PlanwharfFakeCli-" + hash;
    }

    private static int RecordCall(string logPath, string[] args, string stdin, string fakeAs, IReadOnlyDictionary<string, string?>? envOverride)
    {
        var existingCalls = 0;
        var needsLeadingNewline = false;
        Mutex? mutex = null;
        var ownsMutex = false;
        try
        {
            mutex = new Mutex(false, LogMutexName(logPath));
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            if (File.Exists(logPath))
            {
                var bytes = File.ReadAllBytes(logPath);
                if (bytes.Length > 0)
                {
                    var newlines = 0;
                    foreach (var b in bytes)
                    {
                        if (b == (byte)'\n') newlines++;
                    }

                    needsLeadingNewline = bytes[^1] != (byte)'\n';
                    existingCalls = newlines + (needsLeadingNewline ? 1 : 0);
                }
            }
        }
        catch { }

        var callNumber = existingCalls + 1;

        var callRecord = new JsonObject();
        var argsArray = new JsonArray();
        foreach (var a in args) argsArray.Add((JsonNode?)JsonValue.Create(a));
        callRecord["args"] = argsArray;
        callRecord["stdin"] = stdin;
        callRecord["cwd"] = Environment.CurrentDirectory;
        if (!string.IsNullOrEmpty(fakeAs))
        {
            callRecord["fakeAs"] = fakeAs;
        }
        callRecord["callNumber"] = callNumber;

        var envObj = new JsonObject();
        var recordedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (envOverride != null)
        {
            foreach (var kvp in envOverride)
            {
                var key = kvp.Key;
                if (key.StartsWith("PLANWHARF_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value != null)
                    {
                        envObj[key] = kvp.Value;
                        recordedKeys.Add(key);
                    }
                }
            }
        }

        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key.ToString() ?? "";
            if (recordedKeys.Contains(key)) continue;

            if (key.StartsWith("PLANWHARF_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase))
            {
                envObj[key] = de.Value?.ToString();
            }
        }
        callRecord["env"] = envObj;

        try
        {
            using var fileStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fileStream, new UTF8Encoding(false));
            if (needsLeadingNewline) writer.Write('\n');
            writer.WriteLine(callRecord.ToJsonString());
        }
        catch { }
        finally
        {
            if (ownsMutex)
            {
                try { mutex?.ReleaseMutex(); } catch { }
            }

            mutex?.Dispose();
        }

        return callNumber;
    }

    public static bool Matches(JsonObject candidate, string[] args, string stdin, string promptFromFile, int callNumber, string fakeAs)
    {
        if (candidate.TryGetPropertyValue("fakeAs", out var fakeNode) && fakeNode != null)
        {
            var expectedFake = fakeNode.GetValue<string>();
            if (!string.Equals(fakeAs, expectedFake, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (candidate.TryGetPropertyValue("callNumber", out var callNumNode) && callNumNode != null)
        {
            var expectedNum = callNumNode.GetValue<int>();
            if (callNumber != expectedNum) return false;
        }
        else if (candidate.TryGetPropertyValue("nthCall", out var nthNode) && nthNode != null)
        {
            var expectedNum = nthNode.GetValue<int>();
            if (callNumber != expectedNum) return false;
        }
        else if (candidate.TryGetPropertyValue("callIndex", out var callIdxNode) && callIdxNode != null)
        {
            var expectedIdx = callIdxNode.GetValue<int>();
            if (callNumber - 1 != expectedIdx) return false;
        }

        if (candidate.TryGetPropertyValue("argContains", out var argNode) && argNode != null)
        {
            if (!CheckArgContains(argNode, args)) return false;
        }
        else if (candidate.TryGetPropertyValue("argsContain", out var argsNode) && argsNode != null)
        {
            if (!CheckArgContains(argsNode, args)) return false;
        }
        else if (candidate.TryGetPropertyValue("argumentContains", out var argumentNode) && argumentNode != null)
        {
            if (!CheckArgContains(argumentNode, args)) return false;
        }

        if (candidate.TryGetPropertyValue("promptContains", out var promptNode) && promptNode != null)
        {
            if (!CheckPromptContains(promptNode, stdin, promptFromFile)) return false;
        }
        else if (candidate.TryGetPropertyValue("stdinContains", out var stdinNode) && stdinNode != null)
        {
            if (!CheckPromptContains(stdinNode, stdin, promptFromFile)) return false;
        }

        return true;
    }

    private static bool CheckArgContains(JsonNode node, string[] args)
    {
        var joined = string.Join(" ", args);
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var s = item?.GetValue<string>();
                if (!string.IsNullOrEmpty(s) && !joined.Contains(s, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        var expected = node.GetValue<string>();
        return !string.IsNullOrEmpty(expected) && joined.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CheckPromptContains(JsonNode node, string stdin, string promptFromFile)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var s = item?.GetValue<string>();
                if (string.IsNullOrEmpty(s)) continue;
                if (!PromptHas(s, stdin, promptFromFile)) return false;
            }
            return true;
        }

        return PromptHas(node.GetValue<string>(), stdin, promptFromFile);
    }

    // Stdin and --prompt-file are searched separately. Joining them would make the
    // inserted newline look like part of the prompt.
    private static bool PromptHas(string? expected, string stdin, string promptFromFile)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        return stdin.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || promptFromFile.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExecuteAnswer(JsonObject answer, string[] args, TextWriter outWriter, TextWriter errWriter)
    {
        if (answer.TryGetPropertyValue("pauseMs", out var pauseMsNode) && pauseMsNode != null)
        {
            var ms = pauseMsNode.GetValue<int>();
            if (ms > 0) Thread.Sleep(ms);
        }
        else if (answer.TryGetPropertyValue("pauseSeconds", out var pauseSecNode) && pauseSecNode != null)
        {
            var sec = pauseSecNode.GetValue<double>();
            if (sec > 0) Thread.Sleep((int)(sec * 1000));
        }

        if (answer.TryGetPropertyValue("files", out var filesNode) && filesNode != null)
        {
            WriteFiles(filesNode, args);
        }

        if (answer.TryGetPropertyValue("stdout", out var stdoutNode) && stdoutNode != null)
        {
            WriteOutput(outWriter, stdoutNode);
        }

        if (answer.TryGetPropertyValue("stderr", out var stderrNode) && stderrNode != null)
        {
            WriteOutput(errWriter, stderrNode);
        }

        var exitCode = 0;
        if (answer.TryGetPropertyValue("exitCode", out var exitNode) && exitNode != null)
        {
            exitCode = exitNode.GetValue<int>();
        }

        return exitCode;
    }

    private static void WriteOutput(TextWriter writer, JsonNode node)
    {
        if (node is JsonArray arr)
        {
            foreach (var line in arr)
            {
                writer.WriteLine(line?.ToString() ?? "");
            }
        }
        else
        {
            var text = node.GetValue<string>();
            writer.WriteLine(text);
        }
    }

    private static void WriteFiles(JsonNode filesNode, string[] args)
    {
        if (filesNode is JsonArray fileArray)
        {
            foreach (var item in fileArray)
            {
                if (item is not JsonObject fileObj) continue;
                string? targetPath = null;
                if (fileObj.TryGetPropertyValue("path", out var pathNode) && pathNode != null)
                {
                    targetPath = pathNode.GetValue<string>();
                }
                else if (fileObj.TryGetPropertyValue("pathAfterArg", out var argNode) && argNode != null)
                {
                    var argName = argNode.GetValue<string>();
                    for (var i = 0; i < args.Length - 1; i++)
                    {
                        if (args[i] == argName)
                        {
                            targetPath = args[i + 1];
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(targetPath)) continue;

                var content = "";
                if (fileObj.TryGetPropertyValue("content", out var contentNode) && contentNode != null)
                {
                    content = contentNode is JsonValue val && val.TryGetValue<string>(out var s)
                        ? s
                        : contentNode.ToJsonString();
                }

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(targetPath, content, new UTF8Encoding(false));
            }
        }
        else if (filesNode is JsonObject fileMap)
        {
            foreach (var prop in fileMap)
            {
                var targetPath = prop.Key;
                var content = prop.Value is JsonValue val && val.TryGetValue<string>(out var s)
                    ? s
                    : prop.Value?.ToJsonString() ?? "";

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(targetPath, content, new UTF8Encoding(false));
            }
        }
    }
}
