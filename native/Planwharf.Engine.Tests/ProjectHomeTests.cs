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
        // The resolved temporary folder: on macOS /var is a link to /private/var, and the fakes below key their
        // files by the paths the engine builds from the resolved project folder.
        tempDir = Path.Combine(ScenarioParityTests.RealRootForTests(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)), "pw-home-test-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task LocalGit_RedirectedHeadMetadata_FailsClosed()
    {
        var root = Path.Combine(tempDir, "redirected-head");
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        RunGitCommand(alpha, "init", "-q");

        var privateHead = Path.Combine(beta, "private-head");
        File.WriteAllText(privateHead, "ref: refs/heads/private\n");

        var headFile = Path.Combine(alpha, ".git", "HEAD");
        if (File.Exists(headFile)) File.Delete(headFile);

        try
        {
            File.CreateSymbolicLink(headFile, privateHead);
            var result = await ProjectHome.LocalGitAsync(alpha);
            Assert.False(result["readable"]?.GetValue<bool>());
        }
        catch (IOException)
        {
            // Skip if symlink creation is not permitted on this environment
        }
        catch (UnauthorizedAccessException)
        {
            // Skip if symlink privilege not held
        }
    }

    [Fact]
    public async Task LocalGit_SubmoduleRedirectedToAnotherProject_IgnoresSubmoduleAndObservesLocalEdits()
    {
        var root = Path.Combine(tempDir, "submodule-containment");
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);

        RunGitCommand(alpha, "init", "-q");
        RunGitCommand(alpha, "config", "user.name", "Fixture");
        RunGitCommand(alpha, "config", "user.email", "fixture@example.invalid");

        RunGitCommand(beta, "init", "-q");
        RunGitCommand(beta, "config", "user.name", "Fixture");
        RunGitCommand(beta, "config", "user.email", "fixture@example.invalid");

        File.WriteAllText(Path.Combine(beta, "private.txt"), "Foreign initial content");
        RunGitCommand(beta, "add", ".");
        RunGitCommand(beta, "commit", "-qm", "Foreign fixture");

        // Set up submodule cacheinfo in alpha
        var subDir = Path.Combine(alpha, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, ".git"), $"gitdir: {Path.Combine(beta, ".git")}\n");

        using var revProcess = new Process
        {
            StartInfo = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = beta,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };
        revProcess.Start();
        var betaHead = revProcess.StandardOutput.ReadToEnd().Trim();
        revProcess.WaitForExit();

        RunGitCommand(alpha, "update-index", "--add", "--cacheinfo", $"160000,{betaHead},sub");

        File.WriteAllText(Path.Combine(alpha, "local.txt"), "Local initial content");
        RunGitCommand(alpha, "add", "local.txt");
        RunGitCommand(alpha, "commit", "-qm", "Local fixture");

        RunGitCommand(alpha, "config", "diff.submodule", "log");
        RunGitCommand(alpha, "config", "submodule.sub.ignore", "none");

        var before = await ProjectHome.LocalGitAsync(alpha);
        Assert.True(before["readable"]?.GetValue<bool>());
        Assert.NotNull(before["fingerprint"]);

        // Mutate foreign repo: should not change alpha's inspection
        File.WriteAllText(Path.Combine(beta, "private.txt"), "Foreign changed content");
        RunGitCommand(beta, "commit", "-qam", "Foreign changed fixture");

        var afterForeign = await ProjectHome.LocalGitAsync(alpha);
        Assert.Equal(before["fingerprint"]?.GetValue<string>(), afterForeign["fingerprint"]?.GetValue<string>());

        // Mutate local file: alpha's fingerprint should change
        File.WriteAllText(Path.Combine(alpha, "local.txt"), "Local changed content");
        var afterLocal = await ProjectHome.LocalGitAsync(alpha);
        Assert.NotEqual(before["fingerprint"]?.GetValue<string>(), afterLocal["fingerprint"]?.GetValue<string>());
    }

    [Fact]
    public async Task LocalGit_WindowsAndPosixUnicodePaths()
    {
        var repoDir = Path.Combine(tempDir, "unicode-paths-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");
        RunGitCommand(repoDir, "config", "user.name", "Fixture");
        RunGitCommand(repoDir, "config", "user.email", "fixture@example.invalid");
        RunGitCommand(repoDir, "config", "core.quotepath", "false");

        File.WriteAllText(Path.Combine(repoDir, "init.txt"), "init");
        RunGitCommand(repoDir, "add", ".");
        RunGitCommand(repoDir, "commit", "-qm", "Initial commit");

        var subDir = Path.Combine(repoDir, "dossier");
        Directory.CreateDirectory(subDir);
        var unicodeFile = Path.Combine(subDir, "café_☕_文件.txt");
        File.WriteAllText(unicodeFile, "unicode file content with 漢字 and accents: éàï");

        var result = await ProjectHome.LocalGitAsync(repoDir);
        Assert.True(result["readable"]?.GetValue<bool>());
        Assert.NotNull(result["fingerprint"]);

        var files = (JsonArray)result["files"]!;
        Assert.Single(files);
        var item = (JsonObject)files[0]!;
        Assert.Equal("dossier/café_☕_文件.txt".Replace('\\', '/'), item["path"]?.GetValue<string>()?.Replace('\\', '/'));
        Assert.Equal("??", item["status"]?.GetValue<string>());
        Assert.NotNull(item["fingerprint"]?.GetValue<string>());
    }

    [Fact]
    public async Task LocalGit_RealGit_PlanFileExclusion_LiveRepo()
    {
        var repoDir = Path.Combine(tempDir, "real-plan-excl-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var planDir = Path.Combine(repoDir, ".planwharf");
        Directory.CreateDirectory(planDir);
        var planFilePath = Path.Combine(planDir, "plan.md");
        File.WriteAllText(planFilePath, "# Living Plan");

        var codeFile = Path.Combine(repoDir, "code.txt");
        File.WriteAllText(codeFile, "code content");

        var options = new ProjectHomeOptions
        {
            PlanFile = planFilePath
        };

        var result = await ProjectHome.LocalGitAsync(repoDir, options);
        Assert.True(result["readable"]?.GetValue<bool>());

        var files = ((JsonArray)result["files"]!).OfType<JsonObject>().Select(f => f["path"]?.GetValue<string>()).ToList();
        Assert.Contains("code.txt", files);
        Assert.DoesNotContain(".planwharf/plan.md", files);
        Assert.DoesNotContain(".planwharf\\plan.md", files);
    }

    [Fact]
    public async Task Compare_CommitCountOver100_ShowsSummaryMessage()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject { ["head"] = "1111", ["root"] = "/repo", ["scope"] = "." }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject { ["head"] = "2222", ["root"] = "/repo", ["scope"] = "." }
        };

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--is-ancestor")) return Task.FromResult<string?>("");
                if (args.Contains("--count")) return Task.FromResult<string?>("150");
                if (args.Contains("-100")) return Task.FromResult<string?>("2222\tCommit subject");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current, options);
        var changes = (result["changes"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        Assert.Contains("150 commits added", changes);

        var details = (result["details"] as JsonArray ?? []).Select(d => d?.GetValue<string>()).ToList();
        Assert.Contains("Commit 2222: Commit subject", details);
        Assert.Contains("Showing the latest 100 commit subjects.", details);
    }

    [Fact]
    public async Task Compare_CommitCountZero_NoCommitsAdded()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject { ["head"] = "1111", ["root"] = "/repo", ["scope"] = "." }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject { ["head"] = "2222", ["root"] = "/repo", ["scope"] = "." }
        };

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--is-ancestor")) return Task.FromResult<string?>("");
                if (args.Contains("--count")) return Task.FromResult<string?>("0");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current, options);
        var changes = (result["changes"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        Assert.DoesNotContain(changes, c => c != null && c.Contains("commits added"));
    }

    [Fact]
    public async Task Compare_CommitCountInvalid_GitHistoryUnavailable()
    {
        var prev = new JsonObject
        {
            ["git"] = new JsonObject { ["head"] = "1111", ["root"] = "/repo", ["scope"] = "." }
        };
        var current = new JsonObject
        {
            ["git"] = new JsonObject { ["head"] = "2222", ["root"] = "/repo", ["scope"] = "." }
        };

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--is-ancestor")) return Task.FromResult<string?>("");
                if (args.Contains("--count")) return Task.FromResult<string?>("fatal: ambiguous argument");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current, options);
        var unavailable = (result["unavailable"] as JsonArray ?? []).Select(u => u?.GetValue<string>()).ToList();
        Assert.Contains("Git history changed; a reliable commit count is unavailable.", unavailable);
    }

    [Fact]
    public async Task LocalGit_DetailFiles_Over8MiB_FingerprintNull()
    {
        var repoDir = Path.Combine(tempDir, "detail-8mb-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        var file1 = Path.Combine(repoDir, "file1.bin");
        var file2 = Path.Combine(repoDir, "file2.bin");

        // 5 MiB file and 4 MiB file -> total 9 MiB > 8 MiB
        using (var fs = new FileStream(file1, FileMode.Create, FileAccess.Write)) fs.SetLength(5 * 1024 * 1024);
        using (var fs = new FileStream(file2, FileMode.Create, FileAccess.Write)) fs.SetLength(4 * 1024 * 1024);

        var options = new ProjectHomeOptions
        {
            Git = (f, args) =>
            {
                if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(repoDir);
                if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
                if (args.Contains("ls-files")) return Task.FromResult<string?>("");
                if (args.Contains("status") && args.Contains("-z")) return Task.FromResult<string?>("?? file1.bin\0?? file2.bin\0");
                if (args.Contains("status")) return Task.FromResult<string?>("?? file1.bin\n?? file2.bin\n");
                if (args.Contains("diff")) return Task.FromResult<string?>("");
                return Task.FromResult<string?>("");
            }
        };

        var result = await ProjectHome.LocalGitAsync(repoDir, options);
        Assert.True(result["readable"]?.GetValue<bool>());

        var files = (JsonArray)result["files"]!;
        Assert.Equal(2, files.Count);
        var f1 = (JsonObject)files[0]!;
        var f2 = (JsonObject)files[1]!;

        Assert.NotNull(f1["fingerprint"]?.GetValue<string>()); // First file is under 8 MiB
        Assert.Null(f2["fingerprint"]); // Cumulative bytes exceed 8 MiB -> fingerprint is null
    }

    [Fact]
    public async Task ProjectHome_ObservationDoesNotDispatchOrMutateSource()
    {
        var repoDir = Path.Combine(tempDir, "read-only-obs-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");
        RunGitCommand(repoDir, "config", "user.name", "Fixture");
        RunGitCommand(repoDir, "config", "user.email", "fixture@example.invalid");

        var trackedFile = Path.Combine(repoDir, "tracked.txt");
        File.WriteAllText(trackedFile, "initial content");
        RunGitCommand(repoDir, "add", "tracked.txt");
        RunGitCommand(repoDir, "commit", "-qm", "Initial commit");

        var untrackedFile = Path.Combine(repoDir, "untracked.txt");
        File.WriteAllText(untrackedFile, "dirty content");

        // Record all file hashes before observation
        var beforeHashes = Directory.GetFiles(repoDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

        var project = Item(repoDir);
        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["status"] = "idle" }
            }
        };

        var home = await ProjectHome.GetProjectHomeAsync(project, snapshot);
        Assert.NotNull(home);

        var current = home["current"] as JsonObject;
        var comparison = await ProjectHome.CompareAsync(null, current);
        Assert.True(comparison["firstVisit"]?.GetValue<bool>());

        // Verify files are completely unchanged
        var afterFiles = Directory.GetFiles(repoDir, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(beforeHashes.Count, afterFiles.Count);
        foreach (var file in afterFiles)
        {
            Assert.True(beforeHashes.ContainsKey(file), $"Unexpected file created: {file}");
            var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            Assert.Equal(beforeHashes[file], currentHash);
        }
    }

    [Fact]
    public async Task ProjectHome_NowText_AllLifecycleStates()
    {
        var folder = Path.Combine(tempDir, "now-text-repo");
        Directory.CreateDirectory(folder);
        var project = Item(folder);

        // 1. Running workers
        var rowRunning = new JsonObject
        {
            ["id"] = "alpha",
            ["dispatch"] = new JsonArray { new JsonObject { ["alive"] = true } }
        };
        var homeRunning = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowRunning } });
        Assert.Equal("Workers running", homeRunning["now"]?.GetValue<string>());

        // 2. MegaTask PAUSED with 2 remaining slices
        var durablePaused = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "m1", ["projectId"] = "alpha", ["status"] = "PAUSED" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "s1", ["megaTaskId"] = "m1", ["status"] = "COMPLETE" },
                new JsonObject { ["id"] = "s2", ["megaTaskId"] = "m1", ["status"] = "PENDING" },
                new JsonObject { ["id"] = "s3", ["megaTaskId"] = "m1", ["status"] = "PENDING" }
            }
        };
        var homePaused = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "m1" } },
            ["execution"] = durablePaused
        });
        Assert.Equal("Paused \u2014 checkpoint saved (2 slices remaining)", homePaused["now"]?.GetValue<string>());

        // 3. MegaTask FAILED with 1 remaining slice
        var durableFailed = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "m2", ["projectId"] = "alpha", ["status"] = "FAILED" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "s1", ["megaTaskId"] = "m2", ["status"] = "FAILED" }
            }
        };
        var homeFailed = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "m2" } },
            ["execution"] = durableFailed
        });
        Assert.Equal("Failed \u2014 checkpoint saved (1 slices remaining)", homeFailed["now"]?.GetValue<string>());

        // 4. MegaTask COMPLETE
        var durableComplete = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "m3", ["projectId"] = "alpha", ["status"] = "COMPLETE" }
            }
        };
        var homeComplete = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "m3" } },
            ["execution"] = durableComplete
        });
        Assert.Equal("Completed", homeComplete["now"]?.GetValue<string>());

        // 5. MegaTask READY_FOR_APPROVAL
        var durableApprove = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "m4", ["projectId"] = "alpha", ["status"] = "READY_FOR_APPROVAL" }
            }
        };
        var homeApprove = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "m4" } },
            ["execution"] = durableApprove
        });
        Assert.Equal("Waiting for approval", homeApprove["now"]?.GetValue<string>());

        // 6. Row fallback labels
        var labels = new Dictionary<string, string>
        {
            ["watching"] = "Waiting for worker progress",
            ["needs_approval"] = "Waiting for approval",
            ["waiting_on_you"] = "Waiting for your answer",
            ["propose_grill"] = "Waiting for your answer",
            ["checkpointed"] = "Paused \u2014 checkpoint saved",
            ["complete"] = "Completed",
            ["failed"] = "Blocked",
            ["blocked_no_worker"] = "Blocked",
            ["blocked_no_folder"] = "Blocked",
            ["idle"] = "Idle"
        };
        foreach (var (status, expected) in labels)
        {
            var homeRow = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
            {
                ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["status"] = status } }
            });
            Assert.Equal(expected, homeRow["now"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task ProjectHome_ActionTarget_SingleResumeTarget_DetachedResearch()
    {
        var folder = Path.Combine(tempDir, "action-target-repo");
        Directory.CreateDirectory(folder);
        var project = Item(folder);

        // Case A: Single resume target with researchId
        var durableResume = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-Resume", ["projectId"] = "alpha", ["status"] = "PAUSED" }
            },
            ["checkpoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "CP-R1",
                    ["projectId"] = "alpha",
                    ["megaTaskId"] = "MT-Resume",
                    ["repoState"] = new JsonObject
                    {
                        ["repoHead"] = "1111",
                        ["comparable"] = true,
                        ["dirtyFiles"] = new JsonArray()
                    }
                }
            }
        };
        var snapshotResume = new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-Resume" } },
            ["researches"] = new JsonArray
            {
                new JsonObject { ["id"] = "RES-1", ["projectId"] = "alpha", ["megaTaskId"] = "MT-Resume" }
            },
            ["execution"] = durableResume
        };
        var homeResume = await ProjectHome.GetProjectHomeAsync(project, snapshotResume);
        var actionTargetResume = (JsonObject)homeResume["actionTarget"]!;
        Assert.Equal("alpha", actionTargetResume["projectId"]?.GetValue<string>());
        Assert.Equal("MT-Resume", actionTargetResume["megaTaskId"]?.GetValue<string>());
        Assert.Equal("CP-R1", actionTargetResume["checkpointId"]?.GetValue<string>());
        Assert.Equal("RES-1", actionTargetResume["researchId"]?.GetValue<string>());

        // Case B: Detached MegaTask
        var durableDetached = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-Detached", ["projectId"] = "alpha", ["status"] = "RUNNING", ["detached"] = true }
            }
        };
        var snapshotDetached = new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-Detached" } },
            ["researches"] = new JsonArray
            {
                new JsonObject { ["id"] = "RES-2", ["projectId"] = "alpha", ["megaTaskId"] = "MT-Detached" }
            },
            ["execution"] = durableDetached
        };
        var homeDetached = await ProjectHome.GetProjectHomeAsync(project, snapshotDetached);
        var actionTargetDetached = (JsonObject)homeDetached["actionTarget"]!;
        Assert.Equal("alpha", actionTargetDetached["projectId"]?.GetValue<string>());
        Assert.Equal("MT-Detached", actionTargetDetached["megaTaskId"]?.GetValue<string>());
        Assert.Equal("RES-2", actionTargetDetached["researchId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProjectHome_ExecutionResult_ExactSizeBounds()
    {
        var repoDir = Path.Combine(tempDir, "size-bounds-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        // Exactly 8 MiB file
        var exact8MbFile = Path.Combine(tempDir, "exact8mb.json");
        using (var fs = new FileStream(exact8MbFile, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(8 * 1024 * 1024);
        }

        // 8 MiB + 1 byte file
        var over8MbFile = Path.Combine(tempDir, "over8mb.json");
        using (var fs = new FileStream(over8MbFile, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(8 * 1024 * 1024 + 1);
        }

        // Directory as resultPath
        var dirResult = Path.Combine(tempDir, "dir-result");
        Directory.CreateDirectory(dirResult);

        var project = Item(repoDir);

        // 1. Exactly 8 MiB is hashed
        var homeExact = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["resultPath"] = exact8MbFile } }
        });
        Assert.NotNull(homeExact["current"]?["execution"]?["result"]?.GetValue<string>());

        // 2. Over 8 MiB is rejected (null)
        var homeOver = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["resultPath"] = over8MbFile } }
        });
        Assert.Null(homeOver["current"]?["execution"]?["result"]);

        // 3. Directory is rejected (null)
        var homeDir = await ProjectHome.GetProjectHomeAsync(project, new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["resultPath"] = dirResult } }
        });
        Assert.Null(homeDir["current"]?["execution"]?["result"]);
    }

    [Fact]
    public async Task ProjectHome_RecentActions_Max12Messages_AndFallbacks()
    {
        var repoDir = Path.Combine(tempDir, "recent-actions-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        // Create log file with 20 actions
        var logFile = Path.Combine(tempDir, "long-worker.log");
        var sb = new StringBuilder();
        for (int i = 1; i <= 20; i++)
        {
            sb.Append($"{{\"event\":\"step_update\",\"step_update\":{{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"read_file\",\"tool_info\":{{\"parameters\":{{\"path\":\"file_{i}.txt\"}}}}}}}}\n");
        }
        File.WriteAllText(logFile, sb.ToString());

        var project = Item(repoDir);

        // 1. Empty label falls back to modelId
        var rowDispatch = new JsonObject
        {
            ["id"] = "alpha",
            ["dispatch"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = "",
                    ["modelId"] = "gpt-4o",
                    ["logPath"] = logFile
                }
            }
        };

        var homeDispatch = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowDispatch } });
        var actions = (homeDispatch["progressDetails"]?["actions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        Assert.Equal(12, actions.Count); // Sliced to last 12
        Assert.All(actions, a => Assert.Equal("gpt-4o", a["worker"]?.GetValue<string>()));
        Assert.Equal("Finished reading file_20.txt.", actions.Last()["text"]?.GetValue<string>());

        // 2. No dispatch messages falls back to workerTail, and empty workerLabel falls back to "Worker"
        var rowTail = new JsonObject
        {
            ["id"] = "alpha",
            ["workerTail"] = "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"read_file\",\"tool_info\":{\"parameters\":{\"path\":\"tail_file.txt\"}}}}\n",
            ["workerLabel"] = ""
        };

        var homeTail = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowTail } });
        var tailActions = (homeTail["progressDetails"]?["actions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        Assert.Single(tailActions);
        Assert.Equal("Worker", tailActions[0]["worker"]?.GetValue<string>());
        Assert.Equal("Finished reading tail_file.txt.", tailActions[0]["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task Compare_ResearchAndMilestoneDeltas()
    {
        var prev = new JsonObject
        {
            ["execution"] = new JsonObject
            {
                ["status"] = "idle",
                ["research"] = new JsonArray
                {
                    new JsonObject { ["id"] = "R-1", ["status"] = "watching", ["attemptId"] = "a1", ["reportAvailable"] = false },
                    new JsonObject { ["id"] = "R-2", ["status"] = "watching", ["attemptId"] = "a2", ["reportAvailable"] = false }
                }
            }
        };

        var current = new JsonObject
        {
            ["execution"] = new JsonObject
            {
                ["status"] = "idle",
                ["research"] = new JsonArray
                {
                    new JsonObject { ["id"] = "R-1", ["status"] = "complete", ["attemptId"] = "a1", ["reportAvailable"] = true },
                    new JsonObject { ["id"] = "R-2", ["status"] = "watching", ["attemptId"] = "a3", ["reportAvailable"] = false }
                }
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current);
        var changes = (result["changes"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        Assert.Contains("Research completed", changes); // R-1 became complete
        Assert.Contains("Research changed", changes); // R-2 attemptId changed
    }

    [Fact]
    public async Task Compare_LivingPlan_StateDirectionAndClosedDecisions()
    {
        var prev = new JsonObject
        {
            ["revisions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["title"] = "First Plan",
                    ["direction"] = "Initial goal",
                    ["state"] = "Initial state",
                    ["closed"] = new JsonArray { "decision-1" }
                }
            }
        };

        var current = new JsonObject
        {
            ["revisions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 2,
                    ["title"] = "Second Plan",
                    ["direction"] = "New updated goal",
                    ["state"] = "Updated progress state",
                    ["closed"] = new JsonArray { "decision-1", "decision-2" }
                }
            }
        };

        var result = await ProjectHome.CompareAsync(prev, current);
        var details = (result["details"] as JsonArray ?? []).Select(d => d?.GetValue<string>()).ToList();
        Assert.Contains("Living Plan R2: Second Plan", details);
        Assert.Contains("Recorded progress: Updated progress state", details);
        Assert.Contains("Updated goal: New updated goal", details);
        Assert.Contains("Decision recorded: decision-2", details);
    }

    [Fact]
    public async Task LocalGit_InjectedAdapter_DirectorySymlinkToAnotherProject_FailsSafely()
    {
        var root = Path.Combine(tempDir, "inj-symlink-root");
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        RunGitCommand(alpha, "init", "-q");

        var fakeFiles = new FakeProjectHomeFiles();
        fakeFiles.RealPaths[Path.Combine(alpha, "linked")] = beta;
        fakeFiles.Symlinks[Path.Combine(alpha, "linked")] = beta;

        var gitAdapter = new FakeProjectHomeGit((folder, args) =>
        {
            if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(alpha);
            if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
            if (args.Contains("ls-files")) return Task.FromResult<string?>("");
            if (args.Contains("status") && args.Contains("-z")) return Task.FromResult<string?>(" D linked/data\0");
            if (args.Contains("status")) return Task.FromResult<string?>(" D linked/data\n");
            if (args.Contains("diff")) return Task.FromResult<string?>("");
            return Task.FromResult<string?>("");
        });

        var options = new ProjectHomeOptions
        {
            Files = fakeFiles,
            GitAdapter = gitAdapter
        };

        var before = await ProjectHome.LocalGitAsync(alpha, options);
        Assert.True(before["readable"]?.GetValue<bool>());

        var files = (JsonArray)before["files"]!;
        Assert.Single(files);
        var fileItem = (JsonObject)files[0]!;
        Assert.Equal("linked/data", fileItem["path"]?.GetValue<string>());
        Assert.Equal("deleted", fileItem["fingerprint"]?.GetValue<string>());

        var after = await ProjectHome.LocalGitAsync(alpha, options);
        Assert.Equal(before["fingerprint"]?.GetValue<string>(), after["fingerprint"]?.GetValue<string>());
    }

    [Fact]
    public async Task LocalGit_InjectedAdapter_SubmoduleForeignGitdir_IgnoresForeignCommit()
    {
        var root = Path.Combine(tempDir, "inj-submodule-root");
        var alpha = Path.Combine(root, "alpha");
        Directory.CreateDirectory(alpha);
        RunGitCommand(alpha, "init", "-q");

        var localContent = "initial local content";
        var fakeFiles = new FakeProjectHomeFiles();
        fakeFiles.Files[Path.Combine(alpha, "local.txt")] = Encoding.UTF8.GetBytes(localContent);

        var gitAdapter = new FakeProjectHomeGit((folder, args) =>
        {
            if (args.Contains("status") || args.Contains("diff"))
                Assert.Contains("--ignore-submodules=all", args);
            if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(alpha);
            if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
            if (args.Contains("ls-files")) return Task.FromResult<string?>("local.txt\0");
            if (args.Contains("status") && args.Contains("-z")) return Task.FromResult<string?>("?? local.txt\0");
            if (args.Contains("status")) return Task.FromResult<string?>("?? local.txt\n");
            if (args.Contains("diff")) return Task.FromResult<string?>("");
            return Task.FromResult<string?>("");
        });

        var options = new ProjectHomeOptions
        {
            Files = fakeFiles,
            GitAdapter = gitAdapter
        };

        var before = await ProjectHome.LocalGitAsync(alpha, options);
        Assert.True(before["readable"]?.GetValue<bool>());
        var fpBefore = before["fingerprint"]?.GetValue<string>();
        Assert.NotNull(fpBefore);

        // When local file content changes, fingerprint changes
        fakeFiles.Files[Path.Combine(alpha, "local.txt")] = Encoding.UTF8.GetBytes("updated local content");
        var after = await ProjectHome.LocalGitAsync(alpha, options);
        var fpAfter = after["fingerprint"]?.GetValue<string>();
        Assert.NotNull(fpAfter);
        Assert.NotEqual(fpBefore, fpAfter);
    }

    [Fact]
    public async Task ProjectHome_ProgressDetail_PortFromJS_WithInjectedAdapters()
    {
        var root = Path.Combine(tempDir, "prog-detail-inj");
        var folder = Path.Combine(root, "project");
        Directory.CreateDirectory(folder);
        RunGitCommand(folder, "init", "-q");

        var logPath = Path.Combine(root, "worker.log");
        var initialLog = "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"view_file\",\"tool_info\":{\"parameters\":{\"AbsolutePath\":\"" + folder.Replace('\\', '/') + "/Sayaku.ts\"}}}}\n";

        var fakeFiles = new FakeProjectHomeFiles();
        fakeFiles.Files[logPath] = Encoding.UTF8.GetBytes(initialLog);
        fakeFiles.Files[Path.Combine(folder, "Sayaku.ts")] = Encoding.UTF8.GetBytes("export const greeting = \"hello\";\n");

        var gitHead = "1111111111111111111111111111111111111111";
        var gitStatus = "";
        var gitDiff = "";
        var gitUntracked = "";
        var gitAdapter = new FakeProjectHomeGit((f, args) =>
        {
            if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(folder);
            if (args.Contains("--verify")) return Task.FromResult<string?>(gitHead);
            if (args.Contains("--is-ancestor")) return Task.FromResult<string?>("");
            if (args.Contains("--count")) return Task.FromResult<string?>("1");
            if (args.Contains("-100")) return Task.FromResult<string?>("2222222\tUpdate greeting behavior");
            if (args.Contains("ls-files")) return Task.FromResult<string?>(gitUntracked);
            if (args.Contains("status")) return Task.FromResult<string?>(gitStatus);
            if (args.Contains("diff")) return Task.FromResult<string?>(gitDiff);
            return Task.FromResult<string?>("");
        });

        var options = new ProjectHomeOptions
        {
            Files = fakeFiles,
            GitAdapter = gitAdapter
        };

        var project = Item(folder, "fixture");
        var row = new JsonObject
        {
            ["id"] = "fixture",
            ["status"] = "watching",
            ["jobId"] = "audit",
            ["jobTitle"] = "Audit Sayaku greeting behavior",
            ["dispatch"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = "Fixture worker",
                    ["logPath"] = logPath,
                    ["alive"] = false
                }
            }
        };
        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray { row },
            ["researches"] = new JsonArray()
        };

        // First visit
        var first = await ProjectHome.GetProjectHomeAsync(project, snapshot, (JsonNode?)null, options);
        Assert.NotNull(first["current"]);

        // Simulate progress
        gitHead = "2222222222222222222222222222222222222222";
        gitStatus = "?? Sayaku.test.ts\n";
        gitUntracked = "Sayaku.test.ts\0";
        fakeFiles.Files[Path.Combine(folder, "Sayaku.test.ts")] = Encoding.UTF8.GetBytes("fixture test source\n");

        var revArray = (JsonArray)project["revisions"]!;
        var latest = (JsonObject)revArray.Last()!;
        var nextRev = new JsonObject
        {
            ["id"] = latest["id"]!.GetValue<int>() + 1,
            ["title"] = "Greeting audit findings",
            ["state"] = "Greeting behavior updated; integration review remains.",
            ["direction"] = latest["direction"]?.DeepClone(),
            ["open"] = new JsonArray(),
            ["closed"] = new JsonArray { "Preserve the configured greeting." }
        };
        revArray.Add(nextRev);

        row["status"] = "complete";
        var stepLog1 = "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"write_to_file\",\"tool_info\":{\"parameters\":{\"TargetFile\":\"" + folder.Replace('\\', '/') + "/Sayaku.test.ts\"}}}}\n";
        var stepLog2 = "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"run_command\",\"tool_info\":{\"parameters\":{\"CommandLine\":\"node Sayaku.test.ts\"}}}}\n";
        var report = "Greeting parsing now preserves the configured message.\n" + string.Concat(Enumerable.Repeat("Fixture report details are retained without shortening.\n", 30));
        var resultEvent = "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"" + report.Replace("\n", "\\n") + "\"}}\n";
        var updatedLog = initialLog + stepLog1 + stepLog2 + resultEvent;
        fakeFiles.Files[logPath] = Encoding.UTF8.GetBytes(updatedLog);

        // Second visit with baseline
        var returned = await ProjectHome.GetProjectHomeAsync(project, snapshot, first["current"], options);

        var details = ((JsonArray)returned["since"]!["details"]!).Select(d => d?.GetValue<string>()).ToList();
        var detailsJoined = string.Join("\n", details);
        Assert.Matches("Update greeting behavior", detailsJoined);
        Assert.Matches("Added locally: Sayaku.test.ts", detailsJoined);
        Assert.Matches("Recorded progress: Greeting behavior updated", detailsJoined);
        Assert.Matches("Decision recorded: Preserve the configured greeting", detailsJoined);

        var actions = ((JsonArray)returned["progressDetails"]!["actions"]!).Select(a => a?["text"]?.GetValue<string>()).ToList();
        var actionsJoined = string.Join("\n", actions);
        Assert.Matches("writing project files: Sayaku.test.ts", actionsJoined);
        Assert.Matches("Finished running tests for Sayaku.test.ts", actionsJoined);

        var actionsJson = returned["progressDetails"]!["actions"]!.ToJsonString();
        Assert.DoesNotMatch("step_update|conversation_id|tool_info", actionsJson);

        Assert.Equal("Greeting behavior updated; integration review remains.", returned["progress"]?.GetValue<string>());

        var reports = (JsonArray)returned["progressDetails"]!["reports"]!;
        Assert.Single(reports);
        Assert.Equal(report, reports[0]?["text"]?.GetValue<string>());

        var currentJson = returned["current"]!.ToJsonString();
        Assert.DoesNotMatch("Fixture report details", currentJson);

        var currentExecution = (JsonObject)returned["current"]!["execution"]!;
        var fp = currentExecution["reportFingerprint"]?.GetValue<string>();
        Assert.NotNull(fp);
        Assert.Matches("^[a-f0-9]{64}$", fp);
    }

    [Fact]
    public async Task ProjectHome_ProjectContext_StrictIsolationBetweenProjects()
    {
        var folderA = Path.Combine(tempDir, "iso-proj-a");
        var folderB = Path.Combine(tempDir, "iso-proj-b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        RunGitCommand(folderA, "init", "-q");
        RunGitCommand(folderB, "init", "-q");

        var projectA = Item(folderA, "alpha");

        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-A", ["status"] = "watching" },
                new JsonObject { ["id"] = "beta", ["megaTaskId"] = "MT-B", ["status"] = "complete" }
            },
            ["researches"] = new JsonArray
            {
                new JsonObject { ["id"] = "research-alpha", ["projectId"] = "alpha", ["megaTaskId"] = "MT-A", ["status"] = "complete" },
                new JsonObject { ["id"] = "research-beta", ["projectId"] = "beta", ["megaTaskId"] = "MT-B", ["status"] = "complete" }
            },
            ["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray
                {
                    new JsonObject { ["id"] = "MT-A", ["projectId"] = "alpha", ["status"] = "PAUSED", ["title"] = "Alpha task", ["createdAt"] = "2026-11-25T12:00:00.000Z" },
                    new JsonObject { ["id"] = "MT-B", ["projectId"] = "beta", ["status"] = "COMPLETE", ["title"] = "Beta task", ["createdAt"] = "2026-11-25T12:00:00.000Z" }
                },
                ["subTasks"] = new JsonArray
                {
                    new JsonObject { ["id"] = "ST-A1", ["megaTaskId"] = "MT-A", ["title"] = "Sub A", ["status"] = "RUNNING", ["attemptIds"] = new JsonArray { "att-a1" }, ["assignedModelId"] = "gpt-4o" },
                    new JsonObject { ["id"] = "ST-B1", ["megaTaskId"] = "MT-B", ["title"] = "Sub B", ["status"] = "COMPLETE", ["attemptIds"] = new JsonArray { "att-b1" }, ["assignedModelId"] = "claude-3" }
                },
                ["results"] = new JsonArray
                {
                    new JsonObject { ["resultId"] = "R-A", ["megaTaskId"] = "MT-A", ["subTaskId"] = "ST-A1", ["status"] = "RUNNING", ["pid"] = 12345, ["assignedModelId"] = "gpt-4o" },
                    new JsonObject { ["resultId"] = "R-B", ["megaTaskId"] = "MT-B", ["subTaskId"] = "ST-B1", ["status"] = "COMPLETE", ["pid"] = 54321, ["assignedModelId"] = "claude-3" }
                },
                ["timeline"] = new JsonArray
                {
                    new JsonObject { ["id"] = "TL-A", ["projectId"] = "alpha", ["megaTaskId"] = "MT-A", ["createdAt"] = "2026-11-25T12:01:00.000Z", ["summary"] = "Alpha milestone" },
                    new JsonObject { ["id"] = "TL-B", ["projectId"] = "beta", ["megaTaskId"] = "MT-B", ["createdAt"] = "2026-11-25T12:02:00.000Z", ["summary"] = "Beta milestone" }
                },
                ["checkpoints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "CP-A",
                        ["projectId"] = "alpha",
                        ["megaTaskId"] = "MT-A",
                        ["repoState"] = new JsonObject { ["repoHead"] = "1111", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() }
                    },
                    new JsonObject
                    {
                        ["id"] = "CP-B",
                        ["projectId"] = "beta",
                        ["megaTaskId"] = "MT-B",
                        ["repoState"] = new JsonObject { ["repoHead"] = "2222", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() }
                    }
                }
            }
        };

        var home = await ProjectHome.GetProjectHomeAsync(projectA, snapshot);

        Assert.Equal("alpha", home["projectId"]?.GetValue<string>());
        Assert.Equal("Alpha task", home["jobTitle"]?.GetValue<string>());

        var research = (JsonArray)home["current"]!["execution"]!["research"]!;
        Assert.Single(research);
        Assert.Equal("research-alpha", research[0]?["id"]?.GetValue<string>());

        var progressDetails = (JsonObject)home["progressDetails"]!;
        Assert.Equal("MT-A", progressDetails["megaTask"]?["id"]?.GetValue<string>());

        var subTasks = (JsonArray)progressDetails["subTasks"]!;
        Assert.Single(subTasks);
        Assert.Equal("ST-A1", subTasks[0]?["id"]?.GetValue<string>());

        var results = (JsonArray)progressDetails["results"]!;
        Assert.Single(results);
        Assert.Equal("R-A", results[0]?["resultId"]?.GetValue<string>());

        var workers = (JsonArray)progressDetails["workers"]!;
        Assert.All(workers, w => Assert.Equal("gpt-4o", w?["modelId"]?.GetValue<string>()));
        Assert.DoesNotContain(workers, w => w?["modelId"]?.GetValue<string>() == "claude-3");

        var timeline = (JsonArray)home["timeline"]!;
        Assert.Single(timeline);
        Assert.Equal("TL-A", timeline[0]?["id"]?.GetValue<string>());

        var resumeTargets = (JsonArray)home["resumeTargets"]!;
        Assert.Single(resumeTargets);
        Assert.Equal("CP-A", resumeTargets[0]?["id"]?.GetValue<string>());
        Assert.Equal("research-alpha", resumeTargets[0]?["researchId"]?.GetValue<string>());

        var checkpointDiffs = (JsonArray)home["checkpointDiffs"]!;
        Assert.Single(checkpointDiffs);
        Assert.Equal("CP-A", checkpointDiffs[0]?["checkpointId"]?.GetValue<string>());

        var actionTarget = (JsonObject)home["actionTarget"]!;
        Assert.Equal("alpha", actionTarget["projectId"]?.GetValue<string>());
        Assert.Equal("MT-A", actionTarget["megaTaskId"]?.GetValue<string>());
        Assert.Equal("CP-A", actionTarget["checkpointId"]?.GetValue<string>());
        Assert.Equal("research-alpha", actionTarget["researchId"]?.GetValue<string>());

        var json = home.ToJsonString();
        Assert.DoesNotMatch("beta|Beta|MT-B|ST-B1|research-beta|CP-B|TL-B", json);
    }

    [Fact]
    public async Task ProjectHome_ProjectRecipient_ActionTargetAndWorkerGuards()
    {
        var folder = Path.Combine(tempDir, "recipient-repo");
        Directory.CreateDirectory(folder);
        RunGitCommand(folder, "init", "-q");
        var project = Item(folder, "alpha");

        // Case 1: Multiple resume targets -> actionTarget does NOT specify a single checkpointId
        var durableMulti = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-Multi", ["projectId"] = "alpha", ["status"] = "PAUSED" }
            },
            ["checkpoints"] = new JsonArray
            {
                new JsonObject { ["id"] = "CP-1", ["projectId"] = "alpha", ["megaTaskId"] = "MT-Multi", ["repoState"] = new JsonObject { ["repoHead"] = "1111", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() } },
                new JsonObject { ["id"] = "CP-2", ["projectId"] = "alpha", ["megaTaskId"] = "MT-Multi", ["repoState"] = new JsonObject { ["repoHead"] = "2222", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() } }
            }
        };
        var snapshotMulti = new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-Multi" } },
            ["execution"] = durableMulti
        };
        var homeMulti = await ProjectHome.GetProjectHomeAsync(project, snapshotMulti);
        var atMulti = (JsonObject)homeMulti["actionTarget"]!;
        Assert.Equal("alpha", atMulti["projectId"]?.GetValue<string>());
        Assert.Equal("MT-Multi", atMulti["megaTaskId"]?.GetValue<string>());
        Assert.Null(atMulti["checkpointId"]); // Not set when multiple resume targets exist

        // Case 2: Unlinked workspace guard -> action is always null
        var unlinkedProject = Item(null, "alpha");
        var homeUnlinked = await ProjectHome.GetProjectHomeAsync(unlinkedProject, snapshotMulti);
        Assert.Null(homeUnlinked["action"]);

        // Case 3: Detached MegaTask without single resume target receives researchId
        var durableDetached = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-Det", ["projectId"] = "alpha", ["status"] = "RUNNING", ["detached"] = true }
            }
        };
        var snapshotDetached = new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-Det" } },
            ["researches"] = new JsonArray
            {
                new JsonObject { ["id"] = "RES-Det", ["projectId"] = "alpha", ["megaTaskId"] = "MT-Det" }
            },
            ["execution"] = durableDetached
        };
        var homeDetached = await ProjectHome.GetProjectHomeAsync(project, snapshotDetached);
        var atDet = (JsonObject)homeDetached["actionTarget"]!;
        Assert.Equal("RES-Det", atDet["researchId"]?.GetValue<string>());
        Assert.Equal("MT-Det", atDet["megaTaskId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProjectHome_ExecutionNext_AllGraphCasesAThroughJ_MatchesJS()
    {
        var folder = Path.Combine(tempDir, "exec-next-graph");
        Directory.CreateDirectory(folder);
        RunGitCommand(folder, "init", "-q");

        static JsonObject MakeGraph(string status = "RUNNING", string first = "RUNNING", string second = "PENDING")
        {
            var mega = new JsonObject
            {
                ["id"] = "MT-test",
                ["projectId"] = "isolated",
                ["status"] = status,
                ["nextAction"] = "Review the proposed slices and approve this MegaTask.",
                ["currentCheckpointId"] = "CP-test"
            };
            return new JsonObject
            {
                ["megaTasks"] = new JsonArray { mega },
                ["subTasks"] = new JsonArray
                {
                    new JsonObject { ["id"] = "ST-first", ["megaTaskId"] = "MT-test", ["title"] = "Create module", ["status"] = first, ["dependencies"] = new JsonArray(), ["attemptIds"] = new JsonArray(), ["resultId"] = "R-first" },
                    new JsonObject { ["id"] = "ST-second", ["megaTaskId"] = "MT-test", ["title"] = "Verify recovery", ["status"] = second, ["dependencies"] = new JsonArray { "ST-first" }, ["attemptIds"] = new JsonArray(), ["resultId"] = "R-second" }
                },
                ["results"] = new JsonArray(),
                ["checkpoints"] = new JsonArray()
            };
        }

        var cases = new (string Name, JsonObject Durable, string ExpectedPattern)[]
        {
            ("A proposal permits approval", MakeGraph("READY_FOR_APPROVAL", "PENDING"), "Approve the prepared MegaTask."),
            ("B approval immediately replaces proposal text", MakeGraph("RUNNING", "PENDING"), "^Next SubTask: Create module \\(ST-first\\)\\.$"),
            ("C active first slice", MakeGraph(), "^Work in progress: Create module \\(ST-first\\)\\.$"),
            ("D first complete and second pending", MakeGraph("RUNNING", "COMPLETE"), "^Next SubTask: Verify recovery \\(ST-second\\)\\.$"),
            ("E active second slice", MakeGraph("RUNNING", "COMPLETE", "RUNNING"), "^Work in progress: Verify recovery \\(ST-second\\)\\.$"),
            ("F dependency waiting identifies prerequisite", MakeGraph("RUNNING", "BLOCKED"), "Waiting for prerequisite Create module \\(ST-first\\) before Verify recovery \\(ST-second\\)\\."),
            ("reconciliation never reuses proposal", MakeGraph("RUNNING", "COMPLETE", "COMPLETE"), "^Reconciling completed work\\.$"),
            ("G paused fallback uses remaining work", MakeGraph("PAUSED", "COMPLETE"), "Next SubTask: Verify recovery"),
            ("I completion without a result action is neutral", MakeGraph("COMPLETE", "COMPLETE", "COMPLETE"), "^Review completed evidence\\.$"),
            ("J failed without structured recovery is neutral", MakeGraph("FAILED", "FAILED"), "Review Create module \\(ST-first\\) and resume from an explicit checkpoint\\.")
        };

        foreach (var (name, durable, pattern) in cases)
        {
            var project = Item(folder, "isolated");
            var snapshot = new JsonObject
            {
                ["execution"] = (JsonObject)durable.DeepClone(),
                ["projects"] = new JsonArray
                {
                    new JsonObject { ["id"] = "isolated", ["next"] = "Approve the prepared job", ["status"] = "watching", ["dispatch"] = new JsonArray() }
                },
                ["researches"] = new JsonArray()
            };

            var home = await ProjectHome.GetProjectHomeAsync(project, snapshot);
            var nextText = home["next"]?.GetValue<string>();
            Assert.NotNull(nextText);
            if (pattern.StartsWith("^") || pattern.EndsWith("$"))
                Assert.Matches(pattern, nextText);
            else
                Assert.Contains(pattern.Replace("\\(", "(").Replace("\\)", ")").Replace("\\.", "."), nextText);

            if (durable["megaTasks"]?[0]?["status"]?.GetValue<string>() == "READY_FOR_APPROVAL")
            {
                Assert.Equal("Approve the prepared MegaTask.", nextText);
                Assert.Equal("approve", home["action"]?.GetValue<string>());
            }
        }
    }

    [Fact]
    public async Task ProjectHome_ParallelProjects_ConcurrentInvocationsDoNotInterfere()
    {
        var folderA = Path.Combine(tempDir, "par-proj-a");
        var folderB = Path.Combine(tempDir, "par-proj-b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        RunGitCommand(folderA, "init", "-q");
        RunGitCommand(folderB, "init", "-q");

        var projectA = Item(folderA, "alpha");
        var projectB = Item(folderB, "beta");

        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-Par-A", ["projectId"] = "alpha", ["status"] = "RUNNING", ["title"] = "Work Alpha" },
                new JsonObject { ["id"] = "MT-Par-B", ["projectId"] = "beta", ["status"] = "READY_FOR_APPROVAL", ["title"] = "Work Beta" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "ST-Par-A", ["megaTaskId"] = "MT-Par-A", ["status"] = "RUNNING", ["title"] = "Slice A" },
                new JsonObject { ["id"] = "ST-Par-B", ["megaTaskId"] = "MT-Par-B", ["status"] = "PENDING", ["title"] = "Slice B" }
            }
        };

        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-Par-A" },
                new JsonObject { ["id"] = "beta", ["megaTaskId"] = "MT-Par-B" }
            },
            ["execution"] = durable
        };

        var taskA = ProjectHome.GetProjectHomeAsync(projectA, (JsonObject)snapshot.DeepClone());
        var taskB = ProjectHome.GetProjectHomeAsync(projectB, (JsonObject)snapshot.DeepClone());
        await Task.WhenAll(taskA, taskB);

        var homeA = await taskA;
        var homeB = await taskB;

        Assert.Equal("alpha", homeA["projectId"]?.GetValue<string>());
        Assert.Equal("Work in progress: Slice A (ST-Par-A).", homeA["next"]?.GetValue<string>());
        Assert.Null(homeA["action"]);

        Assert.Equal("beta", homeB["projectId"]?.GetValue<string>());
        Assert.Equal("Approve the prepared MegaTask.", homeB["next"]?.GetValue<string>());
        Assert.Equal("approve", homeB["action"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProjectHome_ExecutionResult_InjectedFiles_AllVariants()
    {
        var folder = Path.Combine(tempDir, "inj-res-folder");
        Directory.CreateDirectory(folder);
        RunGitCommand(folder, "init", "-q");
        var project = Item(folder, "alpha");

        var fakeFiles = new FakeProjectHomeFiles();
        var validContent = Encoding.UTF8.GetBytes("{\"status\":\"COMPLETE\",\"summary\":\"all tests passed\"}");
        var validPath = Path.Combine(folder, "valid.json");
        fakeFiles.Files[validPath] = validContent;

        var emptyPath = Path.Combine(folder, "empty.json");
        fakeFiles.Files[emptyPath] = Array.Empty<byte>();

        var oversizePath = Path.Combine(folder, "oversize.json");
        fakeFiles.Files[oversizePath] = new byte[8 * 1024 * 1024 + 1];

        var options = new ProjectHomeOptions { Files = fakeFiles };

        // 1. Valid file -> hashed
        var rowValid = new JsonObject { ["id"] = "alpha", ["resultPath"] = validPath };
        var homeValid = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowValid } }, options);
        var resHash = homeValid["current"]?["execution"]?["result"]?.GetValue<string>();
        Assert.NotNull(resHash);
        Assert.Matches("^[a-f0-9]{64}$", resHash);

        // 2. Empty file -> null
        var rowEmpty = new JsonObject { ["id"] = "alpha", ["resultPath"] = emptyPath };
        var homeEmpty = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowEmpty } }, options);
        Assert.Null(homeEmpty["current"]?["execution"]?["result"]);

        // 3. Oversize file -> null
        var rowOversize = new JsonObject { ["id"] = "alpha", ["resultPath"] = oversizePath };
        var homeOversize = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowOversize } }, options);
        Assert.Null(homeOversize["current"]?["execution"]?["result"]);

        // 4. Absent file -> null
        var rowAbsent = new JsonObject { ["id"] = "alpha", ["resultPath"] = Path.Combine(folder, "nonexistent.json") };
        var homeAbsent = await ProjectHome.GetProjectHomeAsync(project, new JsonObject { ["projects"] = new JsonArray { rowAbsent } }, options);
        Assert.Null(homeAbsent["current"]?["execution"]?["result"]);
    }

    [Fact]
    public async Task LocalGit_UntrackedLimits_Exact2000AndExact8MiB_Succeeds()
    {
        var repoDir = Path.Combine(tempDir, "exact-limits-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init", "-q");

        // Exactly 2000 untracked files
        var names = Enumerable.Range(1, 2000).Select(i => $"f{i}.txt").ToList();
        var fakeFiles = new FakeProjectHomeFiles();
        foreach (var n in names)
            fakeFiles.Files[Path.Combine(repoDir, n)] = [0x41]; // 1 byte each

        var gitAdapter = new FakeProjectHomeGit((f, args) =>
        {
            if (args.Contains("--show-toplevel")) return Task.FromResult<string?>(repoDir);
            if (args.Contains("--verify")) return Task.FromResult<string?>("1111111111111111111111111111111111111111");
            if (args.Contains("ls-files")) return Task.FromResult<string?>(string.Join("\0", names));
            if (args.Contains("status")) return Task.FromResult<string?>("?? f1.txt\n");
            if (args.Contains("diff")) return Task.FromResult<string?>("");
            return Task.FromResult<string?>("");
        });

        var options = new ProjectHomeOptions
        {
            Files = fakeFiles,
            GitAdapter = gitAdapter
        };

        var result = await ProjectHome.LocalGitAsync(repoDir, options);
        Assert.True(result["readable"]?.GetValue<bool>());
        Assert.NotNull(result["fingerprint"]); // Exactly 2000 files succeeds!
    }

    [Fact]
    public async Task ProjectHome_CheckpointDiff_HeadAndRevisionChanges()
    {
        var folder = Path.Combine(tempDir, "cp-diff-variations");
        Directory.CreateDirectory(folder);
        RunGitCommand(folder, "init", "-q");

        var project = Item(folder, "alpha");
        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-1", ["projectId"] = "alpha", ["status"] = "PAUSED" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "ST-1", ["megaTaskId"] = "MT-1", ["status"] = "COMPLETE" },
                new JsonObject { ["id"] = "ST-2", ["megaTaskId"] = "MT-1", ["status"] = "PENDING" }
            },
            ["checkpoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "CP-Diff",
                    ["projectId"] = "alpha",
                    ["megaTaskId"] = "MT-1",
                    ["planRevisionId"] = 1,
                    ["completedSubTaskIds"] = new JsonArray { "ST-1" },
                    ["repoState"] = new JsonObject
                    {
                        ["repoHead"] = "1111111111111111111111111111111111111111",
                        ["fingerprint"] = "fp-initial",
                        ["comparable"] = true,
                        ["dirtyFiles"] = new JsonArray()
                    }
                }
            }
        };

        var options = new ProjectHomeOptions
        {
            Inspect = f => new JsonObject
            {
                ["git"] = true,
                ["commitId"] = "2222222222222222222222222222222222222222",
                ["dirty"] = new JsonArray { " M dirty.txt" }
            }
        };

        var snapshot = new JsonObject
        {
            ["projects"] = new JsonArray { new JsonObject { ["id"] = "alpha", ["megaTaskId"] = "MT-1" } },
            ["execution"] = durable
        };

        var home = await ProjectHome.GetProjectHomeAsync(project, snapshot, options);
        var diffs = (JsonArray)home["checkpointDiffs"]!;
        Assert.Single(diffs);
        var diff = (JsonObject)diffs[0]!;
        Assert.Equal("CP-Diff", diff["checkpointId"]?.GetValue<string>());
        var changes = ((JsonArray)diff["changes"]!).Select(c => c?.GetValue<string>()).ToList();
        Assert.Contains(changes, c => c != null && c.StartsWith("HEAD changed from 11111111") && c.Contains("to 22222222"));
        Assert.Contains(changes, c => c != null && c.Contains("1 files currently changed locally"));
    }

    public sealed class FakeProjectHomeGit : IProjectHomeGit
    {
        public List<(string Folder, IReadOnlyList<string> Args)> Calls { get; } = new();
        public Func<string, IReadOnlyList<string>, Task<string?>>? Handler { get; set; }

        public FakeProjectHomeGit(Func<string, IReadOnlyList<string>, Task<string?>>? handler = null)
        {
            Handler = handler;
        }

        public Task<string?> RunGitAsync(string folder, IReadOnlyList<string> args)
        {
            Calls.Add((folder, args));
            if (Handler != null) return Handler(folder, args);
            return Task.FromResult<string?>("");
        }
    }

    public sealed class FakeProjectHomeFiles : IProjectHomeFiles
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Symlinks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RealPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> RealPathAsync(string path)
        {
            if (RealPaths.TryGetValue(path, out var real)) return Task.FromResult<string?>(real);
            return Task.FromResult<string?>(path);
        }

        public Task<ProjectHomeFileStat> LStatAsync(string path)
        {
            if (Symlinks.TryGetValue(path, out var target))
                return Task.FromResult(new ProjectHomeFileStat(Encoding.UTF8.GetByteCount(target), false, false, true));
            if (Files.TryGetValue(path, out var bytes))
                return Task.FromResult(new ProjectHomeFileStat(bytes.Length, true, false, false));
            throw new FileNotFoundException("File not found in fake", path);
        }

        public Task<ProjectHomeFileStat> StatAsync(string path)
        {
            if (Files.TryGetValue(path, out var bytes))
                return Task.FromResult(new ProjectHomeFileStat(bytes.Length, true, false, false));
            throw new FileNotFoundException("File not found in fake", path);
        }

        public Task<byte[]> ReadFileAsync(string path)
        {
            if (Files.TryGetValue(path, out var bytes)) return Task.FromResult(bytes);
            throw new FileNotFoundException("File not found in fake", path);
        }

        public Task<string> ReadLinkAsync(string path)
        {
            if (Symlinks.TryGetValue(path, out var target)) return Task.FromResult(target);
            throw new FileNotFoundException("Link not found in fake", path);
        }

        public Task<byte[]> ReadTailAsync(string path, int maxBytes)
        {
            if (Files.TryGetValue(path, out var bytes))
            {
                var take = Math.Min(bytes.Length, maxBytes);
                var result = new byte[take];
                Buffer.BlockCopy(bytes, bytes.Length - take, result, 0, take);
                return Task.FromResult(result);
            }
            throw new FileNotFoundException("File not found in fake", path);
        }
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
