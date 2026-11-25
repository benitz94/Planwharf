// Port of engine/project-home.cjs tests.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ProjectHomeTests : IDisposable
{
    private readonly string tempDir;

    public ProjectHomeTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "pw-home-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static JsonObject Item(string? folder, string id = "alpha") => new()
    {
        ["project"] = new JsonObject
        {
            ["id"] = id,
            ["folder"] = folder
        },
        ["revisions"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = 1,
                ["title"] = "Plan",
                ["direction"] = "Goal",
                ["state"] = "",
                ["open"] = new JsonArray { "First question?", "Second question?" },
                ["closed"] = new JsonArray()
            }
        }
    };

    private static void RunGitCommand(string folder, params string[] args)
    {
        using var process = new Process();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        process.StartInfo = start;
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git command failed: git {string.Join(" ", args)}");
    }

    [Fact]
    public async Task ImportedPausedProjectWithoutLinkedFolder_OffersRelinkNeverResume()
    {
        var project = Item(null);
        ((JsonObject)project["project"]!)["continuation"] = new JsonObject { ["resume"] = true };
        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["status"] = "checkpointed", ["canResume"] = true }
            },
            ["researches"] = new JsonArray()
        };

        var home = await ProjectHome.GetProjectHomeAsync(project, snapshot);
        Assert.Equal("Relink the project folder on this PC.", home["next"]?.GetValue<string>());
        Assert.NotEqual("resume", home["action"]?.GetValue<string>());
        Assert.Null(home["action"]);
    }

    [Fact]
    public async Task HomePriority_RelinkApprovalResumeExecutionQuestionReview()
    {
        var folder = Path.Combine(tempDir, "proj");
        Directory.CreateDirectory(folder);
        var project = Item(folder);

        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "m", ["projectId"] = "alpha", ["status"] = "RUNNING" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "s", ["megaTaskId"] = "m", ["title"] = "Run tests", ["status"] = "RUNNING" }
            }
        };

        // Relink outranks approval
        var relinkHome = await ProjectHome.GetProjectHomeAsync(Item(null), new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["status"] = "needs_approval", ["canResume"] = true } }
        }, durable);
        Assert.Equal("Relink the project folder on this PC.", relinkHome["next"]?.GetValue<string>());

        // Approval
        var approveHome = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["status"] = "needs_approval", ["canResume"] = true } }
        });
        Assert.Equal("Approve the prepared job.", approveHome["next"]?.GetValue<string>());

        // Resume
        var resumeHome = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["canResume"] = true } }
        });
        Assert.Equal("Resume from checkpoint.", resumeHome["next"]?.GetValue<string>());

        // Execution running
        var runningHome = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha" } },
            ["execution"] = (JsonObject)durable.DeepClone()
        });
        Assert.Equal("Work in progress: Run tests (s).", runningHome["next"]?.GetValue<string>());

        // Question (open questions)
        var questionHome = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha" } }
        });
        Assert.Equal("Tell the lead what to build.", questionHome["next"]?.GetValue<string>());

        // Review (no open questions)
        var rev = (JsonObject)((JsonArray)project["revisions"]!)[0]!;
        rev["open"] = new JsonArray();
        var reviewHome = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["next"] = "Untrusted invented next action" } }
        });
        Assert.Equal("Review the Living Plan.", reviewHome["next"]?.GetValue<string>());

        // Not a directory -> relink
        var filePath = Path.Combine(tempDir, "afile");
        File.WriteAllText(filePath, "not a directory");
        var fileHome = await ProjectHome.GetProjectHomeAsync(Item(filePath), new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha" } },
            ["execution"] = (JsonObject)durable.DeepClone()
        });
        Assert.Equal("Relink the project folder on this PC.", fileHome["next"]?.GetValue<string>());
    }

    [Fact]
    public async Task CurrentDurableMegaTask_OutranksStaleRowFlagsAndAnotherProject()
    {
        var folder = Path.Combine(tempDir, "proj-mega");
        Directory.CreateDirectory(folder);
        var project = Item(folder);

        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "active", ["projectId"] = "alpha", ["status"] = "RUNNING" },
                new JsonObject { ["id"] = "later", ["projectId"] = "alpha", ["status"] = "READY_FOR_APPROVAL" },
                new JsonObject { ["id"] = "foreign", ["projectId"] = "beta", ["status"] = "READY_FOR_APPROVAL" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "s", ["megaTaskId"] = "active", ["title"] = "Run tests", ["status"] = "RUNNING" }
            }
        };

        var snapshotActive = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "active", ["status"] = "needs_approval", ["canResume"] = true }
            },
            ["execution"] = (JsonObject)durable.DeepClone()
        };
        var homeActive = await ProjectHome.GetProjectHomeAsync(project, snapshotActive);
        Assert.Equal("Work in progress: Run tests (s).", homeActive["next"]?.GetValue<string>());

        var snapshotLater = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "later" }
            },
            ["execution"] = (JsonObject)durable.DeepClone()
        };
        var homeLater = await ProjectHome.GetProjectHomeAsync(project, snapshotLater);
        Assert.Equal("Approve the prepared MegaTask.", homeLater["next"]?.GetValue<string>());
        Assert.Equal("approve", homeLater["action"]?.GetValue<string>());

        var snapshotForeign = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "foreign" }
            },
            ["execution"] = (JsonObject)durable.DeepClone()
        };
        var homeForeign = await ProjectHome.GetProjectHomeAsync(project, snapshotForeign);
        Assert.Equal("Approve the prepared MegaTask.", homeForeign["next"]?.GetValue<string>());
    }

    [Fact]
    public async Task LocalGit_RejectsInheritedParentRepository()
    {
        var root = Path.Combine(tempDir, "parent-repo");
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);

        RunGitCommand(root, "init", "-q");
        RunGitCommand(root, "config", "user.email", "fixture@example.invalid");
        RunGitCommand(root, "config", "user.name", "Fixture");
        File.WriteAllText(Path.Combine(alpha, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(beta, "b.txt"), "beta");
        RunGitCommand(root, "add", ".");
        RunGitCommand(root, "commit", "-qm", "Fixture");

        var before = await ProjectHome.LocalGitAsync(alpha);
        Assert.False(before["readable"]?.GetValue<bool>());
        Assert.Equal("Contained local Git history unavailable.", before["reason"]?.GetValue<string>());

        File.WriteAllText(Path.Combine(beta, "b.txt"), "secret sibling change");
        var after = await ProjectHome.LocalGitAsync(alpha);
        Assert.False(after["readable"]?.GetValue<bool>());
        Assert.Null(after["fingerprint"]);
    }

    [Fact]
    public async Task LocalGit_PlanFileExclusion()
    {
        var repoDir = Path.Combine(tempDir, "plan-excl-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var gitCalls = new List<IReadOnlyList<string>>();
        var options = new ProjectHomeOptions
        {
            PlanFile = Path.Combine(repoDir, ".planwharf", "plan.md"),
            Git = (f, args) =>
            {
                gitCalls.Add(args);
                if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(repoDir);
                if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
                if (args.Contains("ls-files")) return Task.FromResult<string?>("");
                if (args.Contains("status")) return Task.FromResult<string?>("");
                if (args.Contains("diff")) return Task.FromResult<string?>("");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.LocalGitAsync(repoDir, options);
        Assert.True(result["readable"]?.GetValue<bool>());

        // Verify :(exclude,literal).planwharf/plan.md is in arguments
        var statusCall = gitCalls.FirstOrDefault(c => c.Contains("status"));
        Assert.NotNull(statusCall);
        Assert.Contains(":(exclude,literal).planwharf/plan.md", statusCall);
    }

    [Fact]
    public async Task LocalGit_UntrackedBounds_Over2000Files_FailsClosed()
    {
        var repoDir = Path.Combine(tempDir, "bounds-2000-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(repoDir);
                if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
                if (args.Contains("ls-files"))
                {
                    // Return 2001 untracked entries
                    var names = Enumerable.Range(1, 2005).Select(i => $"file{i}.txt");
                    return Task.FromResult<string?>(string.Join("\0", names));
                }
                if (args.Contains("status")) return Task.FromResult<string?>("?? file1.txt\n");
                if (args.Contains("diff")) return Task.FromResult<string?>("");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.LocalGitAsync(repoDir, options);
        Assert.True(result["readable"]?.GetValue<bool>());
        Assert.Null(result["fingerprint"]); // Fails closed, fingerprint is null
    }

    [Fact]
    public async Task LocalGit_UntrackedBounds_Over8MiB_FailsClosed()
    {
        var repoDir = Path.Combine(tempDir, "bounds-8mb-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var largeFileName = "large.bin";
        var largeFilePath = Path.Combine(repoDir, largeFileName);
        // Create 9MB dummy file
        using (var fs = new FileStream(largeFilePath, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(9 * 1024 * 1024);
        }

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(repoDir);
                if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
                if (args.Contains("ls-files")) return Task.FromResult<string?>(largeFileName + "\0");
                if (args.Contains("status")) return Task.FromResult<string?>("?? " + largeFileName + "\n");
                if (args.Contains("diff")) return Task.FromResult<string?>("");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.LocalGitAsync(repoDir, options);
        Assert.True(result["readable"]?.GetValue<bool>());
        Assert.Null(result["fingerprint"]); // Fails closed due to > 8 MiB
    }

    [Fact]
    public async Task Compare_FirstVisit_ReturnsExpectedShape()
    {
        var current = new JsonObject
        {
            ["revisionIds"] = new JsonArray { 1 },
            ["noteIds"] = new JsonArray { "note-1" }
        };

        var result = await ProjectHome.CompareAsync(null, current);
        Assert.True(result["firstVisit"]?.GetValue<bool>());
        Assert.Empty(result["changes"] as JsonArray ?? []);
        Assert.Empty(result["unavailable"] as JsonArray ?? []);
        Assert.Empty(result["details"] as JsonArray ?? []);
    }

    [Fact]
    public async Task Compare_RevisionsAndNotesChanges()
    {
        var prev = new JsonObject
        {
            ["revisionIds"] = new JsonArray { 1 },
            ["noteIds"] = new JsonArray { "n1" }
        };
        var current = new JsonObject
        {
            ["revisionIds"] = new JsonArray { 1, 2, 3 },
            ["noteIds"] = new JsonArray { "n1", "n2" }
        };

        var result = await ProjectHome.CompareAsync(prev, current);
        Assert.False(result["firstVisit"]?.GetValue<bool>());
        var changes = (result["changes"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        Assert.Contains("2 new Living Plan revisions", changes);
        Assert.Contains("1 new user note(s)", changes);
    }

    [Fact]
    public async Task Compare_GitCommitsAddedAndLogDetails()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "1111111111111111111111111111111111111111",
                ["root"] = "/repo",
                ["scope"] = "."
            }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "2222222222222222222222222222222222222222",
                ["root"] = "/repo",
                ["scope"] = "."
            }
        };

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--is-ancestor")) return Task.FromResult<string?>("");
                if (args.Contains("--count")) return Task.FromResult<string?>("2");
                if (args.Contains("-100")) return Task.FromResult<string?>("2222222\tSecond commit\n1111111\tFirst commit");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current, options);
        var changes = (result["changes"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        Assert.Contains("2 commits added", changes);
        var details = (result["details"] as JsonArray ?? []).Select(d => d?.GetValue<string>()).ToList();
        Assert.Contains("Commit 2222222: Second commit", details);
        Assert.Contains("Commit 1111111: First commit", details);
    }

    [Fact]
    public async Task Compare_GitHistoryNonAncestor_UnavailableReason()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "1111111111111111111111111111111111111111",
                ["root"] = "/repo",
                ["scope"] = "."
            }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "2222222222222222222222222222222222222222",
                ["root"] = "/repo",
                ["scope"] = "."
            }
        };

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--is-ancestor")) return Task.FromResult<string?>(null); // Not ancestor
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current, options);
        var unavailable = (result["unavailable"] as JsonArray ?? []).Select(u => u?.GetValue<string>()).ToList();
        Assert.Contains("Git history changed; a reliable commit count is unavailable.", unavailable);
    }

    [Fact]
    public async Task Compare_NonComparableBaselines_DifferentRootOrScope()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "1111111111111111111111111111111111111111",
                ["root"] = "/repoA",
                ["scope"] = "."
            }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "2222222222222222222222222222222222222222",
                ["root"] = "/repoB",
                ["scope"] = "."
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current);
        var unavailable = (result["unavailable"] as JsonArray ?? []).Select(u => u?.GetValue<string>()).ToList();
        Assert.Contains("No comparable Git baseline.", unavailable);
    }

    [Fact]
    public async Task Compare_LocalFilesAddedDeletedRenamedEdited()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "same",
                ["root"] = "/repo",
                ["scope"] = ".",
                ["files"] = new JsonArray
                {
                    new JsonObject { ["path"] = "deleted.txt", ["status"] = "M", ["fingerprint"] = "fp1" },
                    new JsonObject { ["path"] = "edited.txt", ["status"] = "M", ["fingerprint"] = "fp2" }
                }
            }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["head"] = "same",
                ["root"] = "/repo",
                ["scope"] = ".",
                ["files"] = new JsonArray
                {
                    new JsonObject { ["path"] = "added.txt", ["status"] = "??", ["fingerprint"] = "fp3" },
                    new JsonObject { ["path"] = "deleted.txt", ["status"] = "D", ["fingerprint"] = "deleted" },
                    new JsonObject { ["path"] = "renamed.txt", ["from"] = "orig.txt", ["status"] = "R", ["fingerprint"] = "fp4" },
                    new JsonObject { ["path"] = "edited.txt", ["status"] = "M", ["fingerprint"] = "fp5" }
                }
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current);
        var details = (result["details"] as JsonArray ?? []).Select(d => d?.GetValue<string>()).ToList();
        Assert.Contains("Added locally: added.txt", details);
        Assert.Contains("Deleted locally: deleted.txt", details);
        Assert.Contains("Renamed locally: renamed.txt (from orig.txt)", details);
        Assert.Contains("Edited locally: edited.txt", details);
    }

    [Fact]
    public async Task Compare_ExecutionStatusResultActionsResearch()
    {
        var prev = new JsonObject
        {
            ["execution"] = new JsonObject
            {
                ["status"] = "running",
                ["jobId"] = "job1",
                ["attemptId"] = "att1",
                ["checkpoint"] = "cp1",
                ["result"] = "res1",
                ["actions"] = new JsonArray
                {
                    new JsonObject { ["worker"] = "w1", ["text"] = "First action" }
                },
                ["research"] = new JsonArray
                {
                    new JsonObject { ["id"] = "R-1", ["status"] = "watching", ["attemptId"] = "ra1", ["reportAvailable"] = false }
                }
            }
        };
        var current = new JsonObject
        {
            ["execution"] = new JsonObject
            {
                ["status"] = "complete",
                ["jobId"] = "job1",
                ["attemptId"] = "att1",
                ["checkpoint"] = "cp2",
                ["result"] = "res2",
                ["jobTitle"] = "Test Job",
                ["actions"] = new JsonArray
                {
                    new JsonObject { ["worker"] = "w1", ["text"] = "First action" },
                    new JsonObject { ["worker"] = "w1", ["text"] = "Second action" }
                },
                ["research"] = new JsonArray
                {
                    new JsonObject { ["id"] = "R-1", ["status"] = "complete", ["attemptId"] = "ra2", ["reportAvailable"] = true }
                }
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current);
        var changes = (result["changes"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        Assert.Contains("Project work completed", changes);
        Assert.Contains("New checkpoint saved", changes);
        Assert.Contains("Execution result updated", changes);
        Assert.Contains("New worker activity recorded", changes);
        Assert.Contains("Research completed", changes);
    }

    [Fact]
    public async Task ProjectHome_ProgressDetails_Reports_And_BoundedLogTails()
    {
        var repoDir = Path.Combine(tempDir, "logtail-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var logPath = Path.Combine(tempDir, "worker.log");
        var reportText = "Full worker report retained.\n" + new string('x', 500);
        var event1 = "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"read_file\",\"tool_info\":{\"parameters\":{\"path\":\"file.txt\"}}}}\n";
        var resultEvent = new JsonObject
        {
            ["event"] = "result",
            ["result"] = new JsonObject
            {
                ["status"] = "SUCCESS",
                ["response"] = reportText
            }
        };
        var event2 = resultEvent.ToJsonString() + "\n";
        File.WriteAllText(logPath, event1 + event2);

        var project = Item(repoDir);
        var row = new JsonObject
        {
            ["id"] = "alpha",
            ["status"] = "watching",
            ["jobTitle"] = "Test Log Tails",
            ["dispatch"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = "Worker-A",
                    ["logPath"] = logPath,
                    ["alive"] = false
                }
            }
        };
        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray { row }
        };

        var home = await ProjectHome.GetProjectHomeAsync(project, snapshot);
        var progressDetails = (JsonObject)home["progressDetails"]!;
        var reports = (JsonArray)progressDetails["reports"]!;
        Assert.Single(reports);
        Assert.Equal(reportText, reports[0]?["text"]?.GetValue<string>());

        var actions = (JsonArray)progressDetails["actions"]!;
        Assert.NotEmpty(actions);
        Assert.Contains("Finished reading file.txt.", actions.Select(a => a?["text"]?.GetValue<string>()));
    }

    [Fact]
    public async Task ProjectHome_ExecutionResult_AbsentEmptyOversize()
    {
        var repoDir = Path.Combine(tempDir, "res-check-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var emptyFile = Path.Combine(tempDir, "empty.json");
        File.WriteAllText(emptyFile, "");

        var oversizeFile = Path.Combine(tempDir, "oversize.json");
        using (var fs = new FileStream(oversizeFile, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(9 * 1024 * 1024);
        }

        var validFile = Path.Combine(tempDir, "valid.json");
        File.WriteAllText(validFile, "{\"status\":\"COMPLETE\"}");

        var project = Item(repoDir);

        // 1. Absent result file
        var rowAbsent = new JsonObject { ["id"] = "alpha", ["resultPath"] = Path.Combine(tempDir, "nonexistent.json") };
        var homeAbsent = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowAbsent } });
        Assert.Null(homeAbsent["current"]?["execution"]?["result"]);

        // 2. Empty result file
        var rowEmpty = new JsonObject { ["id"] = "alpha", ["resultPath"] = emptyFile };
        var homeEmpty = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowEmpty } });
        Assert.Null(homeEmpty["current"]?["execution"]?["result"]);

        // 3. Oversize result file
        var rowOversize = new JsonObject { ["id"] = "alpha", ["resultPath"] = oversizeFile };
        var homeOversize = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowOversize } });
        Assert.Null(homeOversize["current"]?["execution"]?["result"]);

        // 4. Valid result file
        var rowValid = new JsonObject { ["id"] = "alpha", ["resultPath"] = validFile };
        var homeValid = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowValid } });
        var validHash = homeValid["current"]?["execution"]?["result"]?.GetValue<string>();
        Assert.NotNull(validHash);
        Assert.Matches("^[a-f0-9]{64}$", validHash);
    }

    [Fact]
    public async Task ProjectHome_CheckpointDiff_InjectsInspectionWithCanonicalFolder()
    {
        var repoDir = Path.Combine(tempDir, "inspect-canonical-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        string? inspectedFolder = null;
        var options = new ProjectHomeOptions
        {
            Inspect = folder =>
            {
                inspectedFolder = folder;
                return new JsonObject
                {
                    ["git"] = true,
                    ["commitId"] = "2222222222222222222222222222222222222222",
                    ["dirty"] = new JsonArray()
                };
            }
        };

        var project = Item(repoDir);
        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-1", ["projectId"] = "alpha", ["status"] = "PAUSED" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "ST-1", ["megaTaskId"] = "MT-1", ["status"] = "COMPLETE" }
            },
            ["checkpoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "CP-1",
                    ["projectId"] = "alpha",
                    ["megaTaskId"] = "MT-1",
                    ["planRevisionId"] = 1,
                    ["repoState"] = new JsonObject
                    {
                        ["repoHead"] = "1111111111111111111111111111111111111111",
                        ["fingerprint"] = "fp1",
                        ["comparable"] = true,
                        ["dirtyFiles"] = new JsonArray()
                    }
                }
            }
        };

        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-1" } },
            ["execution"] = durable
        };

        var home = await ProjectHome.GetProjectHomeAsync(project, snapshot, options);

        // Verify that inspect received the canonical folder
        var canonical = ProjectWorkspace.LinkedWorkspace(repoDir);
        Assert.NotNull(inspectedFolder);
        Assert.Equal(canonical, inspectedFolder);

        // Check checkpointDiffs
        var checkpointDiffs = (JsonArray)home["checkpointDiffs"]!;
        Assert.Single(checkpointDiffs);
        var diff = (JsonObject)checkpointDiffs[0]!;
        Assert.Equal("CP-1", diff["checkpointId"]?.GetValue<string>());
        var changes = (JsonArray)diff["changes"]!;
        Assert.Contains(changes.Select(c => c?.GetValue<string>()), c => c != null && c.StartsWith("HEAD changed from 11111111"));
    }

    [Fact]
    public async Task LocalGit_GitdirRedirectToAnotherProject_FailsClosed()
    {
        var root = Path.Combine(tempDir, "gitdir-test");
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);

        RunGitCommand(beta, "init", "-q");
        File.WriteAllText(Path.Combine(alpha, ".git"), $"gitdir: {Path.Combine(beta, ".git")}\n");

        var result = await ProjectHome.LocalGitAsync(alpha);
        Assert.False(result["readable"]?.GetValue<bool>());
    }

    [Fact]
    public async Task LocalGit_DirectorySymlinkContainment_RejectsTargetOutsideRoot()
    {
        var root = Path.Combine(tempDir, "symlink-containment");
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        RunGitCommand(alpha, "init", "-q");

        // Use custom IProjectHomeFiles where realpath of linked resolves to beta
        var fakeFiles = new FakeContainmentFiles(alpha, beta);
        var options = new ProjectHomeOptions
        {
            Files = fakeFiles,
            Git = (f, args) =>
            {
                if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(alpha);
                if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
                if (args.Contains("ls-files")) return Task.FromResult<string?>("");
                if (args.Contains("status") && args.Contains("-z")) return Task.FromResult<string?>(" D linked/data\0");
                if (args.Contains("status")) return Task.FromResult<string?>(" D linked/data\n");
                if (args.Contains("diff")) return Task.FromResult<string?>("");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.LocalGitAsync(alpha, options);
        Assert.True(result["readable"]?.GetValue<bool>());
        var filesArr = (JsonArray)result["files"]!;
        Assert.Single(filesArr);
        var fileItem = (JsonObject)filesArr[0]!;
        Assert.Equal("linked/data", fileItem["path"]?.GetValue<string>());
        Assert.Equal("deleted", fileItem["fingerprint"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProjectHome_TwoProjects_IndependentViews()
    {
        var repoA = Path.Combine(tempDir, "two-projects-a");
        var repoB = Path.Combine(tempDir, "two-projects-b");
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(repoB);
        RunGitCommand(repoA, "init", "-q");
        RunGitCommand(repoB, "init", "-q");

        var projectA = Item(repoA, "proj-a");
        var projectB = Item(repoB, "proj-b");

        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-A", ["projectId"] = "proj-a", ["title"] = "Work for A", ["status"] = "RUNNING" },
                new JsonObject { ["id"] = "MT-B", ["projectId"] = "proj-b", ["title"] = "Work for B", ["status"] = "READY_FOR_APPROVAL" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "ST-A", ["megaTaskId"] = "MT-A", ["title"] = "Sub A", ["status"] = "RUNNING" },
                new JsonObject { ["id"] = "ST-B", ["megaTaskId"] = "MT-B", ["title"] = "Sub B", ["status"] = "PENDING" }
            }
        };

        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "proj-a", ["megaTaskId"] = "MT-A" },
                new JsonObject { ["id"] = "proj-b", ["megaTaskId"] = "MT-B" }
            },
            ["execution"] = durable
        };

        var homeA = await ProjectHome.GetProjectHomeAsync(projectA, (JsonObject)snapshot.DeepClone());
        var homeB = await ProjectHome.GetProjectHomeAsync(projectB, (JsonObject)snapshot.DeepClone());

        Assert.Equal("proj-a", homeA["projectId"]?.GetValue<string>());
        Assert.Equal("Work in progress: Sub A (ST-A).", homeA["next"]?.GetValue<string>());
        Assert.Equal("Work for A", homeA["jobTitle"]?.GetValue<string>());
        Assert.Null(homeA["action"]);

        Assert.Equal("proj-b", homeB["projectId"]?.GetValue<string>());
        Assert.Equal("Approve the prepared MegaTask.", homeB["next"]?.GetValue<string>());
        Assert.Equal("Work for B", homeB["jobTitle"]?.GetValue<string>());
        Assert.Equal("approve", homeB["action"]?.GetValue<string>());
    }

    private sealed class FakeContainmentFiles : IProjectHomeFiles
    {
        private readonly string alpha;
        private readonly string beta;
        public FakeContainmentFiles(string alpha, string beta)
        {
            this.alpha = alpha;
            this.beta = beta;
        }

        public Task<string?> RealPathAsync(string path)
        {
            if (path.StartsWith(Path.Combine(alpha, "linked"), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>(beta); // Symlink points outside alpha to beta
            return Task.FromResult<string?>(path);
        }

        public Task<ProjectHomeFileStat> LStatAsync(string path) =>
            throw new FileNotFoundException("Simulated target outside root throws", path);

        public Task<ProjectHomeFileStat> StatAsync(string path) =>
            Task.FromResult(new ProjectHomeFileStat(0, false, false, false));

        public Task<byte[]> ReadFileAsync(string path) => Task.FromResult(Array.Empty<byte>());
        public Task<string> ReadLinkAsync(string path) => Task.FromResult(beta);
        public Task<byte[]> ReadTailAsync(string path, int maxBytes) => Task.FromResult(Array.Empty<byte>());
    }
}
