using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class OrchestratorStateTests
{
    [Fact]
    public void EmptyEntry_ReturnsDefaultValues()
    {
        var entry = OrchestratorState.EmptyEntry();

        Assert.Equal("idle", entry.Status);
        Assert.Equal("", entry.Doing);
        Assert.Empty(entry.Plan);
        Assert.Null(entry.Facts);
        Assert.Null(entry.JobId);
        Assert.Null(entry.JobTitle);
        Assert.Null(entry.AttemptId);
        Assert.Null(entry.ModelId);
        Assert.False(entry.Approved);
        Assert.Equal("", entry.WaitingFor);
        Assert.Equal("", entry.Next);
        Assert.Null(entry.WorkerLabel);
        Assert.Null(entry.ResultPath);
        Assert.Null(entry.WatchingSince);
        Assert.False(entry.HoldApproval);
        Assert.False(entry.GrillSkipped);
        Assert.Empty(entry.Docs);
        Assert.Empty(entry.Log);
        Assert.Null(entry.PendingAutoStart);
        Assert.Null(entry.Checkpoint);
        Assert.Empty(entry.Dispatch);
        Assert.False(entry.ResumeRequested);
        Assert.False(entry.DeadlineHonored);
        Assert.Null(entry.WorkerPid);
        Assert.Null(entry.WorkerLog);
        Assert.False(entry.WorkerAlive);
        Assert.Empty(entry.Tried);
        Assert.Null(entry.ProgressAt);
        Assert.Null(entry.ProgressKey);
        Assert.Equal("", entry.WorkerTail);
        Assert.Equal("", entry.WorkerActivity);
    }

    [Fact]
    public void EmptyEntryObject_HasAllRequiredProperties()
    {
        var obj = OrchestratorState.EmptyEntryObject();

        Assert.Equal("idle", obj["status"]?.GetValue<string>());
        Assert.Equal("", obj["doing"]?.GetValue<string>());
        Assert.IsType<JsonArray>(obj["plan"]);
        Assert.Null(obj["facts"]);
        Assert.False(obj["approved"]?.GetValue<bool>());
        Assert.False(obj["workerAlive"]?.GetValue<bool>());
        Assert.IsType<JsonArray>(obj["dispatch"]);
        Assert.IsType<JsonArray>(obj["log"]);
        Assert.IsType<JsonArray>(obj["docs"]);
    }

    [Theory]
    [InlineData(null, false, "idle")]
    [InlineData("blocked_no_folder", false, "check")]
    [InlineData("propose_grill", false, "grill")]
    [InlineData("waiting_on_you", false, "grill")]
    [InlineData("idle", false, "idle")]
    [InlineData("idle", true, "plan")]
    [InlineData("needs_approval", false, "approve")]
    [InlineData("blocked_no_worker", false, "assign")]
    [InlineData("watching", false, "watch")]
    [InlineData("checkpointed", false, "watch")]
    [InlineData("failed", false, "watch")]
    [InlineData("complete", false, "done")]
    [InlineData("custom_status", false, "custom_status")]
    public void StepOf_MapsAllStatusesCorrectly(string? status, bool hasPendingAutoStart, string expected)
    {
        var entry = OrchestratorState.EmptyEntry() with
        {
            Status = status ?? "",
            PendingAutoStart = hasPendingAutoStart ? new PendingAutoStart() : null
        };
        var obj = OrchestratorState.EmptyEntryObject();
        if (status != null) obj["status"] = status;
        else obj.Remove("status");
        if (hasPendingAutoStart) obj["pendingAutoStart"] = new JsonObject();

        Assert.Equal(expected, OrchestratorState.StepOf(entry));
        Assert.Equal(expected, OrchestratorState.StepOf(obj));
    }

    [Fact]
    public void PipelineOf_GeneratesSixStagesAndCorrectStates()
    {
        // 1. Idle -> all todo
        var idlePipeline = OrchestratorState.PipelineOf(OrchestratorState.EmptyEntry());
        Assert.Equal(6, idlePipeline.Count);
        Assert.All(idlePipeline, p => Assert.Equal("todo", p.State));
        Assert.Equal(["check", "grill", "plan", "approve", "assign", "watch"], idlePipeline.Select(p => p.Id));

        // 2. Needs approval -> check, grill, plan are done; approve is now; assign, watch are todo
        var entry = OrchestratorState.EmptyEntry() with { Status = "needs_approval" };
        var approvePipeline = OrchestratorState.PipelineOf(entry);
        Assert.Equal("done", approvePipeline.First(p => p.Id == "check").State);
        Assert.Equal("done", approvePipeline.First(p => p.Id == "grill").State);
        Assert.Equal("done", approvePipeline.First(p => p.Id == "plan").State);
        Assert.Equal("now", approvePipeline.First(p => p.Id == "approve").State);
        Assert.Equal("todo", approvePipeline.First(p => p.Id == "assign").State);
        Assert.Equal("todo", approvePipeline.First(p => p.Id == "watch").State);

        // 3. Complete -> all done
        var doneEntry = OrchestratorState.EmptyEntry() with { Status = "complete" };
        var donePipeline = OrchestratorState.PipelineOf(doneEntry);
        Assert.All(donePipeline, p => Assert.Equal("done", p.State));

        // 4. Grill skipped -> grill is skip when step is past grill
        var skippedEntry = OrchestratorState.EmptyEntry() with { Status = "needs_approval", GrillSkipped = true };
        var skippedPipeline = OrchestratorState.PipelineOf(skippedEntry);
        Assert.Equal("skip", skippedPipeline.First(p => p.Id == "grill").State);
    }

    [Fact]
    public void OrchestratorDoing_HandlesWatchingAndNeedsApproval()
    {
        // Watching with files
        var watchingObj = OrchestratorState.EmptyEntryObject();
        watchingObj["status"] = "watching";
        watchingObj["facts"] = new JsonObject { ["fileCount"] = 14 };
        watchingObj["dispatch"] = new JsonArray
        {
            new JsonObject { ["modelId"] = "m1", ["alive"] = true },
            new JsonObject { ["modelId"] = "m2", ["alive"] = false }
        };
        var doing = OrchestratorState.OrchestratorDoing(watchingObj);
        Assert.Contains("Monitoring 2 workers (1 alive)", doing);
        Assert.Contains("14 files in the project folder", doing);

        // Needs approval multi slice
        var approvalObj = OrchestratorState.EmptyEntryObject();
        approvalObj["status"] = "needs_approval";
        approvalObj["dispatch"] = new JsonArray
        {
            new JsonObject { ["slice"] = "s1" },
            new JsonObject { ["slice"] = "s2" }
        };
        Assert.Equal("Split the job into 2 slices across available workers. Waiting for Approve.", OrchestratorState.OrchestratorDoing(approvalObj));

        // Checkpointed
        var cpObj = OrchestratorState.EmptyEntryObject();
        cpObj["status"] = "checkpointed";
        cpObj["checkpoint"] = new JsonObject { ["reason"] = "application update" };
        Assert.Equal("Saved unfinished work before updating.", OrchestratorState.OrchestratorDoing(cpObj));
    }

    [Fact]
    public void CrewOf_AssemblesLeadAndWorkers()
    {
        var row = OrchestratorState.EmptyEntryObject();
        row["status"] = "needs_approval";
        row["dispatch"] = new JsonArray
        {
            new JsonObject
            {
                ["modelId"] = "lead-m",
                ["label"] = "Lead M",
                ["slice"] = "review architecture",
                ["activitySession"] = new JsonObject { ["role"] = "reviewer" }
            },
            new JsonObject
            {
                ["modelId"] = "worker-m",
                ["label"] = "Worker M",
                ["slice"] = "build frontend"
            }
        };

        var crew = OrchestratorState.CrewOf(row, "Lead Astra");
        Assert.Equal(3, crew.Count);
        Assert.Equal("Orchestrator", crew[0].Role);
        Assert.Equal("Lead Astra", crew[0].Name);
        Assert.Equal("Reviewer", crew[1].Role);
        Assert.Equal("Lead M", crew[1].Name);
        Assert.Equal("Worker", crew[2].Role);
        Assert.Equal("Worker M", crew[2].Name);
    }

    [Fact]
    public void Store_LoadAndPersist_RoundtripsPreservingUnknownFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var stateFile = Path.Combine(tempDir, "orchestrator.json");
            var initialJson = """
            {
                "customUnknownField": "legacyValue123",
                "mode": "auto",
                "orchestratorId": "custom-orch",
                "projects": {
                    "proj-1": {
                        "status": "watching",
                        "customProjectField": "preserveMe"
                    }
                }
            }
            """;
            File.WriteAllText(stateFile, initialJson);

            var collaborators = new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T12:00:00.000Z",
                SessionId: "test-session-1"
            );
            var state = OrchestratorState.Load(tempDir, collaborators);

            Assert.Equal("auto", state.Store["mode"]?.GetValue<string>());
            Assert.Equal("custom-orch", state.Store["orchestratorId"]?.GetValue<string>());
            Assert.Equal("legacyValue123", state.Store["customUnknownField"]?.GetValue<string>());

            var entryRow = state.EntryRow("proj-1");
            Assert.Equal("watching", entryRow["status"]?.GetValue<string>());
            Assert.Equal("preserveMe", entryRow["customProjectField"]?.GetValue<string>());

            // Mutate and persist
            entryRow["workerTail"] = "finished step 1";
            state.Persist();

            // Re-read file directly
            var persistedJson = File.ReadAllText(stateFile);
            var reloaded = JsonNode.Parse(persistedJson) as JsonObject;
            Assert.NotNull(reloaded);
            Assert.Equal("legacyValue123", reloaded["customUnknownField"]?.GetValue<string>());
            Assert.Equal("preserveMe", reloaded["projects"]?["proj-1"]?["customProjectField"]?.GetValue<string>());
            Assert.Equal("finished step 1", reloaded["projects"]?["proj-1"]?["workerTail"]?.GetValue<string>());
            Assert.True(persistedJson.EndsWith('\n'), "Persisted JSON must end with newline");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void DurableBackingStore_RowFor_SharesLiveStateWithoutSecondCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var state = OrchestratorState.Load(tempDir);

            // Access via RowFor
            var row = state.RowFor("alpha");
            Assert.Equal("idle", row["status"]?.GetValue<string>());

            // Mutate row through RowFor (simulating Execution.cs modifying the live row)
            row["status"] = "watching";
            row["workerAlive"] = true;

            // Typed access through Entry("alpha") must see the changes immediately
            var typedEntry = state.Entry("alpha");
            Assert.Equal("watching", typedEntry.Status);
            Assert.True(typedEntry.WorkerAlive);

            // RowFor on research record
            state.Store["researches"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "r1",
                    ["megaTaskId"] = "mt-research",
                    ["status"] = "running"
                }
            };
            var researchRow = state.RowFor("alpha", "mt-research");
            Assert.Equal("r1", researchRow["id"]?.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Entry_ReturnsDetachedTypedProjection_WithKnownNestedState()
    {
        var state = OrchestratorState.Load(null);
        var row = state.EntryRow("typed");
        row["facts"] = new JsonObject
        {
            ["git"] = true,
            ["fileCount"] = 7,
            ["folder"] = "C:/project",
            ["recent"] = new JsonArray { "abc first commit" }
        };
        row["pendingAutoStart"] = new JsonObject
        {
            ["timing"] = "deadline",
            ["deadlineAt"] = "2026-11-27T10:00:00.000Z"
        };
        row["repository"] = new JsonObject { ["folder"] = "C:/project", ["github"] = "owner/repo" };
        row["stepModes"] = new JsonObject { ["sub-1"] = "ultra-safe" };
        row["savingNotes"] = new JsonObject { ["quota"] = true };
        row["authorizedSessionId"] = "session-1";

        var entry = state.Entry("typed");

        Assert.NotNull(entry.Facts);
        Assert.True(entry.Facts!.Git);
        Assert.Equal(7, entry.Facts.FileCount);
        Assert.Equal("C:/project", entry.Facts.Folder);
        Assert.NotNull(entry.PendingAutoStart);
        Assert.Equal("deadline", entry.PendingAutoStart!.Timing);
        Assert.Equal("owner/repo", entry.Repository!.Github);
        Assert.Equal("ultra-safe", entry.StepModes!["sub-1"]);
        Assert.True(entry.SavingNotes!["quota"]);
        Assert.Equal("session-1", entry.AuthorizedSessionId);

        row["facts"]!.AsObject()["fileCount"] = 99;
        Assert.Equal(7, entry.Facts.FileCount);
    }

    [Fact]
    public void Sessions_CollectsHistoricalAndInitializesCurrentSession()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var stateFile = Path.Combine(tempDir, "orchestrator.json");
            var initialJson = """
            {
                "asks": [
                    { "sessionId": "session-ask-1", "at": "2026-11-20T10:00:00.000Z" }
                ],
                "researches": [
                    { "sessionId": "session-res-1", "startedAt": "2026-11-21T10:00:00.000Z" }
                ],
                "projects": {
                    "p1": {
                        "dispatch": [
                            { "sessionId": "session-dispatch-1", "startedAt": "2026-11-22T10:00:00.000Z" }
                        ]
                    }
                },
                "sessions": [
                    { "id": "session-existing", "startedAt": "2026-11-19T10:00:00.000Z" }
                ]
            }
            """;
            File.WriteAllText(stateFile, initialJson);

            var collaborators = new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T20:00:00.000Z",
                SessionId: "session-current"
            );
            var state = OrchestratorState.Load(tempDir, collaborators);

            var sessionsArr = state.Store["sessions"] as JsonArray;
            Assert.NotNull(sessionsArr);

            var sessionIds = sessionsArr.OfType<JsonObject>().Select(s => s["id"]?.GetValue<string>()).ToList();
            Assert.Contains("session-existing", sessionIds);
            Assert.Contains("session-ask-1", sessionIds);
            Assert.Contains("session-res-1", sessionIds);
            Assert.Contains("session-dispatch-1", sessionIds);
            Assert.Equal("session-current", sessionIds.Last());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_UsesOneGeneratedSessionId_ForAuthorityAndRegistry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var state = OrchestratorState.Load(tempDir);
            var sessions = Assert.IsType<JsonArray>(state.Store["sessions"]);
            var current = Assert.IsType<JsonObject>(sessions[^1]);

            Assert.False(string.IsNullOrWhiteSpace(state.SessionId));
            Assert.Equal(state.SessionId, current["id"]?.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HydrateCheckpoints_HydratesFilesAndPaintsCheckpoint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        var checkpointsDir = Path.Combine(tempDir, "checkpoints");
        Directory.CreateDirectory(checkpointsDir);

        try
        {
            var cpJson = """
            {
                "jobTitle": "Finish migration task",
                "reason": "application update",
                "step": "watch"
            }
            """;
            File.WriteAllText(Path.Combine(checkpointsDir, "beta.json"), cpJson);

            var state = OrchestratorState.Load(tempDir);

            var betaRow = state.EntryRow("beta");
            Assert.Equal("checkpointed", betaRow["status"]?.GetValue<string>());
            Assert.Equal("Resume from checkpoint", betaRow["waitingFor"]?.GetValue<string>());
            Assert.False(betaRow["workerAlive"]?.GetValue<bool>());
            Assert.NotNull(betaRow["checkpoint"]);
            Assert.Equal(Path.Combine(checkpointsDir, "beta.json"), betaRow["checkpoint"]?["path"]?.GetValue<string>());
            Assert.Equal("Saved unfinished work before updating.", betaRow["orchestratorDoing"]?.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Persist_WithSimulatedSharingViolation_RetriesAndSucceeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var attempts = 0;
            var collaborators = new OrchestratorStateCollaborators(
                Rename: (src, dst) =>
                {
                    attempts++;
                    if (attempts < 3)
                    {
                        // Simulate Windows ERROR_SHARING_VIOLATION (HResult 0x80070020)
                        throw new IOException("Sharing violation", unchecked((int)0x80070020));
                    }
                    File.Move(src, dst, overwrite: true);
                }
            );

            var state = OrchestratorState.Load(tempDir, collaborators);
            state.EntryRow("proj-retry")["status"] = "needs_approval";
            state.Persist();

            Assert.True(attempts >= 3, $"Expected at least 3 attempts, actual: {attempts}");
            Assert.True(File.Exists(state.StateFile));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Persist_FailureDuringReplacement_RemovesTempFileAndRethrows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var stateFile = Path.Combine(tempDir, "orchestrator.json");
            File.WriteAllText(stateFile, "{\"durableEvidence\":\"before-replacement\"}\n");
            var collaborators = new OrchestratorStateCollaborators(
                Rename: (src, dst) => throw new UnauthorizedAccessException("Permanent access denial")
            );

            var state = OrchestratorState.Load(tempDir, collaborators);
            var originalBytes = File.ReadAllBytes(stateFile);
            state.EntryRow("proj-fail")["status"] = "watching";

            Assert.Throws<UnauthorizedAccessException>(() => state.Persist());
            Assert.Equal(originalBytes, File.ReadAllBytes(stateFile));

            // Temporary file should have been deleted by AtomicRename.RenameWithRetry
            var tempFiles = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(tempFiles);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HydrateCheckpoints_HonorsActiveOrApprovedStatuses()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        var checkpointsDir = Path.Combine(tempDir, "checkpoints");
        Directory.CreateDirectory(checkpointsDir);

        try
        {
            File.WriteAllText(Path.Combine(checkpointsDir, "active-proj.json"), """{"jobTitle":"Active Work","reason":"checkpoint"}""");
            File.WriteAllText(Path.Combine(checkpointsDir, "approved-proj.json"), """{"jobTitle":"Approved Work","reason":"checkpoint"}""");
            File.WriteAllText(Path.Combine(checkpointsDir, "resumed-proj.json"), """{"jobTitle":"Resumed Work","reason":"checkpoint"}""");

            var state = OrchestratorState.Load(tempDir);

            var activeRow = state.EntryRow("active-proj");
            activeRow["status"] = "watching";

            var approvedRow = state.EntryRow("approved-proj");
            approvedRow["status"] = "needs_approval";

            var resumedRow = state.EntryRow("resumed-proj");
            resumedRow["resumeRequested"] = true;
            resumedRow["status"] = "idle";

            state.HydrateCheckpoints();

            Assert.Equal("watching", activeRow["status"]?.GetValue<string>());
            Assert.Equal("needs_approval", approvedRow["status"]?.GetValue<string>());
            Assert.Equal("idle", resumedRow["status"]?.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_RestartHydratesPersistedCheckpoint_AndRotatesSessionAuthority()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        var checkpointsDir = Path.Combine(tempDir, "checkpoints");
        Directory.CreateDirectory(checkpointsDir);
        try
        {
            File.WriteAllText(Path.Combine(checkpointsDir, "restart.json"), """
            {
                "jobTitle": "Restart this work",
                "reason": "deadline",
                "historicalEvidence": "retain"
            }
            """);

            var first = OrchestratorState.Load(tempDir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:00:00.000Z", SessionId: "session-before"));
            Assert.Equal("checkpointed", first.EntryRow("restart")["status"]?.GetValue<string>());
            first.Persist();

            var restarted = OrchestratorState.Load(tempDir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T23:00:00.000Z", SessionId: "session-after"));
            var row = restarted.EntryRow("restart");

            Assert.Equal("session-after", restarted.SessionId);
            Assert.Equal("checkpointed", row["status"]?.GetValue<string>());
            Assert.Equal("Resume from checkpoint", row["waitingFor"]?.GetValue<string>());
            Assert.Equal("retain", row["checkpoint"]?["historicalEvidence"]?.GetValue<string>());
            Assert.Equal(Path.Combine(checkpointsDir, "restart.json"), row["checkpoint"]?["path"]?.GetValue<string>());

            var sessions = Assert.IsType<JsonArray>(restarted.Store["sessions"]);
            var sessionIds = sessions.OfType<JsonObject>().Select(item => item["id"]?.GetValue<string>()).ToList();
            Assert.Contains("session-before", sessionIds);
            Assert.Equal("session-after", sessionIds[^1]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HydrateCheckpoints_IgnoresMalformedPersistedCheckpointShape()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        var checkpointsDir = Path.Combine(tempDir, "checkpoints");
        Directory.CreateDirectory(checkpointsDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "orchestrator.json"), """
            {
                "projects": { "broken": { "checkpoint": 17, "status": "idle" } }
            }
            """);

            var state = OrchestratorState.Load(tempDir, new OrchestratorStateCollaborators(SessionId: "safe-session"));

            Assert.Equal("idle", state.EntryRow("broken")["status"]?.GetValue<string>());
            Assert.Equal(17, state.EntryRow("broken")["checkpoint"]?.GetValue<int>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_RepairsLegacyShapes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var stateFile = Path.Combine(tempDir, "orchestrator.json");
            var legacyJson = """
            {
                "grillPropose": true,
                "grillKind": "full",
                "mode": "invalid_mode_defaults_to_manual"
            }
            """;
            File.WriteAllText(stateFile, legacyJson);

            var state = OrchestratorState.Load(tempDir);

            // In actual JavaScript behavior, clarification grill stays off even if old file had it true
            Assert.False(state.Store["grillPropose"]?.GetValue<bool>());
            Assert.Equal("off", state.Store["grillKind"]?.GetValue<string>());
            Assert.Equal("manual", state.Store["mode"]?.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_RepairsWrongKnownTypes_WithoutDroppingUnknownEvidence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "orchestrator.json"), """
            {
                "mode": "invalid",
                "autonomy": {
                    "legacy": { "enabled": true, "sessionId": "old-session" }
                },
                "projects": {
                    "legacy": {
                        "customEvidence": { "source": "old-run" }
                    }
                },
                "researches": {},
                "sessions": "not-an-array",
                "customTopLevelEvidence": "keep-me"
            }
            """);

            var state = OrchestratorState.Load(tempDir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T21:00:00.000Z",
                SessionId: "session-repaired"));

            Assert.Equal("manual", state.Store["mode"]?.GetValue<string>());
            Assert.IsType<JsonObject>(state.Store["autonomy"]);
            Assert.False(state.Store["autonomy"]?["legacy"]?["enabled"]?.GetValue<bool>());
            Assert.IsType<JsonArray>(state.Store["researches"]);
            Assert.IsType<JsonArray>(state.Store["sessions"]);
            Assert.Equal("keep-me", state.Store["customTopLevelEvidence"]?.GetValue<string>());

            var row = state.EntryRow("legacy");
            Assert.Equal("idle", row["status"]?.GetValue<string>());
            Assert.IsType<JsonArray>(row["plan"]);
            Assert.Null(row["facts"]);
            Assert.IsType<JsonArray>(row["dispatch"]);
            Assert.Equal("old-run", row["customEvidence"]?["source"]?.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Persist_CanonicalBytes_NoBomAndEndsWithNewline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "planwharf-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var state = OrchestratorState.Load(tempDir);
            state.Persist();

            var bytes = File.ReadAllBytes(state.StateFile!);
            Assert.True(bytes.Length > 0);

            // No UTF-8 BOM (0xEF, 0xBB, 0xBF)
            if (bytes.Length >= 3)
            {
                Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "File must not contain UTF-8 BOM");
            }

            // Ends with \n (0x0A)
            Assert.Equal((byte)'\n', bytes[^1]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ProgressKey_ComputesDeterministicKey()
    {
        var facts = new JsonObject
        {
            ["fileCount"] = 42,
            ["names"] = new JsonArray { "src", "docs", "test" },
            ["dirty"] = new JsonArray { "modified.cs" }
        };
        var dispatch = new JsonArray
        {
            new JsonObject { ["logPath"] = "worker1.log" },
            new JsonObject { ["logPath"] = "worker2.log" }
        };

        var key = OrchestratorState.ProgressKey(facts, null, dispatch, file => $"{file}_stat");
        Assert.Equal("42|src,docs,test|1|worker1.log_stat;worker2.log_stat", key);
    }

    [Fact]
    public void DefaultInspect_ClipsNamesAndFailsClosedForUnlinkedFolders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "planwharf-state-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(folder, $"file-{i:D2}.txt"), "fixture");
            var facts = OrchestratorState.DefaultInspect(folder);
            Assert.Equal(folder, facts.Folder);
            Assert.Equal(30, facts.FileCount);
            Assert.False(OrchestratorState.DefaultInspect(Path.Combine(folder, "missing")).Git);
            Assert.Equal("folder not found", OrchestratorState.DefaultInspect(Path.Combine(folder, "missing")).Error);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public void Snapshot_DeeplyDetachesExecutionRowsAndNestedCollections()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T21:00:00.000Z",
                SessionId: "snapshot-session",
                IsProcessAlive: _ => true));
            var row = state.EntryRow("alpha");
            row["status"] = "watching";
            row["doing"] = "running";
            row["plan"] = new JsonArray("first");
            row["workerPid"] = 42;
            row["workerAlive"] = true;
            row["dispatch"] = new JsonArray(
                new JsonObject { ["modelId"] = "m1", ["label"] = "Worker", ["slice"] = "tests", ["pid"] = 42, ["attemptId"] = "attempt-1" });
            state.Store["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray(new JsonObject { ["id"] = "mega-1", ["projectId"] = "alpha" }),
                ["subTasks"] = new JsonArray(),
                ["results"] = new JsonArray()
            };
            var plan = new NotebookPlan([new NotebookProject("alpha", "Alpha", Goal: "Ship it")]);
            var snapshot = state.Snapshot(plan);
            var project = Assert.Single(snapshot.Projects);
            ((string[])project.Plan)[0] = "changed outside";
            snapshot.Execution!.MegaTasks![0]["changed"] = true;
            Assert.Equal("first", state.EntryRow("alpha")["plan"]![0]!.GetValue<string>());
            Assert.Null(state.Store["execution"]?["megaTasks"]?[0]?["changed"]);
            Assert.Equal("snapshot-session", snapshot.SessionId);
            Assert.Equal("running", project.ActivitySessions[0].ProcessState);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ActivitySessionPageAsync_PaginatesCurrentAndRejectsInvalidPages()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = OrchestratorState.Load(dir);
            var row = state.EntryRow("alpha");
            row["dispatch"] = new JsonArray(
                new JsonObject { ["attemptId"] = "a1", ["modelId"] = "m1", ["slice"] = "one" },
                new JsonObject { ["attemptId"] = "a2", ["modelId"] = "m1", ["slice"] = "two" },
                new JsonObject { ["attemptId"] = "a3", ["modelId"] = "m1", ["slice"] = "three" });
            var page = await state.ActivitySessionPageAsync("alpha", 1, 2);
            Assert.Equal(3, page.Total);
            Assert.Equal(1, page.Offset);
            Assert.Equal(2, page.Limit);
            Assert.Equal(["a2", "a3"], page.Sessions.Select(s => s.AttemptId!).ToArray());
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.ActivitySessionPageAsync("alpha", 0, 25));
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.ActivitySessionPageAsync("missing"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task InspectRepositoryAsync_UsesInjectedInspectorAndPersistsRepositoryView()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var expected = new RepositoryInfo("2026-11-26T21:00:00.000Z", "/fixture/repo", null,
                new JsonObject { ["git"] = true }, new JsonObject { ["accessible"] = true },
                new JsonObject { ["status"] = "in-sync" });
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                SessionId: "repo-session",
                InspectRepository: (folder, _, _) => Task.FromResult(expected)));
            state.EntryRow("alpha")["facts"] = new JsonObject { ["folder"] = "/fixture/repo" };
            var result = await state.InspectRepositoryAsync("alpha");
            Assert.True(result.Ok);
            Assert.Equal("in-sync", result.Repository!.Comparison!["status"]!.GetValue<string>());
            Assert.Equal("in-sync", state.EntryRow("alpha")["repository"]!["comparison"]!["status"]!.GetValue<string>());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Snapshot_DoesNotLeakDiagnosticOrLivenessAcrossIndependentProjects()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(IsProcessAlive: pid => pid == "11"));
            var first = state.EntryRow("one");
            first["status"] = "watching";
            first["dispatch"] = new JsonArray(new JsonObject
            {
                ["modelId"] = "m1", ["label"] = "One", ["slice"] = "run", ["pid"] = 11,
                ["resultPath"] = Path.Combine(dir, "one-result.json")
            });
            var second = state.EntryRow("two");
            second["status"] = "watching";
            second["dispatch"] = new JsonArray(new JsonObject
            {
                ["modelId"] = "m2", ["label"] = "Two", ["slice"] = "run", ["pid"] = 22,
                ["resultPath"] = Path.Combine(dir, "two-result.json")
            });
            File.WriteAllText(Path.Combine(dir, "one-result.json.diagnostic.json"), "{\"provider\":\"codex\"}");
            var snap = state.Snapshot(new NotebookPlan([
                new NotebookProject("one", "One"),
                new NotebookProject("two", "Two")]));
            Assert.True(snap.Projects[0].Dispatch[0].Alive);
            Assert.False(snap.Projects[1].Dispatch[0].Alive);
            Assert.Equal("codex", snap.Projects[0].Dispatch[0].Diagnostic!["provider"]!.GetValue<string>());
            Assert.Null(snap.Projects[1].Dispatch[0].Diagnostic);
            Assert.Empty(snap.Projects[1].ActivitySessions[0].Events);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
