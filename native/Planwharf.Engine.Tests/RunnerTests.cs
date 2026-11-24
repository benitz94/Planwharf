// Tests for engine/runner.cjs port.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class RunnerTests
{
    [Fact]
    public void ParseArgs_and_PromptFor_basic_flags()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var resultFile = Path.Combine(tempDir, "out.txt");
            var argv = new[]
            {
                "node", "runner.cjs",
                "--folder", tempDir,
                "--result", resultFile,
                "--title", "Build it",
                "--detail", "Add tests.",
                "--worker", "Grok",
                "--attempt", "attempt-1",
                "--slice", "Slice 1",
                "--kind", "implementation"
            };

            var opts = Runner.ParseArgs(argv);
            Assert.Equal(tempDir, opts.Folder);
            Assert.Equal(resultFile, opts.Result);
            Assert.Equal("Build it", opts.Title);
            Assert.Equal("Add tests.", opts.Detail);
            Assert.Equal("Grok", opts.Worker);
            Assert.Equal("attempt-1", opts.Attempt);
            Assert.Equal("Slice 1", opts.Slice);

            var prompt = Runner.PromptFor(opts);
            Assert.Contains("Build it", prompt);
            Assert.Contains("Add tests.", prompt);
            Assert.Contains("Work in small steps.", prompt);
            Assert.Contains("Your slice: Slice 1", prompt);
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ParseArgs_job_file_validation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-jobfile-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var jobFile = Path.Combine(tempDir, "job.json");
            var resultFile = Path.Combine(tempDir, "res.txt");

            // Empty / invalid argument count
            Assert.Throws<ArgumentException>(() => Runner.ParseArgs(["node", "runner.cjs", "--job-file"]));

            // Non-object
            File.WriteAllText(jobFile, "[]");
            Assert.Throws<ArgumentException>(() => Runner.ParseArgs(["node", "runner.cjs", "--job-file", jobFile]));

            // Missing folder/result
            File.WriteAllText(jobFile, """{"title":"Test"}""");
            Assert.Throws<ArgumentException>(() => Runner.ParseArgs(["node", "runner.cjs", "--job-file", jobFile]));

            // Missing model object
            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                folder = tempDir,
                result = resultFile
            }));
            Assert.Throws<ArgumentException>(() => Runner.ParseArgs(["node", "runner.cjs", "--job-file", jobFile]));

            // Valid job file
            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                folder = tempDir,
                result = resultFile,
                title = "Job Title",
                detail = "Detail Info",
                worker = "LeadWorker",
                attempt = "attempt-2",
                slice = "Part A",
                model = new { id = "grok", provider = "grok", cli = "grok-beta", label = "Grok Beta" }
            }));

            var opts = Runner.ParseArgs(["node", "runner.cjs", "--job-file", jobFile]);
            Assert.Equal(tempDir, opts.Folder);
            Assert.Equal(resultFile, opts.Result);
            Assert.Equal("Job Title", opts.Title);
            Assert.Equal("Detail Info", opts.Detail);
            Assert.Equal("LeadWorker", opts.Worker);
            Assert.Equal("attempt-2", opts.Attempt);
            Assert.Equal("Part A", opts.Slice);
            Assert.NotNull(opts.Model);
            Assert.Equal("grok", opts.Model?["id"]?.ToString());
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void PromptFor_audit_and_research_kinds()
    {
        var auditOpts = new Runner.RunnerOptions
        {
            Kind = "audit",
            Title = "Check Security",
            Detail = "Look for leaks.",
            Result = "D:\\evidence\\result.txt"
        };
        var auditPrompt = Runner.PromptFor(auditOpts);
        Assert.Contains("Planwharf audit worker", auditPrompt);
        Assert.Contains("Evidence directory: D:\\evidence", auditPrompt);
        Assert.Contains("Audit: Check Security", auditPrompt);
        Assert.Contains("Look for leaks.", auditPrompt);

        var researchOpts = new Runner.RunnerOptions
        {
            Kind = "research",
            Title = "Explore SQLite",
            Detail = "Check version support."
        };
        var researchPrompt = Runner.PromptFor(researchOpts);
        Assert.Contains("Planwharf research worker", researchPrompt);
        Assert.Contains("Write report.md in this isolated working folder", researchPrompt);
        Assert.Contains("Research: Explore SQLite", researchPrompt);
        Assert.Contains("Check version support.", researchPrompt);
    }

    [Fact]
    public void PatchStatus_updates_and_creates_correct_format()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-patch-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "status.txt");

            // Create new
            Runner.PatchStatus(file, "WATCHING", new JsonObject
            {
                ["worker"] = "Grok",
                ["attempt"] = "attempt-1"
            });
            var content1 = File.ReadAllText(file);
            Assert.Contains("WORKER: Grok", content1);
            Assert.Contains("ATTEMPT: attempt-1", content1);
            Assert.Contains("STATUS: WATCHING", content1);

            // Update to COMPLETE
            Runner.PatchStatus(file, "COMPLETE", new JsonObject
            {
                ["worker"] = "Grok",
                ["attempt"] = "attempt-1",
                ["exitCode"] = 0
            });
            var content2 = File.ReadAllText(file);
            Assert.Contains("STATUS: COMPLETE", content2);
            Assert.Contains("EXIT: 0", content2);

            // Update to FAILED with reason
            Runner.PatchStatus(file, "FAILED", new JsonObject
            {
                ["worker"] = "Grok",
                ["attempt"] = "attempt-1",
                ["exitCode"] = 1,
                ["reason"] = "Timeout exceeded"
            });
            var content3 = File.ReadAllText(file);
            Assert.Contains("STATUS: FAILED", content3);
            Assert.Contains("REASON: Timeout exceeded", content3);
            Assert.Contains("EXIT: 1", content3);
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Process_liveness_and_kill_for_nonexistent()
    {
        Assert.True(Runner.IsProcessAlive(Environment.ProcessId));
        Assert.False(Runner.IsProcessAlive(99999999));
        Assert.False(Runner.IsProcessAlive(-1));
        Assert.False(Runner.KillWorker(99999999));
        Assert.False(Runner.AbortAsk("nonexistent"));
        Assert.False(Runner.AbortSupervisor());
    }

    [Fact]
    public void CliModelOf_and_ToolRequestRule_and_DropThinking()
    {
        // CliModelOf
        var model = new JsonObject { ["id"] = "gpt-6-astra", ["cli"] = "grok-4.7" };
        Assert.Null(Runner.CliModelOf(model)); // grok model id mismatch

        var validModel = new JsonObject
        {
            ["provider"] = "codex",
            ["cli"] = "gpt-5-turbo",
            ["modelRouting"] = "configured"
        };
        Assert.Equal("gpt-5-turbo", Runner.CliModelOf(validModel));

        // DropThinkingPrefix
        Assert.Equal("Done.", Runner.DropThinkingPrefix("I will inspect git.\nDone.", "I will inspect git."));
        Assert.Equal("Hello world", Runner.DropThinkingPrefix("Hello world", ""));

        // ToolRequestRule
        var rule1 = Runner.ToolRequestRule(new JsonObject { ["mcp"] = true, ["browser"] = true, ["hooks"] = true, ["memory"] = true });
        Assert.Contains("Project tools on for your model: MCP servers, browser, hooks and memory.", rule1);

        var rule2 = Runner.ToolRequestRule(new JsonObject { ["mcp"] = true });
        Assert.Contains("Project tools that are off for your model: browser, hooks, memory", rule2);
        Assert.Matches(Runner.ToolRequestRegex, "TOOL REQUEST: browser");
        Assert.Matches(Runner.ToolRequestRegex, "  TOOL REQUEST: mcp  \n");
        Assert.DoesNotMatch(Runner.ToolRequestRegex, "TOOL REQUEST: subagents");
    }

    [Fact]
    public void AskPrompt_formats_properly_and_gating_checks()
    {
        var info = new JsonObject
        {
            ["question"] = "How far did we go with RaceEngineer?",
            ["folder"] = "D:\\benit\\Desktop\\Projects\\RaceEngineer",
            ["model"] = new JsonObject
            {
                ["id"] = "gpt-6-astra",
                ["label"] = "GPT-6 Astra",
                ["alwaysApprove"] = true
            },
            ["state"] = new JsonObject
            {
                ["focus"] = new JsonObject
                {
                    ["name"] = "RaceEngineer",
                    ["folder"] = "D:\\benit\\Desktop\\Projects\\RaceEngineer",
                    ["goal"] = "Local race telemetry",
                    ["savingMode"] = "safe"
                }
            }
        };

        var spoken = Runner.AskPrompt(info);
        Assert.Contains("GPT-6 Astra", spoken);
        Assert.Contains("RaceEngineer", spoken);
        Assert.Contains("Project folder: D:\\benit\\Desktop\\Projects\\RaceEngineer", spoken);
        Assert.Contains("Do not recite STATUS", spoken);
        Assert.Contains("Saving mode is Safe: do not choose a flagship model", spoken);
        Assert.Contains("You have full local tool access, network access", spoken);

        // Without alwaysApprove
        var restrictedInfo = info.DeepClone().AsObject();
        restrictedInfo["model"]!.AsObject()["alwaysApprove"] = false;
        var restrictedSpoken = Runner.AskPrompt(restrictedInfo);
        Assert.Contains("restricted by the configured model permission policy", restrictedSpoken);

        // ChatMode talk requires talkInstruction dependency
        var talkInfo = info.DeepClone().AsObject();
        talkInfo["chatMode"] = "talk";
        Assert.Throws<InvalidOperationException>(() => Runner.AskPrompt(talkInfo));
        var talkResult = Runner.AskPrompt(talkInfo, talkInstruction: () => "Talk mode instruction active.");
        Assert.Contains("Talk mode instruction active.", talkResult);

        // workRunning requires additionInstruction dependency
        var workInfo = info.DeepClone().AsObject();
        workInfo["workRunning"] = true;
        Assert.Throws<InvalidOperationException>(() => Runner.AskPrompt(workInfo));
        var addResult = Runner.AskPrompt(workInfo, additionInstruction: () => "Addition mode instruction active.");
        Assert.Contains("Addition mode instruction active.", addResult);

        // Supervisor prompt
        var superInfo = new JsonObject
        {
            ["purpose"] = "supervisor",
            ["question"] = "What happened?",
            ["state"] = new JsonObject { ["events"] = new JsonArray() }
        };
        var superPrompt = Runner.AskPrompt(superInfo);
        Assert.Contains("Planwharf Supervisor, performing an independent advisory", superPrompt);
        Assert.Contains("You have no authority to approve work", superPrompt);
        Assert.Contains("USER QUESTION:\n\nWhat happened?", superPrompt);
    }

    [Fact]
    public void AskStream_processes_chunks_and_deltas_correctly()
    {
        var stream = Runner.CreateAskStream();
        stream.Push("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"I will inspect git.\\n\"}}}\n");
        stream.Push("{\"type\":\"stream_event\",\"event\":{\"delta\":{\"type\":\"text_delta\",\"text\":\"RaceEngineer is a local prototype.\"}}}\n");
        stream.Push("{\"type\":\"result\",\"duration_ms\":42100,\"result\":\"RaceEngineer is a local prototype.\"}\n");
        var parsed = stream.Finish();

        Assert.Contains("inspect git", parsed.Thinking);
        Assert.Equal("RaceEngineer is a local prototype.", parsed.Answer);
        Assert.Equal(42100, parsed.Ms);

        // Leaked thinking prefix in result text
        var leaked = Runner.CreateAskStream();
        leaked.Push("{\"type\":\"stream_event\",\"event\":{\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"I will inspect git.\\n\"}}}\n");
        leaked.Push("{\"type\":\"result\",\"duration_ms\":900,\"result\":\"I will inspect git.\\nRaceEngineer is a local prototype.\"}\n");
        var cleaned = leaked.Finish();
        Assert.Equal("RaceEngineer is a local prototype.", cleaned.Answer);
        Assert.Contains("inspect git", cleaned.Thinking);

        // Claude protocol validation: expected model mismatch
        var claudeStream = Runner.CreateAskStream("claude-3-7-sonnet");
        claudeStream.Push("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-3-7-sonnet\"}\n");
        claudeStream.Push("{\"type\":\"assistant\",\"message\":{\"model\":\"claude-3-5-haiku\",\"content\":[]}}\n");
        var mismatchRes = claudeStream.Finish();
        Assert.Equal("PROVIDER_MODEL_IDENTITY_CHANGED", mismatchRes.Failure);

        // Claude protocol validation: missing result
        var claudeMissing = Runner.CreateAskStream("claude-3-7-sonnet");
        claudeMissing.Push("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-3-7-sonnet\"}\n");
        var missingRes = claudeMissing.Finish();
        Assert.Equal("PROVIDER_PROTOCOL_CHANGED", missingRes.Failure);

        // Claude synthetic credit refusal
        var claudeCredit = Runner.CreateAskStream("claude-3-7-sonnet");
        claudeCredit.Push("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-3-7-sonnet\"}\n");
        claudeCredit.Push("{\"type\":\"assistant\",\"message\":{\"model\":\"<synthetic>\",\"content\":[{\"type\":\"text\",\"text\":\"Usage credits required.\"}]}}\n");
        claudeCredit.Push("{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"Usage credits required.\"}\n");
        var creditRes = claudeCredit.Finish();
        Assert.NotEqual("PROVIDER_MODEL_IDENTITY_CHANGED", creditRes.Failure);
        Assert.Equal("provider", creditRes.Failure);
        Assert.Contains("credits required", creditRes.Error);
    }

    [Fact]
    public void WhenFinished_completes_after_exit_grace_period()
    {
        // Start a short-lived process
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", "exit 0"]) { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("sh", ["-c", "exit 0"]) { CreateNoWindow = true, UseShellExecute = false };

        using var proc = Process.Start(psi)!;
        var finished = false;
        int? exitCode = null;
        using var mre = new ManualResetEventSlim(false);

        Runner.WhenFinished(proc, (code, sig) =>
        {
            exitCode = code;
            finished = true;
            mre.Set();
        }, exitGraceMs: 500);

        Assert.True(mre.Wait(5000));
        Assert.True(finished);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void SpawnWorker_containment_and_rejection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-spawn-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var resultPath = Path.Combine(tempDir, "results", "result.txt");

            // Rejects missing real linked folder
            Assert.Throws<InvalidOperationException>(() => Runner.SpawnWorker(new JsonObject
            {
                ["folder"] = tempDir,
                ["resultPath"] = resultPath,
                ["job"] = new JsonObject { ["kind"] = "implementation" }
            }));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "logs")));

            // Rejects outside folder / traversal
            var alpha = Path.Combine(tempDir, "alpha");
            var beta = Path.Combine(tempDir, "alpha-other");
            Directory.CreateDirectory(alpha);
            Directory.CreateDirectory(beta);

            Assert.Throws<InvalidOperationException>(() => Runner.SpawnWorker(new JsonObject
            {
                ["linkedFolder"] = alpha,
                ["folder"] = beta,
                ["resultPath"] = Path.Combine(tempDir, "results", "x.txt"),
                ["job"] = new JsonObject { ["kind"] = "audit" }
            }));

            // Successful mock spawn
            var spawned = Runner.SpawnWorker(new JsonObject
            {
                ["linkedFolder"] = alpha,
                ["folder"] = alpha,
                ["resultPath"] = Path.Combine(alpha, "results", "res.txt"),
                ["attemptId"] = "att-1",
                ["job"] = new JsonObject { ["title"] = "Do task", ["kind"] = "implementation" },
                ["worker"] = new JsonObject { ["id"] = "lead-worker" }
            }, launcher: (cmd, args, psi) => 12345);

            Assert.Equal(12345, spawned.Pid);
            Assert.True(File.Exists(spawned.InputPath));
            Assert.True(File.Exists(spawned.LogPath));
            var parsedInput = JsonNode.Parse(File.ReadAllText(spawned.InputPath));
            Assert.Equal("Do task", parsedInput?["title"]?.ToString());
            Assert.Equal("lead-worker", parsedInput?["worker"]?.ToString());
            Assert.Equal(alpha, parsedInput?["folder"]?.ToString());
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void AskCliArgs_supervisor_transport_and_permission_args()
    {
        // Non-api supervisor transport throws
        foreach (var provider in new[] { "codex", "grok", "antigravity" })
        {
            var superInfo = new JsonObject
            {
                ["purpose"] = "supervisor",
                ["model"] = new JsonObject { ["provider"] = provider, ["cli"] = "fixture-model" },
                ["folder"] = ".",
                ["question"] = "Explain evidence"
            };
            var ex = Assert.Throws<InvalidOperationException>(() => Runner.AskCliArgs(superInfo));
            Assert.Contains("No verified no-tool Supervisor transport", ex.Message);
        }

        // Grok lead args
        var grokInfo = new JsonObject
        {
            ["question"] = "How far?",
            ["folder"] = ".",
            ["model"] = new JsonObject
            {
                ["id"] = "grok",
                ["provider"] = "grok",
                ["cli"] = "grok-4.6",
                ["alwaysApprove"] = true
            }
        };
        var grokArgs = Runner.AskCliArgs(grokInfo);
        Assert.Contains(grokArgs.Args, a => a.Contains("always-approve") || a.Contains("bypassPermissions") || a.Contains("dontAsk"));
    }

    [Fact]
    public async Task AskOrchestratorAsync_empty_question_and_busy_supervisor()
    {
        // Empty question
        var emptyRes = await Runner.AskOrchestratorAsync(new JsonObject { ["question"] = "" });
        Assert.Equal("", emptyRes.Answer);
        Assert.Equal("", emptyRes.Thinking);
        Assert.Equal(0, emptyRes.Ms);

        // Null question
        var nullRes = await Runner.AskOrchestratorAsync(new JsonObject());
        Assert.Equal("", nullRes.Answer);
        Assert.Equal(0, nullRes.Ms);
    }

    [Fact]
    public void Run_containment_and_missing_arguments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-run-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var swErr = new StringWriter();
            var swOut = new StringWriter();

            // Missing folder & result
            var code1 = Runner.Run(["node", "runner.cjs"], swOut, swErr);
            Assert.Equal(1, code1);
            Assert.Contains("runner requires --folder and --result", swErr.ToString());

            // Escaping linked folder
            var alpha = Path.Combine(tempDir, "alpha");
            var beta = Path.Combine(tempDir, "beta");
            Directory.CreateDirectory(alpha);
            Directory.CreateDirectory(beta);

            var resultPath = Path.Combine(tempDir, "result.txt");
            var jobFile = Path.Combine(tempDir, "job.json");
            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                linkedFolder = alpha,
                folder = beta,
                result = resultPath,
                model = new { provider = "codex", cli = "gpt-fixture" }
            }));

            var code2 = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr);
            Assert.Equal(1, code2);
            var statusText = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: FAILED", statusText);
            Assert.True(File.Exists(resultPath + ".diagnostic.json"));
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Run_structured_result_and_exit_handling()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-struct-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var folder = Path.Combine(tempDir, "proj");
            Directory.CreateDirectory(folder);
            var resultPath = Path.Combine(tempDir, "result.txt");
            var structuredResultPath = Path.Combine(tempDir, "attempt.json");
            var exitPath = Path.Combine(tempDir, "attempt.exit.json");

            // Write valid structured result
            var resultObj = new JsonObject
            {
                ["resultId"] = "R-1",
                ["attemptId"] = "attempt-1",
                ["subTaskId"] = "sub-1",
                ["megaTaskId"] = "mega-1",
                ["status"] = "COMPLETE",
                ["summary"] = "All completed",
                ["filesChanged"] = new JsonArray(),
                ["tests"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["command"] = "test",
                        ["status"] = "PASSED",
                        ["evidence"] = "ok"
                    }
                },
                ["blockers"] = new JsonArray(),
                ["nextAction"] = "none"
            };
            File.WriteAllText(structuredResultPath, resultObj.ToJsonString());

            var jobFile = Path.Combine(tempDir, "job.json");
            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                linkedFolder = folder,
                folder = folder,
                result = resultPath,
                attempt = "attempt-1",
                resultId = "R-1",
                subTaskId = "sub-1",
                megaTaskId = "mega-1",
                structuredResultPath = structuredResultPath,
                exitPath = exitPath,
                model = new { provider = "codex", cli = "gpt-5-turbo", modelRouting = "configured" }
            }));

            var swErr = new StringWriter();
            var swOut = new StringWriter();

            // Run with a simulated exit 0 process
            var code = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi =>
            {
                var runPsi = OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd.exe", ["/c", "exit 0"]) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }
                    : new ProcessStartInfo("sh", ["-c", "exit 0"]) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                return Process.Start(runPsi)!;
            });

            var statusText = File.ReadAllText(resultPath);
            Assert.True(code == 0, $"Exit code was {code}. Status: {statusText}. Err: {swErr}");
            Assert.Contains("STATUS: COMPLETE", statusText);
            Assert.True(File.Exists(exitPath));
            var exitJson = JsonNode.Parse(File.ReadAllText(exitPath));
            Assert.Equal(0, exitJson?["exitCode"]?.GetValue<int>());
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Descendant_cleanup_including_parent_exited()
    {
        if (OperatingSystem.IsWindows())
        {
            // Spawn a parent cmd.exe that spawns a child ping that runs for 20 seconds, and the parent exits immediately
            var psi = new ProcessStartInfo("cmd.exe", ["/c", "start /b ping -n 25 127.0.0.1"])
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            int parentPid;
            using (var parent = Process.Start(psi)!)
            {
                parentPid = parent.Id;
                parent.WaitForExit(5000);
            }

            // At this point parent has exited
            Assert.False(Runner.IsProcessAlive(parentPid));

            // StopProcessTreeWin32 should find any orphaned child of parentPid and terminate it
            Runner.StopProcessTreeWin32((uint)parentPid);

            // Verify with LivingTree that no process belongs to parentPid
            var living = Runner.LivingTree(parentPid);
            Assert.Empty(living);
        }
    }

    [Fact]
    public void AskStream_tool_use_and_tokens_parsing()
    {
        var stream = Runner.CreateAskStream();

        // 1. turn.completed with tokens
        stream.Push("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":120,\"output_tokens\":45,\"cached_input_tokens\":30}}\n");

        // 2. tool_use with chunked json
        stream.Push("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"read_file\"}}}\n");
        stream.Push("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"README.md\\\"}\"}}}\n");
        stream.Push("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":0}}\n");

        // 3. step_update with tool name and agent text delta
        stream.Push("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"tool_name\":\"git_status\"}}\n");
        stream.Push("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"All is clean.\"}}\n");

        // 4. result event
        stream.Push("{\"type\":\"result\",\"duration_ms\":1500,\"result\":\"All is clean.\"}\n");

        var outcome = stream.Finish();
        Assert.Equal("All is clean.", outcome.Answer);
        Assert.Equal(1500, outcome.Ms);
        Assert.NotNull(outcome.Tokens);
        Assert.Equal(120, outcome.Tokens?["input"]?.GetValue<long>());
        Assert.Equal(45, outcome.Tokens?["output"]?.GetValue<long>());
        Assert.Equal(30, outcome.Tokens?["cachedInput"]?.GetValue<long>());
        Assert.Contains("read_file", outcome.Thinking);
        Assert.Contains("README.md", outcome.Thinking);
        Assert.Contains("git_status", outcome.Thinking);
    }

    [Fact]
    public void ParseAskLine_direct_accumulator_and_string_overload()
    {
        var acc = new Runner.AskAccumulator { ExpectedModel = "claude-3-7-sonnet" };

        // Null / malformed
        Assert.Same(acc, Runner.ParseAskLine(acc, (string?)null));
        Assert.Same(acc, Runner.ParseAskLine(acc, "NOT_JSON"));
        Assert.Same(acc, Runner.ParseAskLine(acc, (JsonNode?)null));

        // Assistant with matching model
        var matchObj = JsonNode.Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"claude-3-7-sonnet\"}}");
        Runner.ParseAskLine(acc, matchObj);
        Assert.True(acc.ModelSeen);
        Assert.Null(acc.Failure);

        // Assistant with different model
        var mismatchLine = "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-3-5-haiku\"}}";
        Runner.ParseAskLine(acc, mismatchLine);
        Assert.Equal("PROVIDER_MODEL_IDENTITY_CHANGED", acc.Failure);
        Assert.Contains("different model", acc.Error);

        // Result event with usage tokens (Claude model stream)
        var acc2 = new Runner.AskAccumulator { ExpectedModel = "claude-3-7-sonnet" };
        var resultLine = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":2000,\"result\":\"Output answer\",\"usage\":{\"input_tokens\":500,\"output_tokens\":200,\"cache_read_input_tokens\":100}}";
        Runner.ParseAskLine(acc2, resultLine);
        Assert.Equal("Output answer", acc2.Answer);
        Assert.Equal(2000, acc2.Ms);
        Assert.NotNull(acc2.Tokens);
        Assert.Equal(500, acc2.Tokens?["input"]?.GetValue<long>());
        Assert.Equal(200, acc2.Tokens?["output"]?.GetValue<long>());
        Assert.Equal(100, acc2.Tokens?["cachedInput"]?.GetValue<long>());
    }

    private static Process StartFake(string scenarioPath)
    {
        var name = OperatingSystem.IsWindows() ? "Planwharf.FakeCli.exe" : "Planwharf.FakeCli";
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, name))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PLANWHARF_FAKE_SCENARIO"] = scenarioPath;
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task Keyed_lead_replacement_and_supervisor_isolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-lead-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Test 1: Keyed lead replacement
            var leadKey = "test-project-keyed-lead";
            var leadInfo1 = new JsonObject
            {
                ["askKey"] = leadKey,
                ["question"] = "First lead inquiry?",
                ["folder"] = tempDir,
                ["model"] = new JsonObject { ["provider"] = "codex", ["cli"] = "gpt-5-turbo", ["modelRouting"] = "configured" }
            };

            var sc1 = Path.Combine(tempDir, "sc1.json");
            File.WriteAllText(sc1, "[{\"pauseSeconds\": 15, \"answer\": \"First response\"}]");

            Process? firstProc = null;
            var t1 = Runner.AskOrchestratorAsync(leadInfo1, processRunner: psi =>
            {
                firstProc = StartFake(sc1);
                return firstProc;
            });

            // Wait briefly for first process to start
            await Task.Delay(300);
            Assert.NotNull(firstProc);
            Assert.False(firstProc.HasExited);

            // Starting a second request with same key should abort the first child
            var sc2 = Path.Combine(tempDir, "sc2.json");
            File.WriteAllText(sc2, "[{\"stdout\": \"{\\\"type\\\":\\\"result\\\",\\\"result\\\":\\\"Second response\\\"}\\n\"}]");

            var leadInfo2 = new JsonObject
            {
                ["askKey"] = leadKey,
                ["question"] = "Second lead inquiry?",
                ["folder"] = tempDir,
                ["model"] = new JsonObject { ["provider"] = "codex", ["cli"] = "gpt-5-turbo", ["modelRouting"] = "configured" }
            };

            Process? secondProc = null;
            var t2 = Runner.AskOrchestratorAsync(leadInfo2, processRunner: psi =>
            {
                secondProc = StartFake(sc2);
                return secondProc;
            });

            var res2 = await t2;
            Assert.Contains("Second response", res2.Answer);

            // Verify first process was aborted
            await Task.Delay(300);
            Assert.True(firstProc.HasExited);

            // Test 2: Supervisor isolation and busy detection
            ProviderAdapters.RegisterAdapter("api",
                nativeModel: new System.Text.RegularExpressions.Regex(@"\S+"),
                executable: _ => "test-api",
                availability: _ => new JsonObject { ["available"] = true },
                execute: (m, f, p, l, r) => new LaunchSpecResult("test-api", [], f, "api", "api", "claude-sonnet-4.5", Stdin: null),
                environment: (i, p) => new Dictionary<string, string>(),
                terminate: _ => { },
                quota: (_, _) => Task.FromResult(new JsonObject()),
                discover: (_, _) => Task.FromResult(new JsonObject()),
                resultIdentity: m => m
            );

            var scSuper = Path.Combine(tempDir, "scSuper.json");
            File.WriteAllText(scSuper, "[{\"pauseSeconds\": 15, \"stdout\": \"Super response\"}]");

            var superInfo = new JsonObject
            {
                ["purpose"] = "supervisor",
                ["question"] = "Supervisor check?",
                ["folder"] = tempDir,
                ["model"] = new JsonObject { ["provider"] = "api", ["adapter"] = "api", ["cli"] = "claude-sonnet-4.5" }
            };

            Process? superProc = null;
            var superTask1 = Runner.AskOrchestratorAsync(superInfo, processRunner: psi =>
            {
                superProc = StartFake(scSuper);
                return superProc;
            });

            await Task.Delay(300);
            Assert.NotNull(superProc);
            Assert.False(superProc.HasExited);

            // Concurrent supervisor request should return busy
            var superTask2 = await Runner.AskOrchestratorAsync(superInfo);
            Assert.Equal("busy", superTask2.Failure);
            Assert.Contains("already in progress", superTask2.Answer);

            // AbortSupervisor terminates the active supervisor child
            Assert.True(Runner.AbortSupervisor());
            await Task.Delay(300);
            Assert.True(superProc.HasExited);

            // Subsequent AbortSupervisor returns false as none is active
            Assert.False(Runner.AbortSupervisor());
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Run_worker_exit_failure_with_stderr_tail_and_diagnostic()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-fail-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var folder = Path.Combine(tempDir, "proj");
            Directory.CreateDirectory(folder);
            var resultPath = Path.Combine(tempDir, "res.txt");
            var jobFile = Path.Combine(tempDir, "job.json");

            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                linkedFolder = folder,
                folder = folder,
                result = resultPath,
                attempt = "attempt-fail",
                worker = "FailWorker",
                model = new { provider = "codex", cli = "gpt-5-turbo", modelRouting = "configured" }
            }));

            var swErr = new StringWriter();
            var swOut = new StringWriter();

            var scFail = Path.Combine(tempDir, "scFail.json");
            File.WriteAllText(scFail, "[{\"stderr\": \"Rate limit exceeded 429\\n\", \"exitCode\": 1}]");

            // Run process that writes to stderr and exits 1
            var code = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi => StartFake(scFail));

            Assert.Equal(1, code);
            var statusContent = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: FAILED", statusContent);
            Assert.Contains("EXIT: 1", statusContent);

            var diagPath = resultPath + ".diagnostic.json";
            Assert.True(File.Exists(diagPath));
            var diagText = File.ReadAllText(diagPath);
            Assert.Contains("PROVIDER_USAGE_LIMIT", diagText);
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Run_antigravity_protocol_final_status_handling()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-agy-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var folder = Path.Combine(tempDir, "proj");
            Directory.CreateDirectory(folder);
            var resultPath = Path.Combine(tempDir, "res.txt");
            var jobFile = Path.Combine(tempDir, "job.json");

            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                linkedFolder = folder,
                folder = folder,
                result = resultPath,
                attempt = "attempt-agy",
                worker = "AgyWorker",
                model = new { provider = "antigravity", adapter = "antigravity", cli = "gemini-2.5-pro", modelRouting = "configured", alwaysApprove = true }
            }));

            var swErr = new StringWriter();
            var swOut = new StringWriter();

            var scAgy = Path.Combine(tempDir, "scAgy.json");
            File.WriteAllText(scAgy, "[{\"stdout\": \"{\\\"event\\\":\\\"result\\\",\\\"result\\\":{\\\"status\\\":\\\"FAILED\\\",\\\"error\\\":\\\"Model quota exhausted\\\"}}\\n\", \"exitCode\": 0}]");

            // Process exits 0, but stdout reports FAILED status in result event
            var code = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi => StartFake(scAgy));

            Assert.Equal(1, code);
            var statusContent = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: FAILED", statusContent);
            Assert.Contains("EXIT: 1", statusContent);
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Run_structured_result_variants()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-variants-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var folder = Path.Combine(tempDir, "proj");
            Directory.CreateDirectory(folder);
            var resultPath = Path.Combine(tempDir, "res.txt");
            var structuredPath = Path.Combine(tempDir, "res.json");
            var exitPath = Path.Combine(tempDir, "res.exit.json");

            // 1. Valid COMPLETE + nonzero exit code (1) -> status FAILED
            var completeObj = new JsonObject
            {
                ["resultId"] = "R-var",
                ["attemptId"] = "att-var",
                ["subTaskId"] = "sub-var",
                ["megaTaskId"] = "mega-var",
                ["status"] = "COMPLETE",
                ["summary"] = "Done",
                ["filesChanged"] = new JsonArray(),
                ["tests"] = new JsonArray(),
                ["blockers"] = new JsonArray(),
                ["nextAction"] = "none"
            };
            File.WriteAllText(structuredPath, completeObj.ToJsonString());

            var jobFile = Path.Combine(tempDir, "job.json");
            File.WriteAllText(jobFile, JsonSerializer.Serialize(new
            {
                linkedFolder = folder,
                folder = folder,
                result = resultPath,
                attempt = "att-var",
                resultId = "R-var",
                subTaskId = "sub-var",
                megaTaskId = "mega-var",
                structuredResultPath = structuredPath,
                exitPath = exitPath,
                model = new { provider = "codex", cli = "gpt-5-turbo", modelRouting = "configured" }
            }));

            var swErr = new StringWriter();
            var swOut = new StringWriter();

            var scNonZero = Path.Combine(tempDir, "scNonZero.json");
            File.WriteAllText(scNonZero, "[{\"exitCode\": 1}]");

            var code1 = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi => StartFake(scNonZero));
            Assert.Equal(1, code1);
            var status1 = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: FAILED", status1);
            Assert.Contains("EXIT: 1", status1);

            // 2. Valid BLOCKED + exit code 0 -> status BLOCKED, runner exit code 1
            var blockedObj = new JsonObject
            {
                ["resultId"] = "R-var",
                ["attemptId"] = "att-var",
                ["subTaskId"] = "sub-var",
                ["megaTaskId"] = "mega-var",
                ["status"] = "BLOCKED",
                ["summary"] = "Need decision",
                ["filesChanged"] = new JsonArray(),
                ["tests"] = new JsonArray(),
                ["blockers"] = new JsonArray { "decision needed" },
                ["nextAction"] = "none"
            };
            File.WriteAllText(structuredPath, blockedObj.ToJsonString());

            var scZero = Path.Combine(tempDir, "scZero.json");
            File.WriteAllText(scZero, "[{\"exitCode\": 0}]");

            var code2 = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi => StartFake(scZero));
            Assert.Equal(1, code2);
            var status2 = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: BLOCKED", status2);
            Assert.Contains("EXIT: 0", status2);

            // 3. Identity mismatch (wrong attemptId in structured result) -> status FAILED
            var mismatchObj = completeObj.DeepClone().AsObject();
            mismatchObj["attemptId"] = "wrong-attempt";
            File.WriteAllText(structuredPath, mismatchObj.ToJsonString());

            var code3 = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi => StartFake(scZero));
            Assert.Equal(1, code3);
            var status3 = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: FAILED", status3);

            // 4. Missing structured result file with exit 0 -> status FAILED
            File.Delete(structuredPath);
            var code4 = Runner.Run(["node", "runner.cjs", "--job-file", jobFile], swOut, swErr, processRunner: psi => StartFake(scZero));
            Assert.Equal(1, code4);
            var status4 = File.ReadAllText(resultPath);
            Assert.Contains("STATUS: FAILED", status4);
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Keyed_lead_different_keys_do_not_abort_each_other()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-runner-keys-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var leadA = new JsonObject
            {
                ["askKey"] = "key-A",
                ["question"] = "Question A?",
                ["folder"] = tempDir,
                ["model"] = new JsonObject { ["provider"] = "codex", ["cli"] = "gpt-5-turbo", ["modelRouting"] = "configured" }
            };
            var leadB = new JsonObject
            {
                ["askKey"] = "key-B",
                ["question"] = "Question B?",
                ["folder"] = tempDir,
                ["model"] = new JsonObject { ["provider"] = "codex", ["cli"] = "gpt-5-turbo", ["modelRouting"] = "configured" }
            };

            var scA = Path.Combine(tempDir, "scA.json");
            File.WriteAllText(scA, "[{\"pauseSeconds\": 15, \"answer\": \"Answer A\"}]");
            var scB = Path.Combine(tempDir, "scB.json");
            File.WriteAllText(scB, "[{\"pauseSeconds\": 15, \"answer\": \"Answer B\"}]");

            Process? procA = null;
            var tA = Runner.AskOrchestratorAsync(leadA, processRunner: _ =>
            {
                procA = StartFake(scA);
                return procA;
            });

            await Task.Delay(300);
            Assert.NotNull(procA);
            Assert.False(procA.HasExited);

            Process? procB = null;
            var tB = Runner.AskOrchestratorAsync(leadB, processRunner: _ =>
            {
                procB = StartFake(scB);
                return procB;
            });

            await Task.Delay(300);
            Assert.NotNull(procB);
            Assert.False(procB.HasExited);

            // Process A must still be running (not aborted by B)
            Assert.False(procA.HasExited);

            // Abort A only
            Assert.True(Runner.AbortAsk("key-A"));
            await Task.Delay(300);
            Assert.True(procA.HasExited);
            Assert.False(procB.HasExited);

            // Abort B
            Assert.True(Runner.AbortAsk("key-B"));
            await Task.Delay(300);
            Assert.True(procB.HasExited);
        }
        finally
        {
            ProviderAdapters.ResetBuiltIns();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void AskStream_split_unicode_and_partial_chunks()
    {
        var stream = Runner.CreateAskStream();

        // Push text split across chunks, including multi-byte Unicode and emoji
        stream.Push("{\"type\":\"stream_event\",\"event\":{\"delta\":{\"type\":\"text_delta\",\"text\":\"Citt");
        stream.Push("à ");
        stream.Push("🚀\"}}}\n");

        // Unterminated line and malformed JSON line should be handled safely
        stream.Push("MALFORMED JSON UNPARSED LINE\n");

        // Result without trailing newline
        stream.Push("{\"type\":\"result\",\"duration_ms\":850,\"result\":\"Città 🚀\"}");

        var outRes = stream.Finish();
        Assert.Equal("Città 🚀", outRes.Answer);
        Assert.Equal(850, outRes.Ms);
    }
}
