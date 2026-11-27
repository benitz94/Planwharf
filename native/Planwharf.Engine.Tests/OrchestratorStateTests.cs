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
    public async Task Tick_RefreshesResearchAfterRestartWithoutChangingCompletedHistory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-research-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var collaborators = new OrchestratorStateCollaborators(IsProcessAlive: _ => false, ReadPlan: _ => Task.FromResult(new NotebookPlan([])));
            var state = OrchestratorState.Load(dir, collaborators);
            ((JsonArray)state.Store["researches"]!).Add(new JsonObject { ["id"] = "active", ["status"] = "watching", ["pid"] = 42 });
            ((JsonArray)state.Store["researches"]!).Add(new JsonObject { ["id"] = "old", ["status"] = "complete", ["reason"] = "historical" });
            state.Persist();
            var restored = OrchestratorState.Load(dir, collaborators);
            await restored.TickAsync();
            var again = OrchestratorState.Load(dir, collaborators);
            Assert.Equal("failed", again.Store["researches"]![0]!["status"]!.GetValue<string>());
            Assert.Equal("complete", again.Store["researches"]![1]!["status"]!.GetValue<string>());
            Assert.Equal("historical", again.Store["researches"]![1]!["reason"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FocusWaitsForTickAndPersistsAfterIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<NotebookPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new NotebookPlan([new("alpha", "Alpha"), new("beta", "Beta")]);
        var calls = 0;
        var state = OrchestratorState.Load(null, new OrchestratorStateCollaborators(ReadPlan: _ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(plan);
            entered.SetResult(); return release.Task;
        }));
        var tick = state.TickAsync("alpha");
        await entered.Task;
        var focus = state.SetFocusAsync("beta");
        Assert.False(focus.IsCompleted);
        release.SetResult(plan);
        await tick;
        Assert.True((await focus).Ok);
        Assert.Equal("beta", state.Store["selectedProjectId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Archive_RejectsActiveResearchAndEmptyProjectId()
    {
        var plan = new NotebookPlan([new("alpha", "Alpha")]);
        var state = OrchestratorState.Load(null, new OrchestratorStateCollaborators(ReadPlan: _ => Task.FromResult(plan)));
        ((JsonArray)state.Store["researches"]!).Add(new JsonObject { ["projectId"] = "alpha", ["status"] = "watching" });
        Assert.False((await state.ArchiveProjectAsync("", "remove")).Ok);
        Assert.False((await state.ArchiveProjectAsync("alpha", "remove")).Ok);
        Assert.Single((JsonArray)state.Store["researches"]!);
        Assert.Empty((JsonArray)state.Store["projectArchive"]!);
    }

    [Fact]
    public void Research_MissingReportFailsAfterSuccessfulExit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-missing-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result = Path.Combine(dir, "result.txt"); File.WriteAllText(result, "STATUS: COMPLETE\nEXIT: 0\n");
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators());
            var record = new JsonObject { ["status"] = "watching", ["resultPath"] = result, ["reportPath"] = Path.Combine(dir, "missing.md") };
            ((JsonArray)state.Store["researches"]!).Add(record);
            state.RefreshResearch();
            Assert.Equal("failed", record["status"]!.GetValue<string>());
            Assert.False(record["reportAvailable"]!.GetValue<bool>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ConcurrentFocusArchiveAndTick_KeepProjectStateSeparate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-concurrent-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plan = new NotebookPlan([new("alpha", "Alpha"), new("beta", "Beta")]);
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(ReadPlan: _ => Task.FromResult(plan)));
            state.EntryRow("alpha")["evidence"] = "alpha"; state.EntryRow("beta")["evidence"] = "beta";
            var focus = state.SetFocusAsync("beta");
            var archive = state.ArchiveProjectAsync("alpha", "remove");
            var tick = state.TickAsync("beta");
            await Task.WhenAll(focus, archive, tick);
            Assert.True((await focus).Ok); Assert.True((await archive).Ok);
            Assert.Equal("beta", state.Store["selectedProjectId"]!.GetValue<string>());
            Assert.Equal("alpha", state.Store["projectArchive"]![0]!["state"]!["project"]!["evidence"]!.GetValue<string>());
            Assert.Equal("beta", state.Store["projects"]!["beta"]!["evidence"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Research_V2UsesLatestResultAndAllowsEmptyReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-research-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var report = Path.Combine(dir, "report.md"); File.WriteAllText(report, "");
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators());
            state.Store["execution"] = new JsonObject { ["results"] = new JsonArray(
                new JsonObject { ["megaTaskId"] = "m", ["summary"] = "old" },
                new JsonObject { ["megaTaskId"] = "m", ["summary"] = "new", ["reportPath"] = report }) };
            var record = new JsonObject { ["executionVersion"] = 2, ["megaTaskId"] = "m", ["status"] = "complete",
                ["dispatch"] = new JsonArray(new JsonObject { ["pid"] = 4, ["alive"] = true }) };
            ((JsonArray)state.Store["researches"]!).Add(record);
            state.RefreshResearch();
            Assert.True(record["alive"]!.GetValue<bool>()); Assert.True(record["reportAvailable"]!.GetValue<bool>());
            Assert.Equal("new", record["activity"]!.GetValue<string>()); Assert.Equal("complete", record["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archive_RetainsHistoryAndOtherProjectsAcrossRestart(bool keepTeam)
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var collaborators = new OrchestratorStateCollaborators(ReadPlan: _ => Task.FromResult(new NotebookPlan([new("alpha", "Alpha"), new("beta", "Beta")])));
            var state = OrchestratorState.Load(dir, collaborators);
            state.EntryRow("alpha")["evidence"] = "kept"; state.EntryRow("beta")["evidence"] = "other";
            state.Store["projectTeams"] = new JsonObject { ["alpha"] = new JsonObject { ["orchestratorId"] = "lead", ["roles"] = new JsonObject() } };
            state.Store["projectTools"] = new JsonObject { ["alpha"] = new JsonObject { ["tool"] = true } };
            ((JsonArray)state.Store["asks"]!).Add(new JsonObject { ["projectId"] = "alpha" });
            ((JsonArray)state.Store["asks"]!).Add(new JsonObject { ["projectId"] = "beta" });
            state.Store["execution"] = new JsonObject();
            var execution = (JsonObject)state.Store["execution"]!;
            execution["megaTasks"] = new JsonArray(); execution["subTasks"] = new JsonArray();
            ((JsonArray)execution["megaTasks"]!).Add(new JsonObject { ["id"] = "m1", ["projectId"] = "alpha" });
            ((JsonArray)execution["subTasks"]!).Add(new JsonObject { ["megaTaskId"] = "m1", ["id"] = "s1" });
            Assert.True((await state.ArchiveProjectAsync("alpha", "restart", new ArchiveProjectOptions(keepTeam))).Ok);
            var restored = OrchestratorState.Load(dir, collaborators);
            var saved = restored.Store["projectArchive"]![0]!["state"]!;
            Assert.Equal("kept", saved["project"]!["evidence"]!.GetValue<string>());
            Assert.Single((JsonArray)saved["execution"]!["subTasks"]!);
            Assert.Single((JsonArray)restored.Store["asks"]!);
            Assert.Equal("other", restored.Store["projects"]!["beta"]!["evidence"]!.GetValue<string>());
            Assert.Equal(keepTeam, restored.Store["projectTeams"]!["alpha"] is not null);
            Assert.Equal(keepTeam, restored.Store["projectTools"]!["alpha"] is not null);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("STATUS: COMPLETE\nEXIT: 0\n", "report", true, "complete")]
    [InlineData("STATUS: COMPLETE\nEXIT: 0\n", "", true, "failed")]
    [InlineData("STATUS: COMPLETE\nEXIT: 1\n", "report", true, "watching")]
    [InlineData("STATUS: FAILED\n", "report", true, "failed")]
    [InlineData("STATUS: WATCHING\n", "report", false, "failed")]
    public void Research_RequiresExitAndReportEvidence(string result, string report, bool alive, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-research-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var resultPath = Path.Combine(dir, "result.txt");
            var reportPath = Path.Combine(dir, "report.md");
            File.WriteAllText(resultPath, result); File.WriteAllText(reportPath, report);
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(IsProcessAlive: _ => alive));
            var record = new JsonObject { ["status"] = "watching", ["pid"] = 42, ["resultPath"] = resultPath, ["reportPath"] = reportPath };
            ((JsonArray)state.Store["researches"]!).Add(record);
            state.RefreshResearch();
            Assert.Equal(expected, record["status"]!.GetValue<string>());
            var reason = record["reason"]?.GetValue<string>();
            state.RefreshResearch();
            Assert.Equal(expected, record["status"]!.GetValue<string>());
            Assert.Equal(reason, record["reason"]?.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Focus_UsesNotebookAndPersistsAcrossRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var collaborators = new OrchestratorStateCollaborators(ReadPlan: _ =>
                Task.FromResult(new NotebookPlan([new("alpha", "Alpha")])));
            var state = OrchestratorState.Load(dir, collaborators);
            Assert.True((await state.SetFocusAsync("alpha")).Ok);
            state.EntryRow("stale");
            Assert.False((await state.SetFocusAsync("stale")).Ok);
            Assert.Equal("alpha", OrchestratorState.Load(dir, collaborators).Store["selectedProjectId"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class TickFiles : IExecutionFiles
    {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);
        public byte[] Read(string path) => _content.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path);
        public void Write(string path, byte[] bytes) => _content[path] = bytes;
        public void Append(string path, byte[] bytes) => _content[path] = _content.GetValueOrDefault(path, []).Concat(bytes).ToArray();
        public void CreateDirectory(string path) { }
        public string? LinkedWorkspace(string? path) => string.IsNullOrEmpty(path) ? null : path;
        public string RealDirectory(string path) => path;
        public ExecutionFileStat Stat(string path) => new(Read(path).Length, true, false);
        public void Json(string path, JsonObject value) => Write(path, System.Text.Encoding.UTF8.GetBytes(value.ToJsonString()));
    }

    private sealed class TickFixture : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "planwharf-tick-bridge-" + Guid.NewGuid().ToString("N"));
        public readonly TickFiles Files = new();
        public readonly HashSet<int> Alive = [];
        public readonly List<JsonObject> Launches = [];
        public readonly List<CompletedNotebookTask> Completed = [];
        public AttemptEngine Attempts { get; }
        public OrchestratorState State { get; private set; } = null!;
        public ExecutionBridge Bridge { get; private set; } = null!;
        public NotebookPlan Plan;
        public bool Shutdown { get; set; }
        private int _ids;
        private readonly JsonObject _model = JsonNode.Parse("""{"id":"fixture-model","label":"Fixture","provider":"codex","cli":"fixture-cli","enabled":true,"available":true}""")!.AsObject();

        public TickFixture()
        {
            Directory.CreateDirectory(Dir);
            Attempts = AttemptEngine.Create(Dir);
            Plan = new NotebookPlan([new("alpha", "Alpha", Folder: Dir, Tasks:
                [new("job", "Build", "Fixture work", "running", ExecutionVersion: 2, Raw: new JsonObject { ["revision_id"] = 1 })]),
                new("beta", "Beta", Folder: Dir)]);
            Open("first-session");
        }

        public void Open(string session)
        {
            State = OrchestratorState.Load(Dir, new(
                SessionId: session, Now: () => "2026-11-26T21:00:00.000Z",
                ReadPlan: _ => Task.FromResult(Plan), Bridge: () => Bridge,
                Attempts: Attempts, IsProcessAlive: pid => Alive.Contains(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture)),
                IsShuttingDown: () => Shutdown,
                DispatchParts: (_, _) => [new JsonObject { ["modelId"] = "fixture-model", ["slice"] = "Build fixture", ["scope"] = new JsonArray("src") }],
                CompleteTasks: (completed, _) =>
                {
                    Completed.AddRange(completed);
                    Plan = Plan with { Projects = Plan.Projects.Select(p => p with
                    { Tasks = (p.Tasks ?? []).Select(t => completed.Any(c => c.ProjectId == p.Id && c.TaskId == t.Id) ? t with { Status = "complete" } : t).ToArray() }).ToArray() };
                    return Task.FromResult(Plan);
                }));
            Bridge = Execution.CreateExecutionBridge(new ExecutionOptions
            {
                Store = State.Store, Attempts = Attempts, Directory = Dir, Files = Files,
                RowFor = State.RowForDelegate, Models = () => [_model],
                Inspect = _ => new JsonObject { ["git"] = false, ["dirty"] = new JsonArray() },
                Now = () => "2026-11-26T21:00:00.000Z", SessionId = session, LeadId = () => "fixture-lead",
                NewId = () => (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture), Persist = State.Persist,
                IsProcessAlive = Alive.Contains, SafeGitWorkspace = _ => false,
                Git = _ => new Worktrees.GitReply(1),
                SpawnWorker = request =>
                {
                    Launches.Add((JsonObject)request.DeepClone());
                    var pid = 1000 + Launches.Count; Alive.Add(pid);
                    return new JsonObject { ["pid"] = pid };
                }
            });
        }

        public JsonObject Mega => Bridge.Data.MegaTasks.OfType<JsonObject>().First(m => m["projectId"]!.GetValue<string>() == "alpha");
        public JsonObject Sub => Bridge.Data.SubTasks.OfType<JsonObject>().First(s => JsonNode.DeepEquals(s["megaTaskId"], Mega["id"]));
        public JsonObject Part => State.EntryRow("alpha")["dispatch"]!.AsArray().OfType<JsonObject>().First();

        public void Evidence(string status = "COMPLETE", int exitCode = 0, bool writeExit = true)
        {
            var part = Part;
            Files.Json(part["structuredResultPath"]!.GetValue<string>(), new JsonObject
            {
                ["resultId"] = "R-" + part["attemptId"]!.GetValue<string>(), ["attemptId"] = part["attemptId"]!.DeepClone(),
                ["subTaskId"] = Sub["id"]!.DeepClone(), ["megaTaskId"] = Mega["id"]!.DeepClone(),
                ["status"] = status, ["summary"] = "Fixture evidence", ["filesChanged"] = new JsonArray(),
                ["tests"] = new JsonArray(), ["blockers"] = new JsonArray(), ["nextAction"] = "Review",
                ["future"] = new JsonObject { ["preserve"] = true }
            });
            if (writeExit) Files.Json(part["exitPath"]!.GetValue<string>(), new JsonObject
            {
                ["attemptId"] = part["attemptId"]!.DeepClone(), ["provider"] = "codex", ["model"] = "fixture-cli", ["exitCode"] = exitCode
            });
        }

        public async Task Start()
        {
            await State.TickAsync("alpha");
            Bridge.Approve("alpha", Mega["id"]!.GetValue<string>());
            await State.TickAsync("alpha");
        }

        public void Dispose() => Directory.Delete(Dir, true);
    }

    [Fact]
    public async Task Tick_BridgeTransitionsFromApprovalToWatchingAndCollectsOnceAcrossRestart()
    {
        using var fixture = new TickFixture();
        var first = await fixture.State.TickAsync("alpha");
        Assert.Equal("needs_approval", first.Projects.Single(p => p.Id == "alpha").Status);
        Assert.Empty(fixture.Launches);
        Assert.Same(fixture.State.EntryRow("alpha"), fixture.Bridge.Options.RowFor("alpha", fixture.Mega["id"]!.GetValue<string>()));
        fixture.Bridge.Approve("alpha", fixture.Mega["id"]!.GetValue<string>());
        await fixture.State.TickAsync("alpha");
        Assert.Equal("watching", fixture.State.Entry("alpha").Status);
        Assert.Single(fixture.Launches);
        Assert.Equal("codex", fixture.Launches[0]["provider"]!.GetValue<string>());
        Assert.Equal("fixture-cli", fixture.Launches[0]["cli"]!.GetValue<string>());
        fixture.Evidence();
        await fixture.State.TickAsync("alpha");
        Assert.Empty(fixture.Bridge.Data.Results); // A live process is not collected early.
        fixture.Open("restarted-session");
        await fixture.State.TickAsync("alpha");
        Assert.Equal("watching", fixture.State.Entry("alpha").Status);
        Assert.Single(fixture.Launches);
        fixture.Alive.Clear();
        var completed = await fixture.State.TickAsync("alpha");
        Assert.Equal("complete", completed.Projects.Single(p => p.Id == "alpha").Status);
        Assert.Single(fixture.Bridge.Data.Results);
        Assert.Single(fixture.Completed);
        Assert.Equal("closed", fixture.Attempts.GetAttempts().Single()["status"]!.GetValue<string>());
        var savedResult = fixture.Bridge.Data.Results.ToJsonString();
        fixture.Open("third-session");
        await fixture.State.TickAsync("alpha");
        await fixture.State.TickAsync("alpha");
        Assert.Single(fixture.Completed);
        Assert.Single(fixture.Launches);
        Assert.Equal(savedResult, fixture.Bridge.Data.Results.ToJsonString());
        Assert.True(fixture.Bridge.Data.Results[0]!["future"]!["preserve"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("COMPLETE", 1, true, "checkpointed")]
    [InlineData("COMPLETE", 0, false, "checkpointed")]
    [InlineData("FAILED", 1, true, "failed")]
    [InlineData("BLOCKED", 0, true, "checkpointed")]
    public async Task Tick_DeadBridgeAttemptRequiresStructuredIdentityAndSuccessfulExit(string status, int exitCode, bool exit, string expected)
    {
        using var fixture = new TickFixture();
        await fixture.Start();
        fixture.Evidence(status, exitCode, exit);
        fixture.Alive.Clear();
        fixture.Open("restart");
        await fixture.State.TickAsync("alpha");
        Assert.Equal(expected, fixture.State.Entry("alpha").Status);
        Assert.Empty(fixture.Completed);
        Assert.Single(fixture.Launches);
        var results = fixture.Bridge.Data.Results.ToJsonString();
        var checkpoints = fixture.Bridge.Data.Checkpoints.ToJsonString();
        await fixture.State.TickAsync("alpha");
        Assert.Equal(results, fixture.Bridge.Data.Results.ToJsonString());
        Assert.Equal(checkpoints, fixture.Bridge.Data.Checkpoints.ToJsonString());
        Assert.Single(fixture.Launches);
    }

    [Fact]
    public async Task Tick_OrphanedBridgeSubtaskFailsWithoutReplayingHistoricalResults()
    {
        using var fixture = new TickFixture();
        await fixture.Start();
        fixture.State.EntryRow("alpha")["dispatch"] = new JsonArray();
        fixture.State.Persist();
        fixture.Open("restart");
        await fixture.State.TickAsync("alpha");
        Assert.Equal("FAILED", fixture.Sub["status"]!.GetValue<string>());
        Assert.Equal("failed", fixture.State.Entry("alpha").Status);
        Assert.Single(fixture.Launches);
        Assert.Empty(fixture.Bridge.Data.Results);
        Assert.Empty(fixture.Completed);
    }

    [Fact]
    public async Task Tick_ShutdownObservesFinishedBridgeAttemptsWithoutSchedulingPendingWork()
    {
        using var fixture = new TickFixture();
        await fixture.Start();
        fixture.Evidence();
        fixture.Alive.Clear();
        fixture.Shutdown = true;
        await fixture.State.TickAsync("alpha");
        Assert.Single(fixture.Bridge.Data.Results);
        Assert.Single(fixture.Launches);
        Assert.Equal("complete", fixture.State.Entry("alpha").Status);
        // Completion was first observed by the snapshot, after the notebook completion pass.
        await fixture.State.TickAsync("alpha");
        Assert.Single(fixture.Completed);
    }

    [Fact]
    public async Task Tick_TwoProjectsKeepBridgeRowsAttemptsAndCompletionIndependent()
    {
        using var fixture = new TickFixture();
        var alpha = fixture.Plan.Projects[0];
        fixture.Plan = new NotebookPlan([alpha, new("beta", "Beta", Folder: fixture.Dir, Tasks: alpha.Tasks)]);
        await fixture.State.TickAsync();
        foreach (var mega in fixture.Bridge.Data.MegaTasks.OfType<JsonObject>())
            fixture.Bridge.Approve(mega["projectId"]!.GetValue<string>(), mega["id"]!.GetValue<string>());
        await Task.WhenAll(fixture.State.TickAsync("alpha"), fixture.State.TickAsync("beta"));
        Assert.Equal(2, fixture.Launches.Count);
        Assert.Equal(2, fixture.Attempts.GetAttempts().Count);
        Assert.NotEqual(fixture.State.Entry("alpha").AttemptId, fixture.State.Entry("beta").AttemptId);
        var beta = fixture.State.EntryRow("beta").DeepClone();
        fixture.Evidence();
        fixture.Alive.Remove(fixture.Part["pid"]!.GetValue<int>());
        await fixture.State.TickAsync("alpha");
        Assert.Single(fixture.Completed);
        Assert.Equal("alpha", fixture.Completed[0].ProjectId);
        Assert.True(JsonNode.DeepEquals(beta, fixture.State.EntryRow("beta")));
        Assert.Equal("watching", fixture.State.Entry("beta").Status);
        Assert.Single(fixture.Bridge.Data.Results);
    }

    [Fact]
    public async Task Tick_PhaseTwoResearchUsesTheExactDurableResearchRow()
    {
        using var fixture = new TickFixture();
        fixture.Plan = new NotebookPlan([fixture.Plan.Projects[0] with { Tasks = [] }]);
        var job = JsonNode.Parse("""{"id":"research-job","title":"Research","detail":"Fixture research","kind":"research","detached":true,"revision_id":1}""")!.AsObject();
        var record = new JsonObject { ["id"] = "research-record", ["projectId"] = "alpha", ["megaTaskId"] = "MT-1", ["executionVersion"] = 2, ["job"] = job };
        fixture.State.Store["researches"]!.AsArray().Add(record);
        var item = new JsonObject { ["project"] = new JsonObject { ["id"] = "alpha", ["folder"] = fixture.Dir }, ["revisions"] = new JsonArray() };
        fixture.Bridge.Propose(item, job, [new JsonObject { ["modelId"] = "fixture-model", ["slice"] = "Research", ["type"] = "RESEARCH" }]);
        Assert.Same(record, fixture.Bridge.Options.RowFor("alpha", "MT-1"));
        await fixture.State.TickAsync("alpha");
        Assert.Equal("needs_approval", record["status"]!.GetValue<string>());
        Assert.Equal("idle", fixture.State.Entry("alpha").Status);
        Assert.Null(fixture.State.EntryRow("alpha")["megaTaskId"]);
        Assert.Empty(fixture.Launches);
        fixture.Open("research-restart");
        var saved = fixture.State.Store["researches"]!.AsArray()[0]!.AsObject();
        Assert.Same(saved, fixture.Bridge.Options.RowFor("alpha", "MT-1"));
        await fixture.State.TickAsync("alpha");
        Assert.Equal("needs_approval", saved["status"]!.GetValue<string>());
        Assert.Null(fixture.State.EntryRow("alpha")["megaTaskId"]);
    }

    [Theory]
    [InlineData(true, "STATUS: WATCHING\n", "watching", "active")]
    [InlineData(false, "STATUS: COMPLETE\nEXIT: 0\n", "complete", "closed")]
    [InlineData(false, "STATUS: COMPLETE\n", "failed", "closed")]
    [InlineData(false, "STATUS: COMPLETE\nEXIT: 1\n", "failed", "closed")]
    public async Task Tick_ColdLegacyWorkersNeedSuccessfulExitAndPreserveEvidence(bool alive, string result, string expected, string claimStatus)
    {
        using var fixture = new TickFixture();
        var task = fixture.Plan.Projects[0].Tasks![0] with { ExecutionVersion = 1 };
        fixture.Plan = new NotebookPlan([fixture.Plan.Projects[0] with { Tasks = [task] }]);
        var claim = fixture.Attempts.Claim(new JsonObject { ["projectId"] = "alpha", ["taskId"] = task.Id, ["modelId"] = "fixture-model",
            ["candidates"] = new JsonArray(new JsonObject { ["id"] = "fixture-model", ["enabled"] = true }) });
        Assert.True(claim["ok"]!.GetValue<bool>());
        var attemptId = claim["attempt"]!["id"]!.GetValue<string>();
        var row = fixture.State.EntryRow("alpha");
        row["status"] = "watching"; row["jobId"] = task.Id;
        row["jobFingerprint"] = OrchestratorState.JobFingerprint(task); row["attemptId"] = attemptId;
        row["approved"] = true; row["workerPid"] = 77;
        row["dispatch"] = new JsonArray(new JsonObject { ["pid"] = 77, ["resultPath"] = "legacy-result" });
        row["log"] = new JsonArray("Historical evidence");
        fixture.Files.Write("legacy-result", System.Text.Encoding.UTF8.GetBytes(result));
        fixture.State.Persist();
        if (alive) fixture.Alive.Add(77);
        var state = OrchestratorState.Load(fixture.Dir, new(
            ReadPlan: _ => Task.FromResult(fixture.Plan), Attempts: fixture.Attempts,
            IsProcessAlive: pid => alive && pid == "77", ReadResult: path => System.Text.Encoding.UTF8.GetString(fixture.Files.Read(path))));
        await state.TickAsync("alpha");
        Assert.Equal(expected, state.Entry("alpha").Status);
        Assert.Equal(claimStatus, fixture.Attempts.GetResult(attemptId)!["status"]!.GetValue<string>());
        Assert.Equal("Historical evidence", state.EntryRow("alpha")["log"]![0]!.GetValue<string>());
        Assert.Empty(fixture.Launches);
    }

    [Fact]
    public async Task Tick_NoRunningJobClosesOnlyItsProjectOrphanAndBecomesIdle()
    {
        using var fixture = new TickFixture();
        foreach (var id in new[] { "alpha", "beta" })
            Assert.True(fixture.Attempts.Claim(new JsonObject { ["projectId"] = id, ["taskId"] = "orphan-" + id, ["modelId"] = "fixture-model",
                ["candidates"] = new JsonArray(new JsonObject { ["id"] = "fixture-model", ["enabled"] = true }) })["ok"]!.GetValue<bool>());
        fixture.Plan = new NotebookPlan([new("alpha", "Alpha"), new("beta", "Beta")]);
        await fixture.State.TickAsync("alpha");
        Assert.Equal("idle", fixture.State.Entry("alpha").Status);
        Assert.Null(fixture.Attempts.GetActiveAttempt(new JsonObject { ["projectId"] = "alpha" }));
        Assert.NotNull(fixture.Attempts.GetActiveAttempt(new JsonObject { ["projectId"] = "beta" }));
        Assert.Equal("no running job", fixture.Attempts.GetAttempts().First()["closedReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tick_SavedCheckpointStaysPausedAcrossRepeatedTicksAndNewSession()
    {
        using var fixture = new TickFixture();
        var task = fixture.Plan.Projects[0].Tasks![0] with { ExecutionVersion = 1 };
        fixture.Plan = new NotebookPlan([fixture.Plan.Projects[0] with { Tasks = [task] }]);
        var folder = Path.Combine(fixture.Dir, "checkpoints");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "alpha.json");
        var saved = new JsonObject { ["jobId"] = task.Id, ["jobTitle"] = task.Title, ["detail"] = task.Detail,
            ["jobFingerprint"] = OrchestratorState.JobFingerprint(task), ["reason"] = "shutdown", ["future"] = "preserved" }.ToJsonString();
        File.WriteAllText(file, saved);
        fixture.Open("restart");
        for (var i = 0; i < 3; i++)
        {
            var snapshot = await fixture.State.TickAsync("alpha");
            Assert.Equal("checkpointed", snapshot.Projects.Single().Status);
            Assert.True(snapshot.Projects.Single().CanResume);
            Assert.Equal(saved, File.ReadAllText(file));
            Assert.Equal("preserved", fixture.State.EntryRow("alpha")["checkpoint"]!["future"]!.GetValue<string>());
        }
        Assert.Empty(fixture.Launches);
    }

    [Fact]
    public async Task Tick_CancellingQueuedProjectDoesNotCancelOtherProjectOrDispatchItsDecision()
    {
        var read = new TaskCompletionSource<NotebookPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var projects = new List<string>();
        var state = OrchestratorState.Load(null, new(ReadPlan: _ => read.Task,
            Decide: context => projects.Add(context.Project.Id)));
        var alpha = state.TickAsync("alpha");
        using var cancellation = new CancellationTokenSource();
        var beta = state.TickAsync("beta", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beta);
        Assert.False(alpha.IsCompleted);
        read.SetResult(new NotebookPlan([new("alpha", "Alpha"), new("beta", "Beta")]));
        await alpha;
        Assert.Equal(["alpha"], projects);
        await state.TickAsync("beta");
        Assert.Equal(["alpha", "beta"], projects);
    }

    [Fact]
    public async Task Tick_ReadFailureDoesNotPersistOrDispatchAndCanBeRetried()
    {
        using var fixture = new TickFixture();
        fixture.State.Persist();
        var file = Path.Combine(fixture.Dir, "orchestrator.json");
        var original = File.ReadAllText(file);
        var fail = true;
        var state = OrchestratorState.Load(fixture.Dir, new(ReadPlan: _ => fail
            ? Task.FromException<NotebookPlan>(new IOException("Unreadable notebook")) : Task.FromResult(new NotebookPlan([])),
            Decide: _ => throw new Exception("No decisions without notebook")));
        await Assert.ThrowsAsync<IOException>(() => state.TickAsync());
        Assert.Equal(original, File.ReadAllText(file));
        fail = false;
        Assert.Empty((await state.TickAsync()).Projects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tick_RejectsBridgeWithCopiedStoreOrRowBeforeDispatch(bool copyRow)
    {
        using var fixture = new TickFixture();
        ExecutionBridge? bridge = null;
        var state = OrchestratorState.Load(null, new(ReadPlan: _ => Task.FromResult(fixture.Plan), Bridge: () => bridge));
        bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = copyRow ? state.Store : (JsonObject)state.Store.DeepClone(), Attempts = fixture.Attempts, Directory = fixture.Dir,
            RowFor = (_, _) => new JsonObject(), Inspect = _ => throw new Exception("No inspection"), Models = () => [],
            Now = () => "2026-11-26T21:00:00Z", LeadId = () => null, Persist = () => throw new Exception("No persistence")
        });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => state.TickAsync("alpha"));
        Assert.Equal(copyRow ? nameof(ExecutionOptions.RowFor) : nameof(ExecutionOptions.Store), error.ParamName);
        Assert.Empty(bridge.Data.MegaTasks);
    }

    [Fact]
    public void JobFingerprint_MatchesActualJavaScriptForNumericRevisionAndGoldModel()
    {
        Assert.Equal("1a99a70d68fa63012296f188ff501cce2a5fbef13b6c3b677de9b6bb0b3f15f6",
            OrchestratorState.JobFingerprint(new("job", "Build", "Fixture work", Raw: new JsonObject { ["revision_id"] = 1 })));
        Assert.Equal("1c5d6cda39514a154073ad8bb0007769d4f0d2c8cc03cd0d32e9fd824bc0390e",
            OrchestratorState.JobFingerprint(new("job", "é < +", "a\nb", Raw: JsonNode.Parse("""{"revision_id":0,"goldModel":{"id":"g","provider":"codex","cli":"fixture"}}""")!.AsObject())));
    }

    [Fact]
    public async Task Tick_RestartCannotDispatchSavedAutomaticApprovalWithoutSessionAuthority()
    {
        using var fixture = new TickFixture();
        var task = fixture.Plan.Projects[0].Tasks![0] with { ExecutionVersion = 1 };
        var row = fixture.State.EntryRow("alpha");
        row["status"] = "watching"; row["approved"] = true; row["dispatch"] = new JsonArray();
        row["jobId"] = task.Id; row["jobFingerprint"] = OrchestratorState.JobFingerprint(task);
        row["authorizedSessionId"] = "old-session"; fixture.State.Store["mode"] = "auto";
        fixture.State.Persist();
        var state = OrchestratorState.Load(fixture.Dir, new(SessionId: "new-session", EnforceSessionBoundary: true,
            ReadPlan: _ => Task.FromResult(new NotebookPlan([fixture.Plan.Projects[0] with { Tasks = [task] }]))));
        await state.TickAsync("alpha");
        await state.TickAsync("alpha");
        Assert.Equal("needs_approval", state.Entry("alpha").Status);
        Assert.False(state.Entry("alpha").Approved);
        Assert.True(state.Entry("alpha").HoldApproval);
        Assert.Empty(fixture.Launches);
    }

    [Fact]
    public async Task Tick_HistoricalCompleteLabelPreservesEvidenceButCannotCloseClaimWithoutFiles()
    {
        using var fixture = new TickFixture();
        var task = fixture.Plan.Projects[0].Tasks![0] with { ExecutionVersion = 1, Status = "complete" };
        fixture.Plan = new NotebookPlan([fixture.Plan.Projects[0] with { Tasks = [task] }]);
        var claim = fixture.Attempts.Claim(new JsonObject { ["projectId"] = "alpha", ["taskId"] = task.Id, ["modelId"] = "fixture-model",
            ["candidates"] = new JsonArray(new JsonObject { ["id"] = "fixture-model", ["enabled"] = true }) });
        Assert.True(claim["ok"]!.GetValue<bool>());
        var row = fixture.State.EntryRow("alpha");
        row["status"] = "complete"; row["jobId"] = task.Id; row["jobFingerprint"] = OrchestratorState.JobFingerprint(task);
        row["attemptId"] = claim["attempt"]!["id"]!.DeepClone(); row["doing"] = "Saved completion";
        row["log"] = new JsonArray("Original raw evidence"); row["future"] = new JsonObject { ["keep"] = 42 };
        await fixture.State.TickAsync("alpha");
        await fixture.State.TickAsync("alpha");
        Assert.NotNull(fixture.Attempts.GetActiveAttempt(new JsonObject { ["projectId"] = "alpha" }));
        Assert.Equal("Saved completion", fixture.State.Entry("alpha").Doing);
        Assert.Equal("Original raw evidence", row["log"]![0]!.GetValue<string>());
        Assert.Equal(42, row["future"]!["keep"]!.GetValue<int>());
        Assert.Empty(fixture.Completed);
    }

    [Fact]
    public async Task Tick_ReplacementFailurePreservesPreviousDiskStateAndReleasesGateForRetry()
    {
        using var fixture = new TickFixture();
        var fail = false;
        var state = OrchestratorState.Load(fixture.Dir, new(
            ReadPlan: _ => Task.FromResult(new NotebookPlan([new("alpha", "Alpha")])),
            Decide: c => c.Row["doing"] = "New state",
            Rename: (source, destination) => { if (fail) throw new IOException("replacement failed"); File.Move(source, destination, true); }));
        state.Persist();
        var before = File.ReadAllBytes(state.StateFile!);
        fail = true;
        await Assert.ThrowsAsync<IOException>(() => state.TickAsync("alpha"));
        Assert.Equal(before, File.ReadAllBytes(state.StateFile!));
        Assert.Empty(Directory.GetFiles(fixture.Dir, "*.tmp"));
        fail = false;
        await state.TickAsync("alpha");
        Assert.Equal("New state", JsonNode.Parse(File.ReadAllText(state.StateFile!))!["projects"]!["alpha"]!["doing"]!.GetValue<string>());
    }




    [Fact]
    public async Task Tick_OrdersCollaboratorsScopesDecisionsAndPersistsTheirLiveRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-tick-" + Guid.NewGuid().ToString("N"));
        var effects = new List<string>();
        var plan = new NotebookPlan([new("alpha", "Alpha", Tasks: [new("job", "Build", Status: "running")]), new("beta", "Beta")]);
        OrchestratorState? state = null;
        try
        {
            state = OrchestratorState.Load(dir, new(
                SessionId: "test-session",
                RestoreUnavailableModels: () => effects.Add("restore"),
                RefreshPlans: (force, _) => { Assert.False(force); effects.Add("plans"); return Task.CompletedTask; },
                ReadPlan: _ => { effects.Add("read"); return Task.FromResult(plan); },
                RefreshRepositoryMetadata: (p, id) => { Assert.Same(plan, p); Assert.Equal("alpha", id); effects.Add("repositories"); },
                Decide: context =>
                {
                    Assert.Same(state!.EntryRow("alpha"), context.Row);
                    Assert.Equal("alpha", context.Project.Id);
                    Assert.True(context.WasRunningAtStartup);
                    Assert.Equal("test-session", context.SessionId);
                    context.Row["doing"] = "Decision on alpha";
                    effects.Add("decide");
                },
                RefreshPhaseTwoResearch: (p, schedule, id) => { Assert.Equal("alpha", id); effects.Add(schedule ? "phase2" : "phase2-view"); },
                RefreshResearch: () => effects.Add("research")));
            var snapshot = await state.TickAsync("alpha");
            Assert.Equal(["restore", "plans", "read", "repositories", "decide", "phase2", "research", "phase2-view", "research"], effects);
            Assert.Equal("Decision on alpha", snapshot.Projects.Single(p => p.Id == "alpha").Doing);
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "orchestrator.json")))!;
            Assert.Equal("Decision on alpha", saved["projects"]!["alpha"]!["doing"]!.GetValue<string>());
            Assert.Equal("", state.Entry("beta").Doing);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Tick_ShutdownStillRefreshesEvidenceAndPersistsWithoutDeciding()
    {
        var effects = new List<string>();
        var state = OrchestratorState.Load(null, new(
            ReadPlan: _ => Task.FromResult(new NotebookPlan([new("alpha", "Alpha")])),
            Decide: _ => throw new Exception("No dispatch while shutting down"),
            RefreshPhaseTwoResearch: (_, schedule, _) => { Assert.False(schedule); effects.Add("phase2"); },
            RefreshResearch: () => effects.Add("research"),
            IsShuttingDown: () => true));
        Assert.Single((await state.TickAsync()).Projects);
        Assert.Equal(["phase2", "research", "phase2", "research"], effects);
    }

    [Fact]
    public async Task Tick_AwaitsPlanWithoutBlockingAndCancellationReleasesTransitionGate()
    {
        var pending = new TaskCompletionSource<NotebookPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var state = OrchestratorState.Load(null, new(ReadPlan: token => ++reads == 1
            ? pending.Task.WaitAsync(token) : Task.FromResult(new NotebookPlan([]))));
        using var cancellation = new CancellationTokenSource();
        var tick = state.TickAsync(cancellationToken: cancellation.Token);
        Assert.False(tick.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
        Assert.Empty((await state.TickAsync()).Projects);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("STATUS: WATCHING\n", false)]
    [InlineData("STATUS: COMPLETE\n", true)]
    [InlineData("STATUS: FAILED\n", false)]
    [InlineData("", false)]
    public async Task Tick_LegacyCompletionRequiresEveryResultAndMatchingJob(string secondResult, bool expected)
    {
        var task = new NotebookTask("job", "Build", "Original", "running");
        var plan = new NotebookPlan([new("alpha", "Alpha", Tasks: [task]), new("beta", "Beta", Tasks: [task])]);
        var results = new List<CompletedNotebookTask>();
        var state = OrchestratorState.Load(null, new(
            ReadPlan: _ => Task.FromResult(plan), Decide: _ => { },
            ReadResult: path => path == "first" ? "STATUS: COMPLETE\nEXIT: 0\n" : secondResult,
            CompleteTasks: (completed, _) =>
            {
                results.AddRange(completed);
                plan = new NotebookPlan([plan.Projects[0] with { Tasks = [task with { Status = "complete" }] }, plan.Projects[1]]);
                return Task.FromResult(plan);
            }));
        foreach (var id in new[] { "alpha", "beta" })
        {
            var row = state.EntryRow(id);
            row["status"] = "complete"; row["jobId"] = "job";
            row["jobFingerprint"] = OrchestratorState.JobFingerprint(task);
            row["attemptId"] = "attempt-1";
            row["dispatch"] = new JsonArray(new JsonObject { ["resultPath"] = "first" }, new JsonObject { ["resultPath"] = "second" });
        }
        await state.TickAsync("alpha");
        await state.TickAsync("alpha");
        Assert.Equal(expected ? 1 : 0, results.Count);
        if (expected)
        {
            Assert.Equal("alpha", results[0].ProjectId);
            Assert.Equal("attempt-1", results[0].AttemptId);
        }
        // Reusing an ID for a different request must not complete the new request.
        plan = new NotebookPlan([plan.Projects[0] with { Tasks = [task with { Detail = "Different request" }] }]);
        await state.TickAsync("alpha");
        Assert.Equal(expected ? 1 : 0, results.Count);
    }

    [Fact]
    public async Task Tick_StatusLabelAndMissingFilesCannotCompleteNotebookTask()
    {
        var task = new NotebookTask("job", "Build", Status: "running");
        var state = OrchestratorState.Load(null, new(
            ReadPlan: _ => Task.FromResult(new NotebookPlan([new("alpha", "Alpha", Tasks: [task])])),
            Decide: _ => { }, ReadResult: _ => throw new FileNotFoundException(),
            CompleteTasks: (_, _) => throw new Exception("No completion without evidence")));
        var row = state.EntryRow("alpha");
        row["status"] = "complete"; row["jobId"] = "job"; row["jobFingerprint"] = OrchestratorState.JobFingerprint(task);
        await state.TickAsync();
        row["resultPath"] = "missing";
        await state.TickAsync();
    }

    [Fact]
    public async Task Tick_RestoresOnlyTemporarilyUnavailableModelsBeforeReadingPlan()
    {
        OrchestratorState? state = null;
        state = OrchestratorState.Load(null, new(Now: () => "2026-11-26T21:00:00Z", ReadPlan: _ =>
        {
            Assert.True(state!.Store["models"]![0]!["available"]!.GetValue<bool>());
            Assert.Null(state.Store["models"]![0]!["unavailableReason"]);
            return Task.FromResult(new NotebookPlan([]));
        }));
        state.Store["models"] = JsonNode.Parse("""
            [{"id":"temporary","available":false,"unavailableAt":"2026-11-26T19:00:00Z","unavailableReason":"Call failed"},
             {"id":"recent","available":false,"unavailableAt":"2026-11-26T20:59:59Z"},
             {"id":"permanent","available":false},
             {"id":"invalid","available":false,"unavailableAt":"invalid"}]
            """);
        await state.TickAsync();
        Assert.All(state.Store["models"]!.AsArray().Skip(1), model => Assert.False(model!["available"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Tick_RemembersFirstNotebookEvenWhenAProjectAppearsLater()
    {
        var job = new NotebookTask("old", "Old", Status: "running");
        var plan = new NotebookPlan([new("alpha", "Alpha", Tasks: [job])]);
        var observed = new List<(string Project, bool Startup)>();
        var state = OrchestratorState.Load(null, new(ReadPlan: _ => Task.FromResult(plan),
            Decide: c => observed.Add((c.Project.Id, c.WasRunningAtStartup))));
        state.Snapshot(plan);
        plan = new NotebookPlan([plan.Projects[0] with { Tasks = [job with { Id = "new" }] }, new("beta", "Beta", Tasks: [job])]);
        await state.TickAsync();
        Assert.Equal(new[] { ("alpha", false), ("beta", false) }, observed);
    }


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

    [Fact]
    public void Persist_UsesUniqueSameDirectoryTempFiles_AndNodeCompatibleFormatting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var observedTempPaths = new List<string>();
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:00:00.000Z",
                SessionId: "format-session",
                Rename: (src, dst) =>
                {
                    observedTempPaths.Add(src);
                    File.Move(src, dst, overwrite: true);
                }));
            state.EntryRow("proj")["doing"] = "cafè — <alpha> & 'beta' + 100%";
            state.Persist();
            state.EntryRow("proj")["workerTail"] = "second";
            state.Persist();

            Assert.Equal(2, observedTempPaths.Count);
            Assert.NotEqual(observedTempPaths[0], observedTempPaths[1]);
            foreach (var tempPath in observedTempPaths)
            {
                Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(Path.GetDirectoryName(tempPath)!));
                Assert.StartsWith(state.StateFile! + ".", tempPath, StringComparison.Ordinal);
                Assert.EndsWith(".tmp", tempPath, StringComparison.Ordinal);
            }

            var raw = File.ReadAllText(state.StateFile!);
            Assert.EndsWith("\n", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\n", raw, StringComparison.Ordinal);
            Assert.Contains("cafè — <alpha> & 'beta' + 100%", raw, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_DisablesStaleSessionAutonomy_AndRepairsModesPreservingUnknownFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "orchestrator.json"), """
            {
                "customTop": { "kept": true },
                "autonomy": {
                    "alpha": { "enabled": true, "sessionId": "old-session" },
                    "beta": { "enabled": true, "sessionId": "current-session" }
                },
                "chatModes": { "alpha": "auto", "beta": "talk" },
                "savingModes": { "alpha": "bad-mode", "beta": "safe" },
                "modelWeights": { "m1": "premium", "m2": "bogus" },
                "projectTeams": {
                    "alpha": { "orchestratorId": null, "roles": { "m1": { "worker": true } }, "notLead": ["m1", 42], "teamExtension": { "keep": true } },
                    "scalarNotLead": { "orchestratorId": "lead", "roles": {}, "notLead": "bad" },
                    "invalid": "not-an-object"
                },
                "projectTools": {
                    "alpha": { "default": { "Read": true, "toolExtension": "keep" }, "models": {}, "settingsExtension": 7 }
                },
                "execution": null,
                "pullRequestFixes": [
                    { "projectId": "alpha", "number": 12, "result": "fixed" },
                    { "projectId": "alpha", "number": "bad" }
                ]
            }
            """);

            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:00:00.000Z",
                SessionId: "current-session"));

            Assert.False(state.Store["autonomy"]!["alpha"]!["enabled"]!.GetValue<bool>());
            Assert.True(state.Store["autonomy"]!["beta"]!["enabled"]!.GetValue<bool>());
            Assert.Equal("plan", state.Store["chatModes"]!["alpha"]!.GetValue<string>());
            Assert.Equal("talk", state.Store["chatModes"]!["beta"]!.GetValue<string>());
            Assert.Equal("normal", state.Store["savingModes"]!["alpha"]!.GetValue<string>());
            Assert.Equal("safe", state.Store["savingModes"]!["beta"]!.GetValue<string>());
            Assert.Equal("premium", state.Store["modelWeights"]!["m1"]!.GetValue<string>());
            Assert.False((state.Store["modelWeights"] as JsonObject)!.ContainsKey("m2"));
            Assert.Single((state.Store["pullRequestFixes"] as JsonArray)!);
            Assert.False((state.Store["projectTeams"] as JsonObject)!.ContainsKey("invalid"));
            Assert.True(state.Store["projectTeams"]!["alpha"]!["teamExtension"]!["keep"]!.GetValue<bool>());
            Assert.NotNull(state.Store["projectTeams"]!["scalarNotLead"]);
            Assert.False(state.Store["projectTeams"]!["scalarNotLead"]!.AsObject().ContainsKey("notLead"));
            Assert.Equal("keep", state.Store["projectTools"]!["alpha"]!["default"]!["toolExtension"]!.GetValue<string>());
            Assert.Equal(7, state.Store["projectTools"]!["alpha"]!["settingsExtension"]!.GetValue<int>());
            Assert.IsType<JsonObject>(state.Store["execution"]);
            Assert.True(state.Store["customTop"]!["kept"]!.GetValue<bool>());

            // savingRepaired persisted repaired savingModes/modelWeights immediately on load
            var onDisk = JsonNode.Parse(File.ReadAllText(state.StateFile!))!.AsObject();
            Assert.Equal("normal", onDisk["savingModes"]!["alpha"]!.GetValue<string>());
            Assert.True(onDisk["customTop"]!["kept"]!.GetValue<bool>());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task TickAsync_ReconcilesLegacyWatchingRows_IdempotentlyWithoutDoubleClosingAttempts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var attempts = AttemptEngine.Create(dir);
            var claim = attempts.Claim(new JsonObject
            {
                ["taskId"] = "legacy-job",
                ["projectId"] = "alpha",
                ["modelId"] = "worker-1",
                ["candidates"] = new JsonArray(new JsonObject { ["id"] = "worker-1", ["enabled"] = true })
            });
            var attemptId = claim["attempt"]!["id"]!.GetValue<string>();
            var resultPath = Path.Combine(dir, "legacy-result.txt");
            File.WriteAllText(resultPath, "STATUS: COMPLETE\nEXIT: 0\n");
            var historicalResultBytes = File.ReadAllBytes(resultPath);

            File.WriteAllText(Path.Combine(dir, "orchestrator.json"), new JsonObject
            {
                ["projects"] = new JsonObject
                {
                    ["alpha"] = new JsonObject
                    {
                        ["status"] = "watching",
                        ["jobId"] = "legacy-job",
                        ["attemptId"] = attemptId,
                        ["workerAlive"] = true,
                        ["dispatch"] = new JsonArray(new JsonObject
                        {
                            ["modelId"] = "worker-1",
                            ["pid"] = 999,
                            ["alive"] = true,
                            ["resultPath"] = resultPath
                        })
                    }
                }
            }.ToJsonString());

            var closedCalls = 0;
            var adapter = new RecordingAttemptAdapter(() => closedCalls++);
            var plan = new NotebookPlan([new NotebookProject("alpha", "Alpha")]);
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:00:00.000Z",
                SessionId: "reconcile-1",
                Attempts: attempts,
                AttemptAdapter: adapter,
                IsProcessAlive: _ => false,
                ReadPlan: _ => Task.FromResult(plan),
                Phase2: true));

            var snap1 = await state.TickAsync("alpha");
            var snap2 = await state.TickAsync("alpha");
            state.Persist();

            var reloaded = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:05:00.000Z",
                SessionId: "reconcile-2",
                Attempts: attempts,
                AttemptAdapter: adapter,
                IsProcessAlive: _ => false,
                ReadPlan: _ => Task.FromResult(plan),
                Phase2: true));
            var snap3 = await reloaded.TickAsync("alpha");

            Assert.Equal(1, closedCalls);
            Assert.Equal("complete", snap1.Projects[0].Status);
            Assert.Equal("Legacy completion recorded", snap1.Projects[0].Label);
            Assert.False(snap1.Projects[0].WorkerAlive);
            Assert.Equal("complete", snap2.Projects[0].Status);
            Assert.Equal("complete", snap3.Projects[0].Status);
            var sessions = Assert.IsType<JsonArray>(reloaded.Store["sessions"]);
            Assert.Equal(1, sessions.OfType<JsonObject>().Count(s => s["id"]?.GetValue<string>() == "reconcile-1"));
            Assert.Equal(1, sessions.OfType<JsonObject>().Count(s => s["id"]?.GetValue<string>() == "reconcile-2"));
            Assert.Equal(historicalResultBytes, File.ReadAllBytes(resultPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Snapshot_WorkerSnapshotParity_TerminalLivenessCrewRolesAndMonitoring()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ids = new[] { "dead-complete", "reused-complete", "running", "dead-watching", "paused", "crew-roles" };
            var projects = new JsonObject();
            foreach (var id in ids)
            {
                projects[id] = new JsonObject
                {
                    ["status"] = id == "paused" ? "checkpointed" : id.Contains("complete") ? "complete" : "watching",
                    ["modelId"] = "fixture",
                    ["workerPid"] = id.StartsWith("dead") ? 111 : 222,
                    ["workerAlive"] = true,
                    ["monitoring"] = new JsonObject { ["workers"] = 1, ["alive"] = 1, ["lastProgress"] = "2026-10-10T20:00:00Z" },
                    ["dispatch"] = new JsonArray(new JsonObject
                    {
                        ["modelId"] = "fixture",
                        ["pid"] = id.StartsWith("dead") ? 111 : 222,
                        ["alive"] = true,
                        ["slice"] = "Fixture"
                    })
                };
            }
            projects["crew-roles"]!["megaTaskId"] = "crew-current";
            projects["crew-roles"]!["dispatch"] = new JsonArray(
                new JsonObject { ["label"] = "Current reviewer", ["modelId"] = "fixture", ["pid"] = 222, ["megaTaskId"] = "crew-current", ["subTaskId"] = "current-review" },
                new JsonObject { ["label"] = "Historical reviewer", ["modelId"] = "fixture", ["pid"] = 222, ["megaTaskId"] = "crew-old", ["subTaskId"] = "old-review" },
                new JsonObject { ["label"] = "Retained audit fallback", ["modelId"] = "fixture", ["type"] = "AUDIT", ["pid"] = 111 },
                new JsonObject { ["label"] = "Legacy worker fallback", ["modelId"] = "fixture", ["pid"] = 111 });

            var execution = new JsonObject
            {
                ["megaTasks"] = new JsonArray(
                    new JsonObject { ["id"] = "crew-current", ["projectId"] = "crew-roles", ["title"] = "Current review", ["status"] = "RUNNING" },
                    new JsonObject { ["id"] = "crew-old", ["projectId"] = "crew-roles", ["title"] = "Original review", ["status"] = "COMPLETE" }),
                ["subTasks"] = new JsonArray(
                    new JsonObject { ["id"] = "current-review", ["megaTaskId"] = "crew-current", ["type"] = "AUDIT", ["status"] = "RUNNING" },
                    new JsonObject { ["id"] = "old-review", ["megaTaskId"] = "crew-old", ["type"] = "AUDIT", ["status"] = "COMPLETE" }),
                ["results"] = new JsonArray(),
                ["checkpoints"] = new JsonArray(),
                ["timeline"] = new JsonArray()
            };
            var evidence = new JsonObject { ["projects"] = projects, ["execution"] = execution, ["mode"] = "manual", ["researches"] = new JsonArray() }.ToJsonString();
            var stateFile = Path.Combine(dir, "orchestrator.json");
            File.WriteAllText(stateFile, evidence);

            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:00:00.000Z",
                SessionId: "ws-session",
                IsProcessAlive: pid => pid == "222"));
            var plan = new NotebookPlan(ids.Select(id => new NotebookProject(id, id)).ToArray());
            var snapshot = state.Snapshot(plan);

            ProjectSnapshot ById(string id) => snapshot.Projects.Single(p => p.Id == id);
            foreach (var id in new[] { "dead-complete", "reused-complete", "dead-watching", "paused" })
            {
                Assert.False(ById(id).Dispatch[0].Alive);
                Assert.False(ById(id).WorkerAlive);
                Assert.Equal(0, ById(id).Monitoring?.Alive);
                if (id is "dead-complete" or "reused-complete" or "paused")
                {
                    Assert.NotEqual("running", ById(id).Dispatch[0].ActivitySession?.ProcessState);
                }
            }
            Assert.True(ById("running").Dispatch[0].Alive);
            Assert.True(ById("running").WorkerAlive);
            Assert.Equal(1, ById("running").Monitoring?.Alive);
            Assert.Equal("running", ById("running").Dispatch[0].ActivitySession?.ProcessState);
            Assert.Null(ById("running").SessionId);
            Assert.Equal(222, ById("reused-complete").Dispatch[0].Pid);

            var crew = ById("crew-roles").Crew;
            Assert.Equal("Reviewer", crew.Single(c => c.Name == "Current reviewer").Role);
            Assert.Equal("Reviewer", crew.Single(c => c.Name == "Historical reviewer").Role);
            Assert.Equal("Reviewer", crew.Single(c => c.Name == "Retained audit fallback").Role);
            Assert.Equal("Worker", crew.Single(c => c.Name == "Legacy worker fallback").Role);
            Assert.Contains("Original review", crew.Single(c => c.Name == "Historical reviewer").Doing);
            Assert.Equal(evidence, File.ReadAllText(stateFile));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RefreshResearch_CoversV1CompletionAndFailureEvidence_AndV2Diagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-research-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var okReport = Path.Combine(dir, "ok-report.md");
            var okResult = Path.Combine(dir, "ok-result.txt");
            var okLog = Path.Combine(dir, "ok.log");
            File.WriteAllText(okReport, "# Findings\nEvidence\n");
            File.WriteAllText(okResult, "STATUS: COMPLETE\r\nEXIT: 0\r\n");
            File.WriteAllText(okResult + ".diagnostic.json", "{\"provider\":\"codex\"}");
            File.WriteAllText(okLog, "step 1\n");

            var missingReportResult = Path.Combine(dir, "missing-report-result.txt");
            File.WriteAllText(missingReportResult, "STATUS: COMPLETE\nEXIT: 0\n");

            var deadResult = Path.Combine(dir, "dead-result.txt");
            File.WriteAllText(deadResult, "STATUS: WATCHING\n");

            var v2Report = Path.Combine(dir, "v2-report.md");
            var v2Log = Path.Combine(dir, "v2.log");
            File.WriteAllText(v2Report, "# V2 Report\n");
            File.WriteAllText(v2Log, "v2 line\n");

            var closedReasons = new List<(string Id, string Reason)>();
            var adapter = new RecordingAttemptAdapter((_, id, reason) => closedReasons.Add((id, reason)));
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:10:00.000Z",
                SessionId: "research-session",
                AttemptAdapter: adapter,
                IsProcessAlive: pid => pid == "501"));

            state.Store["execution"] = new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject
                {
                    ["megaTaskId"] = "mt-r2",
                    ["reportPath"] = v2Report,
                    ["summary"] = "V2 findings complete."
                })
            };
            state.Store["researches"] = new JsonArray(
                new JsonObject { ["id"] = "r-ok", ["projectId"] = "alpha", ["executionVersion"] = 1, ["status"] = "watching", ["pid"] = 502, ["attemptId"] = "att-ok", ["reportPath"] = okReport, ["resultPath"] = okResult, ["logPath"] = okLog },
                new JsonObject { ["id"] = "r-norep", ["projectId"] = "alpha", ["executionVersion"] = 1, ["status"] = "watching", ["pid"] = 503, ["attemptId"] = "att-norep", ["reportPath"] = Path.Combine(dir, "absent.md"), ["resultPath"] = missingReportResult },
                new JsonObject { ["id"] = "r-dead", ["projectId"] = "alpha", ["executionVersion"] = 1, ["status"] = "watching", ["pid"] = 504, ["attemptId"] = "att-dead", ["reportPath"] = Path.Combine(dir, "absent2.md"), ["resultPath"] = deadResult },
                new JsonObject { ["id"] = "r-live", ["projectId"] = "alpha", ["executionVersion"] = 1, ["status"] = "watching", ["pid"] = 501, ["reportPath"] = Path.Combine(dir, "absent3.md"), ["resultPath"] = deadResult },
                new JsonObject
                {
                    ["id"] = "r-v2",
                    ["projectId"] = "alpha",
                    ["megaTaskId"] = "mt-r2",
                    ["executionVersion"] = 2,
                    ["status"] = "watching",
                    ["dispatch"] = new JsonArray(new JsonObject { ["pid"] = 501, ["alive"] = true, ["folder"] = dir, ["logPath"] = v2Log, ["sessionId"] = "v2-sess" })
                });

            var snap = state.Snapshot(new NotebookPlan([new NotebookProject("alpha", "Alpha")]));

            var rOk = snap.Researches.Single(r => r.Id == "r-ok");
            Assert.Equal("complete", rOk.Status);
            Assert.True(rOk.ReportAvailable);
            Assert.Equal("codex", rOk.Diagnostic?["provider"]?.GetValue<string>());

            var rNoRep = snap.Researches.Single(r => r.Id == "r-norep");
            Assert.Equal("failed", rNoRep.Status);
            Assert.False(rNoRep.ReportAvailable);
            Assert.Contains("without the required report.md", rNoRep.Reason);

            var rDead = snap.Researches.Single(r => r.Id == "r-dead");
            Assert.Equal("failed", rDead.Status);
            Assert.Contains("without successful completion evidence", rDead.Reason);

            var rLive = snap.Researches.Single(r => r.Id == "r-live");
            Assert.Equal("watching", rLive.Status);
            Assert.True(rLive.Alive);

            var rV2 = snap.Researches.Single(r => r.Id == "r-v2");
            Assert.True(rV2.Alive);
            Assert.True(rV2.ReportAvailable);
            Assert.Equal("v2-sess", rV2.SessionId);
            Assert.Equal("V2 findings complete.", rV2.Activity);
            Assert.Contains(closedReasons, x => x.Id == "att-ok");
            Assert.Contains(closedReasons, x => x.Id == "att-norep");
            Assert.Contains(closedReasons, x => x.Id == "att-dead");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ExecutionBridge_RowForDelegate_SharesLiveDurableRowsWithoutSecondCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-bridge-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(dir, "alpha-repo");
        Directory.CreateDirectory(repo);
        try
        {
            var attempts = AttemptEngine.Create(dir);
            var idSeq = 0;
            var launches = new List<JsonObject>();
            var task = new NotebookTask("job-v2", "Build v2", "Shared row task", "running", "1", 2);
            var project = new NotebookProject("alpha", "Alpha", repo, Revisions: [new NotebookRevision("Ship v2", Id: "1")], Tasks: [task]);
            var plan = new NotebookPlan([project]);
            var completedCallbackCount = 0;
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:15:00.000Z",
                SessionId: "bridge-session",
                Attempts: attempts,
                IsProcessAlive: pid => pid == "701",
                ReadPlan: _ => Task.FromResult(plan),
                CompleteTasks: (items, _) => { completedCallbackCount += items.Count; return Task.FromResult(plan); }));

            var bridge = new ExecutionBridge(new ExecutionOptions
            {
                Store = state.Store,
                Attempts = attempts,
                Directory = dir,
                RowFor = state.RowForDelegate,
                Inspect = folder => OrchestratorState.DefaultInspectJson(folder),
                Models = () => [new JsonObject
                {
                    ["id"] = "worker-a", ["label"] = "Worker A", ["provider"] = "codex", ["cli"] = "gpt-6-luna",
                    ["kind"] = "premium", ["enabled"] = true, ["available"] = true, ["providerEnabled"] = true,
                    ["capabilities"] = new JsonObject { ["canUseTools"] = true, ["canInspectFilesystem"] = true, ["canCode"] = true, ["canResearch"] = true }
                }],
                Now = () => "2026-11-26T22:15:00.000Z",
                Persist = state.Persist,
                LeadId = () => "worker-a",
                SessionId = "bridge-session",
                NewId = () => $"id-{++idSeq}",
                SpawnWorker = info => { launches.Add((JsonObject)info.DeepClone()); return new JsonObject { ["pid"] = 701 }; },
                IsProcessAlive = pid => pid == 701,
                KillWorker = _ => true
            });
            state.AttachExecutionBridge(bridge);

            var itemJson = new JsonObject
            {
                ["schema"] = 1,
                ["project"] = new JsonObject { ["id"] = "alpha", ["name"] = "Alpha", ["folder"] = repo },
                ["revisions"] = new JsonArray(new JsonObject { ["id"] = "1", ["direction"] = "Ship v2" }),
                ["tasks"] = new JsonArray(new JsonObject { ["id"] = "job-v2", ["title"] = "Build v2", ["detail"] = "Shared row task", ["status"] = "running", ["revision_id"] = "1", ["executionVersion"] = 2 })
            };
            var jobJson = new JsonObject { ["id"] = "job-v2", ["title"] = "Build v2", ["detail"] = "Shared row task", ["revision_id"] = "1", ["executionVersion"] = 2 };
            var mega = bridge.Propose(itemJson, jobJson, [new JsonObject { ["modelId"] = "worker-a", ["slice"] = "Implement shared state" }]);
            var megaId = mega["id"]!.GetValue<string>();

            Assert.True(ReferenceEquals(state.EntryRow("alpha"), state.RowFor("alpha")));
            Assert.Equal(megaId, state.Entry("alpha").MegaTaskId);

            // Detached v2 research routes through RowFor to store["researches"] without overwriting alpha's project row
            var resJob = new JsonObject { ["id"] = "res-v2", ["title"] = "Investigate", ["detail"] = "Read only", ["revision_id"] = "1", ["kind"] = "research", ["detached"] = true };
            var resRecord = new JsonObject { ["id"] = "res-v2", ["projectId"] = "alpha", ["executionVersion"] = 2, ["status"] = "needs_approval", ["job"] = resJob.DeepClone() };
            (state.Store["researches"] as JsonArray)!.Add((JsonNode)resRecord);
            var resMega = bridge.Propose(itemJson, resJob, [new JsonObject { ["modelId"] = "worker-a", ["slice"] = "Research", ["type"] = "RESEARCH" }]);
            var resMegaId = resMega["id"]!.GetValue<string>();
            resRecord["megaTaskId"] = resMegaId;

            Assert.True(ReferenceEquals(resRecord, state.RowFor("alpha", resMegaId)));
            Assert.Equal(megaId, state.Entry("alpha").MegaTaskId);

            bridge.Approve("alpha", megaId);
            var tickSnap = await state.TickAsync("alpha");
            Assert.Single(launches);
            Assert.Equal(megaId, tickSnap.Projects[0].MegaTaskId);
            Assert.Equal("watching", tickSnap.Projects[0].Status);
            Assert.True(tickSnap.Projects[0].CanStop);
            Assert.Equal(0, completedCallbackCount);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task TickAsync_CancellationAndFailureRecovery_ReleasesGateAndRefreshesUnavailableModels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-tick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = "2026-11-26T22:00:00.000Z";
            var throwOnPlan = true;
            var plan = new NotebookPlan([new NotebookProject("alpha", "Alpha")]);
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => now,
                SessionId: "tick-session",
                ReadPlan: _ => throwOnPlan ? throw new InvalidOperationException("transient read error") : Task.FromResult(plan)));

            state.Store["models"] = new JsonArray(new JsonObject
            {
                ["id"] = "m1", ["label"] = "Model 1", ["available"] = false,
                ["unavailableAt"] = "2026-11-26T21:40:00.000Z", ["unavailableReason"] = "rate limit"
            });

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.TickAsync("alpha", cts.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.TickAsync("alpha"));

            // Gate must have been released after cancellation and failure
            throwOnPlan = false;
            now = "2026-11-26T22:00:00.000Z";
            var snap = await state.TickAsync("alpha");
            Assert.True(snap.Models.Single(m => m.Id == "m1").Available);
            Assert.Null(state.Store["models"]![0]!["unavailableReason"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task DefaultInspect_Containment_RejectsParentReposAndRedirectedGitDir_WhileKeepingLocalEditsVisible()
    {
        var root = Path.Combine(Path.GetTempPath(), "planwharf-state-git-" + Guid.NewGuid().ToString("N"));
        var parentAlpha = Path.Combine(root, "parent", "alpha");
        var parentBeta = Path.Combine(root, "parent", "beta");
        var isoAlpha = Path.Combine(root, "iso-alpha");
        var isoBeta = Path.Combine(root, "iso-beta");
        Directory.CreateDirectory(parentAlpha);
        Directory.CreateDirectory(parentBeta);
        Directory.CreateDirectory(isoAlpha);
        Directory.CreateDirectory(isoBeta);
        try
        {
            RunGit(Path.Combine(root, "parent"), "init", "-q");
            RunGit(Path.Combine(root, "parent"), "config", "user.name", "Fixture");
            RunGit(Path.Combine(root, "parent"), "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(parentAlpha, "a.txt"), "alpha\n");
            File.WriteAllText(Path.Combine(parentBeta, "b.txt"), "beta\n");
            RunGit(Path.Combine(root, "parent"), "add", ".");
            RunGit(Path.Combine(root, "parent"), "commit", "-qm", "Init");
            Assert.False(OrchestratorState.DefaultInspect(parentAlpha).Git);

            RunGit(isoBeta, "init", "-q");
            RunGit(isoBeta, "config", "user.name", "Fixture");
            RunGit(isoBeta, "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(isoBeta, "secret.txt"), "foreign\n");
            RunGit(isoBeta, "add", ".");
            RunGit(isoBeta, "commit", "-qm", "Foreign");

            File.WriteAllText(Path.Combine(isoAlpha, ".git"), $"gitdir: {Path.Combine(isoBeta, ".git")}\n");
            Assert.False(OrchestratorState.DefaultInspect(isoAlpha).Git);
            File.Delete(Path.Combine(isoAlpha, ".git"));

            RunGit(isoAlpha, "init", "-q");
            RunGit(isoAlpha, "config", "user.name", "Fixture");
            RunGit(isoAlpha, "config", "user.email", "fixture@example.invalid");
            Directory.CreateDirectory(Path.Combine(isoAlpha, "sub"));
            File.WriteAllText(Path.Combine(isoAlpha, "sub", ".git"), $"gitdir: {Path.Combine(isoBeta, ".git")}\n");
            var betaHead = RunGit(isoBeta, "rev-parse", "HEAD");
            RunGit(isoAlpha, "update-index", "--add", "--cacheinfo", $"160000,{betaHead},sub");
            File.WriteAllText(Path.Combine(isoAlpha, "local.txt"), "v1\n");
            RunGit(isoAlpha, "add", "local.txt");
            RunGit(isoAlpha, "commit", "-qm", "Local");
            RunGit(isoAlpha, "config", "diff.submodule", "log");
            RunGit(isoAlpha, "config", "submodule.sub.ignore", "none");

            var before = OrchestratorState.DefaultInspect(isoAlpha);
            Assert.True(before.Git);
            Assert.Empty(before.Dirty!);

            File.WriteAllText(Path.Combine(isoBeta, "secret.txt"), "foreign-2\n");
            RunGit(isoBeta, "commit", "-qam", "Foreign-2");
            Assert.Empty(OrchestratorState.DefaultInspect(isoAlpha).Dirty!);

            File.WriteAllText(Path.Combine(isoAlpha, "local.txt"), "v2\n");
            var after = OrchestratorState.DefaultInspect(isoAlpha);
            Assert.Contains(after.Dirty!, d => d.EndsWith("local.txt", StringComparison.Ordinal));
            Assert.Contains("local.txt", OrchestratorState.DefaultInspectJson(isoAlpha)["diffStat"]?.GetValue<string>() ?? "", StringComparison.Ordinal);

            // InspectRepositoryAsync succeeds for a ReadPlan project even before EntryRow was created
            var state = OrchestratorState.Load(Path.Combine(root, "state"), new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:20:00.000Z",
                ReadPlan: _ => Task.FromResult(new NotebookPlan([new NotebookProject("iso", "Iso", isoAlpha)]))));
            var res = await state.InspectRepositoryAsync("iso");
            Assert.True(res.Ok);
            Assert.NotNull(res.Snapshot);
            Assert.False((await state.InspectRepositoryAsync("missing")).Ok);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ArchiveProjectAsync_And_SetFocusAsync_IsolateProjects_AndSerializeConcurrently()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plan = new NotebookPlan([new NotebookProject("alpha", "Alpha"), new NotebookProject("beta", "Beta")]);
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:25:00.000Z",
                SessionId: "conc-session",
                IsProcessAlive: pid => pid == "888",
                ReadPlan: async _ => { await Task.Yield(); return plan; }));

            state.EntryRow("alpha")["status"] = "watching";
            state.EntryRow("alpha")["dispatch"] = new JsonArray(new JsonObject { ["pid"] = 888, ["alive"] = true });
            state.EntryRow("beta")["status"] = "idle";
            state.Store["autonomy"] = new JsonObject { ["alpha"] = new JsonObject { ["enabled"] = false }, ["beta"] = new JsonObject { ["enabled"] = false } };
            state.Store["projectTeams"] = new JsonObject
            {
                ["alpha"] = new JsonObject { ["orchestratorId"] = null, ["roles"] = new JsonObject() },
                ["beta"] = new JsonObject { ["orchestratorId"] = "lead-b", ["roles"] = new JsonObject() }
            };
            state.Store["models"] = new JsonArray(new JsonObject { ["id"] = "lead-b", ["label"] = "Lead B", ["enabled"] = true, ["available"] = true });
            state.Store["asks"] = new JsonArray(
                new JsonObject { ["id"] = "ask-a", ["projectId"] = "alpha", ["question"] = "A?" },
                new JsonObject { ["id"] = "ask-b", ["projectId"] = "beta", ["question"] = "B?" });
            state.Store["researches"] = new JsonArray(
                new JsonObject { ["id"] = "res-a", ["projectId"] = "alpha", ["status"] = "complete" },
                new JsonObject { ["id"] = "res-b", ["projectId"] = "beta", ["status"] = "complete" });
            state.Store["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray(
                    new JsonObject { ["id"] = "mt-a", ["projectId"] = "alpha", ["status"] = "COMPLETE" },
                    new JsonObject { ["id"] = "mt-b", ["projectId"] = "beta", ["status"] = "COMPLETE" }),
                ["subTasks"] = new JsonArray(
                    new JsonObject { ["id"] = "st-a", ["megaTaskId"] = "mt-a" },
                    new JsonObject { ["id"] = "st-b", ["megaTaskId"] = "mt-b" }),
                ["results"] = new JsonArray(
                    new JsonObject { ["resultId"] = "R-a", ["megaTaskId"] = "mt-a" },
                    new JsonObject { ["resultId"] = "R-b", ["megaTaskId"] = "mt-b" }),
                ["checkpoints"] = new JsonArray(),
                ["timeline"] = new JsonArray()
            };

            // Live worker blocks archive
            var blocked = await state.ArchiveProjectAsync("alpha", "user removed");
            Assert.False(blocked.Ok);
            Assert.Equal("Stop the work on this project first.", blocked.Reason);

            // Stop worker, then run concurrent archive(alpha), focus(beta), tick(beta), invalid focus
            state.EntryRow("alpha")["status"] = "complete";
            var archiveTask = state.ArchiveProjectAsync("alpha", "user removed", new ArchiveProjectOptions(Notebook: plan.Projects[0]));
            var focusBetaTask = state.SetFocusAsync("beta");
            var focusMissingTask = state.SetFocusAsync("does-not-exist");
            var tickBetaTask = state.TickAsync("beta");
            await Task.WhenAll(archiveTask, focusBetaTask, focusMissingTask, tickBetaTask);
            var archiveResult = await archiveTask;
            var focusBetaResult = await focusBetaTask;
            var focusMissingResult = await focusMissingTask;

            Assert.True(archiveResult.Ok);
            Assert.Equal(1, archiveResult.Archived!.Asks);
            Assert.Equal(1, archiveResult.Archived.MegaTasks);
            Assert.True(focusBetaResult.Ok);
            Assert.Equal("beta", focusBetaResult.SelectedProjectId);
            Assert.False(focusMissingResult.Ok);

            // Note: tickBetaTask ran concurrently against static `plan` containing `alpha`; if `tickBetaTask` ran after `archiveTask`,
            // Snapshot(plan) re-creates an empty idle entry for `alpha` (Start Over semantics). Verify live history/asks/researches/execution are purged.
            var alphaEntry = state.TryGetEntry("alpha");
            Assert.True(alphaEntry is null || (alphaEntry.Status == "idle" && alphaEntry.MegaTaskId is null && alphaEntry.Dispatch.Count == 0));
            Assert.NotNull(state.TryGetEntry("beta"));
            Assert.Single((state.Store["asks"] as JsonArray)!);
            Assert.Single((state.Store["researches"] as JsonArray)!);
            Assert.Single((state.Store["execution"]!["megaTasks"] as JsonArray)!);
            Assert.Single((state.Store["execution"]!["subTasks"] as JsonArray)!);
            Assert.Single((state.Store["execution"]!["results"] as JsonArray)!);
            Assert.Single((state.Store["projectArchive"] as JsonArray)!);

            // When removed from the notebook plan, archiving leaves no entry in store["projects"] across subsequent ticks
            plan = new NotebookPlan([plan.Projects[1]]);
            state.Store["projects"]!.AsObject().Remove("alpha");
            await state.TickAsync("beta");
            Assert.Null(state.TryGetEntry("alpha"));

            // Project team with null orchestratorId must not inherit shared lead label
            state.Store["orchestratorId"] = "lead-b";
            state.Store["projectTeams"]!["empty-team"] = new JsonObject { ["orchestratorId"] = null, ["roles"] = new JsonObject() };
            var snap = state.Snapshot(new NotebookPlan([new NotebookProject("empty-team", "Empty Team"), new NotebookProject("beta", "Beta")]));
            Assert.Equal(OrchestratorState.OrchestratorDefaultName, snap.Projects.Single(p => p.Id == "empty-team").LeadLabel);
            Assert.Equal("Lead B", snap.Projects.Single(p => p.Id == "beta").LeadLabel);
            Assert.Equal("beta", snap.SelectedProjectId);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ArchiveProjectAsync_DoesNotRecreateEarlierArchivedProjectDuringSecondArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-archive-sequence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plan = new NotebookPlan([new NotebookProject("alpha", "Alpha"), new NotebookProject("beta", "Beta")]);
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-27T10:00:00.000Z", ReadPlan: _ => Task.FromResult(plan)));
            state.EntryRow("alpha")["status"] = "complete";
            state.EntryRow("beta")["status"] = "complete";

            Assert.True((await state.ArchiveProjectAsync("alpha", "remove alpha")).Ok);
            Assert.True((await state.ArchiveProjectAsync("beta", "remove beta")).Ok);

            Assert.Empty((state.Store["projects"] as JsonObject)!);
            Assert.Equal(2, (state.Store["projectArchive"] as JsonArray)!.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void HydrateCheckpoints_RejectsTraversalIds_AndSnapshotIncludesCompleteFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-state-fields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "checkpoints"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "escape.json"), "{\"jobTitle\":\"Should not load\"}");
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-26T22:30:00.000Z",
                SessionId: "fields-session"));

            state.EntryRow("../escape");
            state.HydrateCheckpoints();
            Assert.Null(state.EntryRow("../escape")["checkpoint"]);

            state.Store["orchestratorId"] = "missing-lead";
            state.Store["goldModelId"] = "gold-m";
            state.Store["models"] = new JsonArray(
                new JsonObject { ["id"] = "m-orch", ["label"] = "Orch M", ["kind"] = "premium", ["enabled"] = true, ["available"] = true, ["roles"] = new JsonObject { ["orchestrator"] = true } });
            state.Store["projectTools"] = new JsonObject
            {
                ["alpha"] = new JsonObject { ["default"] = new JsonObject { ["Read"] = true }, ["models"] = new JsonObject { ["m-orch"] = new JsonObject { ["Edit"] = true } } }
            };
            state.Store["thinkingByKey"] = new JsonObject
            {
                ["alpha"] = new JsonObject { ["startedAt"] = "2026-11-26T22:29:00.000Z", ["draft"] = "thinking" }
            };
            state.Store["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray(new JsonObject { ["id"] = "mt-1", ["projectId"] = "alpha", ["status"] = "PAUSED", ["title"] = "Work 1" }),
                ["subTasks"] = new JsonArray(),
                ["results"] = new JsonArray(),
                ["checkpoints"] = new JsonArray(new JsonObject { ["id"] = "cp-1", ["projectId"] = "alpha", ["megaTaskId"] = "mt-1", ["path"] = Path.Combine(dir, "checkpoints", "alpha.json") }),
                ["decisions"] = new JsonArray(
                    new JsonObject { ["id"] = "d-nonbreak", ["projectId"] = "alpha", ["megaTaskId"] = "mt-1", ["text"] = "Later?", ["breaks"] = false, ["at"] = "2026-11-26T22:02:00.000Z" },
                    new JsonObject { ["id"] = "d-break", ["projectId"] = "alpha", ["megaTaskId"] = "mt-1", ["text"] = "Urgent?", ["breaks"] = true, ["at"] = "2026-11-26T22:01:00.000Z" },
                    new JsonObject { ["id"] = "d-done", ["projectId"] = "alpha", ["megaTaskId"] = "mt-1", ["text"] = "Done?", ["breaks"] = false, ["answer"] = "Yes", ["at"] = "2026-11-26T22:00:00.000Z" })
            };
            state.EntryRow("alpha")["megaTaskId"] = "mt-1";

            var snap = state.Snapshot(new NotebookPlan([new NotebookProject("alpha", "Alpha")]), "alpha");
            Assert.Equal("gold-m", snap.GoldModelId);
            Assert.NotNull(snap.OrchestratorUnavailableReason);
            Assert.Single(snap.Orchestrators);
            Assert.True(snap.Asking);
            Assert.True(snap.Thinking.ContainsKey("draft"));
            Assert.True(snap.ThinkingByProject.ContainsKey("alpha"));
            Assert.True(snap.ProjectTools.ContainsKey("alpha"));
            Assert.True(snap.ProjectToolSettings.ContainsKey("alpha"));
            Assert.Equal(2, snap.DecisionsInbox.OpenCount);
            Assert.Equal("d-break", snap.DecisionsInbox.Open[0].Id);
            Assert.Single(snap.DecisionsInbox.Answered);
            Assert.Single(snap.Projects[0].ResumableCheckpoints);
            Assert.Equal(2, snap.Projects[0].OpenDecisions.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EmptyEntry_And_EntryAccess_IsolateInstancesAndPreserveMissingState()
    {
        var first = OrchestratorState.EmptyEntryObject();
        var second = OrchestratorState.EmptyEntryObject();
        ((JsonArray)first["plan"]!).Add("step-1");
        Assert.Empty((JsonArray)second["plan"]!);

        var state = OrchestratorState.Load(null);
        Assert.Null(state.TryGetEntry("missing"));
        Assert.False(((JsonObject)state.Store["projects"]!).ContainsKey("missing"));

        var created = state.Entry("alpha");
        Assert.Equal("idle", created.Status);
        Assert.NotNull(state.TryGetEntry("alpha"));
    }

    [Fact]
    public void StepOf_NullAndEmptyStatus_ReturnIdle()
    {
        Assert.Equal("idle", OrchestratorState.StepOf((ProjectEntry?)null));
        Assert.Equal("idle", OrchestratorState.StepOf((JsonObject?)null));
        Assert.Equal("idle", OrchestratorState.StepOf(OrchestratorState.EmptyEntry() with { Status = "" }));
    }

    [Theory]
    [InlineData(null, false, false, "check", "todo")]
    [InlineData("blocked_no_folder", false, false, "check", "now")]
    [InlineData("propose_grill", false, true, "grill", "now")]
    [InlineData("waiting_on_you", false, false, "grill", "now")]
    [InlineData("idle", true, false, "plan", "now")]
    [InlineData("blocked_no_worker", false, true, "assign", "now")]
    [InlineData("watching", false, true, "watch", "now")]
    [InlineData("checkpointed", false, false, "watch", "now")]
    [InlineData("failed", false, false, "watch", "now")]
    [InlineData("complete", false, true, "watch", "done")]
    public void PipelineOf_JsonAndTyped_CoverAllStagesAndGrillSkipRules(
        string? status, bool pendingAutoStart, bool grillSkipped, string focusStep, string expectedState)
    {
        JsonObject? obj = status is null ? null : OrchestratorState.EmptyEntryObject();
        if (obj is not null)
        {
            obj["status"] = status;
            obj["grillSkipped"] = grillSkipped;
            if (pendingAutoStart) obj["pendingAutoStart"] = new JsonObject();
        }
        var entry = status is null
            ? null
            : OrchestratorState.EmptyEntry() with
            {
                Status = status,
                GrillSkipped = grillSkipped,
                PendingAutoStart = pendingAutoStart ? new PendingAutoStart() : null
            };

        foreach (var pipeline in new[] { OrchestratorState.PipelineOf(obj), OrchestratorState.PipelineOf(entry) })
        {
            Assert.Equal(6, pipeline.Count);
            Assert.Equal(expectedState, pipeline.Single(p => p.Id == focusStep).State);
            if (status is "propose_grill" or "waiting_on_you")
            {
                Assert.Equal("now", pipeline.Single(p => p.Id == "grill").State);
            }
            else if (grillSkipped && status is "blocked_no_worker" or "watching" or "complete")
            {
                Assert.Equal("skip", pipeline.Single(p => p.Id == "grill").State);
            }
        }
    }

    [Fact]
    public void CrewOf_TypedAndJsonOverloads_CoverRolesStatusesAndFallbacks()
    {
        // Typed overload with ExecutionState SubTasks and various statuses
        var entry = OrchestratorState.EmptyEntry() with
        {
            Status = "watching",
            WorkerActivity = "",
            Dispatch = [new DispatchPart("m1", "Worker One", "Implement API", "why", Pid: 321, LogPath: null, ResultPath: null, StoppedByUser: false)]
        };
        var exec = new ExecutionState(SubTasks: [new JsonObject { ["id"] = "s1", ["type"] = "AUDIT" }]);
        var watchingCrew = OrchestratorState.CrewOf(entry, "Lead One", exec);
        Assert.Equal("Orchestrator", watchingCrew[0].Role);
        Assert.Equal("Lead One", watchingCrew[0].Name);
        Assert.Equal("Reviewer", watchingCrew[1].Role);
        Assert.Contains("Process 321 running.", watchingCrew[1].Doing);

        Assert.Equal("Evidence complete.", OrchestratorState.CrewOf(entry with { Status = "complete" }, null)[1].Doing);
        Assert.Equal("No remaining provider usage.", OrchestratorState.CrewOf(entry with { Status = "blocked_no_worker" }, null)[1].Doing);
        Assert.Contains("checkpoint", OrchestratorState.CrewOf(entry with { Status = "checkpointed" }, null)[1].Doing, StringComparison.OrdinalIgnoreCase);

        var singleLabel = OrchestratorState.EmptyEntry() with { Status = "watching", WorkerLabel = "Solo", WorkerPid = 44, WorkerAlive = false };
        Assert.Equal("Process 44 stopped.", OrchestratorState.CrewOf(singleLabel, null)[1].Doing);

        var multiLabel = OrchestratorState.EmptyEntry() with { Status = "watching", WorkerLabel = "A + B" };
        Assert.Single(OrchestratorState.CrewOf(multiLabel, null));

        var grillRow = OrchestratorState.EmptyEntry() with { Status = "propose_grill" };
        Assert.Equal("Not assigned until grill is closed.", OrchestratorState.CrewOf(grillRow, null)[1].Doing);
        Assert.Equal("Not assigned yet.", OrchestratorState.CrewOf((ProjectEntry?)null, null)[1].Doing);

        // JsonObject overload with historical assignment and watching fallbacks
        var jsonRow = OrchestratorState.EmptyEntryObject();
        jsonRow["status"] = "watching";
        jsonRow["dispatch"] = new JsonArray(
            new JsonObject { ["label"] = "Hist", ["historicalAssignment"] = true, ["assignmentJobTitle"] = "Old Job", ["activity"] = "Done earlier" },
            new JsonObject { ["modelId"] = "m2", ["slice"] = "Core", ["pid"] = 55, ["alive"] = false },
            new JsonObject { ["modelId"] = "m3", ["slice"] = "Docs" });
        var jsonCrew = OrchestratorState.CrewOf(jsonRow, null);
        Assert.Equal("Old Job: Done earlier", jsonCrew[1].Doing);
        Assert.Equal("Core. Process 55 stopped.", jsonCrew[2].Doing);
        Assert.Equal("Docs. Claimed.", jsonCrew[3].Doing);
    }

    [Fact]
    public void ProgressKey_And_LogStat_CoverAllOverloadsAndRealLogFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-prog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("0|0", OrchestratorState.LogStat(null));
            Assert.Equal("0|0", OrchestratorState.LogStat(Path.Combine(dir, "missing.log")));

            var logFile = Path.Combine(dir, "worker.log");
            File.WriteAllText(logFile, "hello");
            var stat = OrchestratorState.LogStat(logFile);
            Assert.StartsWith("5|", stat, StringComparison.Ordinal);

            var facts = new InspectionFacts(Git: false, LastCommit: "", LastSubject: "", Recent: [], Dirty: ["a.cs", "b.cs"], Head: "", Branch: "", FileCount: 12, Folder: dir);
            var keyTypedSingle = OrchestratorState.ProgressKey(facts, logFile, null);
            Assert.Equal($"12||2|{stat}", keyTypedSingle);

            var dispatch = new List<DispatchPart> { new("m1", "M1", "s1", "", LogPath: logFile) };
            var factsObj = new JsonObject { ["fileCount"] = 12, ["names"] = new JsonArray("src"), ["dirty"] = new JsonArray("a.cs") };
            var keyObjList = OrchestratorState.ProgressKey(factsObj, null, dispatch, f => f == logFile ? "custom" : "0|0");
            Assert.Equal("12|src|1|custom", keyObjList);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DefaultInspect_CoversEmptyInvalidFileNameClippingAndGitMetadata()
    {
        Assert.Equal("no folder", OrchestratorState.DefaultInspectJson(null)["error"]?.GetValue<string>());
        Assert.Equal("no folder", OrchestratorState.DefaultInspectJson("")["error"]?.GetValue<string>());

        var root = Path.Combine(Path.GetTempPath(), "planwharf-inspect-extra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plainFile = Path.Combine(root, "plain.txt");
            File.WriteAllText(plainFile, "content");
            Assert.Equal("not a directory", OrchestratorState.DefaultInspectJson(plainFile)["error"]?.GetValue<string>());

            var manyDir = Path.Combine(root, "many");
            Directory.CreateDirectory(manyDir);
            File.WriteAllText(Path.Combine(manyDir, "README.md"), "# Doc\n");
            for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(manyDir, $"f-{i:D2}.txt"), "x");
            var manyJson = OrchestratorState.DefaultInspectJson(manyDir);
            Assert.Equal(31, manyJson["fileCount"]?.GetValue<int>());
            Assert.Equal(24, manyJson["names"]!.AsArray().Count);
            Assert.Contains("README.md", manyJson["docs"]!.AsArray().Select(n => n!.GetValue<string>()));

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            RunGit(repo, "init", "-q");
            RunGit(repo, "config", "user.name", "Fixture");
            RunGit(repo, "config", "user.email", "fixture@example.invalid");
            for (var i = 1; i <= 6; i++)
            {
                File.WriteAllText(Path.Combine(repo, $"long-file-name-for-diffstat-clipping-{i:D2}-abcdefghijklmnopqrstuvwxyz.txt"), $"v{i}\n");
                RunGit(repo, "add", ".");
                RunGit(repo, "commit", "-qm", $"Commit {i}");
            }
            for (var i = 1; i <= 6; i++)
            {
                File.WriteAllText(Path.Combine(repo, $"long-file-name-for-diffstat-clipping-{i:D2}-abcdefghijklmnopqrstuvwxyz.txt"), $"changed-{i}\n");
            }
            var inspected = OrchestratorState.DefaultInspect(repo);
            Assert.True(inspected.Git);
            Assert.False(string.IsNullOrEmpty(inspected.Branch));
            Assert.False(string.IsNullOrEmpty(inspected.Head));
            Assert.Equal("Commit 6", inspected.LastSubject);
            Assert.Equal(5, inspected.Recent!.Count);
            var jsonInspected = OrchestratorState.DefaultInspectJson(repo);
            Assert.False(string.IsNullOrEmpty(jsonInspected["commitId"]?.GetValue<string>()));
            var diffStat = jsonInspected["diffStat"]?.GetValue<string>() ?? "";
            Assert.True(diffStat.Length <= 400);
            Assert.EndsWith("…", diffStat, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Load_CorruptJsonNullDirAndOptionalStores_RecoverCleanly()
    {
        var memoryOnly = OrchestratorState.Load(null);
        memoryOnly.EntryRow("in-mem")["status"] = "watching";
        memoryOnly.Persist();
        Assert.Null(memoryOnly.StateFile);

        var dir = Path.Combine(Path.GetTempPath(), "planwharf-load-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stateFile = Path.Combine(dir, "orchestrator.json");
            File.WriteAllText(stateFile, "{ not valid json");
            var corruptState = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(SessionId: "clean-sess"));
            Assert.Equal("manual", corruptState.Store["mode"]?.GetValue<string>());
            Assert.Empty((JsonObject)corruptState.Store["projects"]!);

            File.WriteAllText(stateFile, """
            {
                "autoSwitch": { "enabled": true },
                "notLead": ["m1", 123, "m2"],
                "workerHistory": [
                    { "id": "alpha", "dispatch": [ { "sessionId": "hist-sess-1", "startedAt": "2026-11-20T09:00:00.000Z" } ] }
                ]
            }
            """);
            var repaired = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(SessionId: "cur-sess"));
            Assert.True(repaired.Store["autoSwitch"]?["enabled"]?.GetValue<bool>());
            Assert.Equal(["m1", "m2"], ((JsonArray)repaired.Store["notLead"]!).Select(n => n!.GetValue<string>()).ToArray());
            var sessionIds = ((JsonArray)repaired.Store["sessions"]!).OfType<JsonObject>().Select(s => s["id"]?.GetValue<string>()).ToArray();
            Assert.Contains("hist-sess-1", sessionIds);
            Assert.Equal("cur-sess", sessionIds[^1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Persist_ExhaustedSharingViolationRetries_CleansTempFilesAndPreservesOriginal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-persist-exhaust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stateFile = Path.Combine(dir, "orchestrator.json");
            File.WriteAllText(stateFile, "{\"kept\":\"original\"}\n");
            var originalBytes = File.ReadAllBytes(stateFile);
            var attempts = 0;
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Rename: (_, _) =>
                {
                    attempts++;
                    throw new IOException("Persistent sharing violation", unchecked((int)0x80070020));
                }));
            state.EntryRow("alpha")["doing"] = "updated";

            Assert.Throws<IOException>(() => state.Persist());
            Assert.True(attempts > 1);
            Assert.Equal(originalBytes, File.ReadAllBytes(stateFile));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void HydrateCheckpoints_BackfillsPathIgnoresCorruptDiskAndPreservesComplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-cp-edge-" + Guid.NewGuid().ToString("N"));
        var cpDir = Path.Combine(dir, "checkpoints");
        Directory.CreateDirectory(cpDir);
        try
        {
            var alphaCp = Path.Combine(cpDir, "alpha.json");
            File.WriteAllText(alphaCp, "{\"jobTitle\":\"Alpha Work\",\"reason\":\"shutdown\"}");
            File.WriteAllText(Path.Combine(cpDir, "done.json"), "{\"jobTitle\":\"Done Work\",\"reason\":\"shutdown\"}");
            File.WriteAllText(Path.Combine(cpDir, "corrupt.json"), "{broken");
            File.WriteAllText(Path.Combine(dir, "orchestrator.json"), """
            {
                "projects": {
                    "alpha": { "status": "idle", "checkpoint": { "jobTitle": "Alpha Work", "reason": "shutdown" } },
                    "done": { "status": "complete" }
                }
            }
            """);

            var state = OrchestratorState.Load(dir);
            Assert.Equal(alphaCp, state.EntryRow("alpha")["checkpoint"]?["path"]?.GetValue<string>());
            Assert.Equal("checkpointed", state.EntryRow("alpha")["status"]?.GetValue<string>());
            Assert.Equal("complete", state.EntryRow("done")["status"]?.GetValue<string>());
            Assert.Null(state.EntryRow("corrupt")["checkpoint"]);
            Assert.Equal("idle", state.EntryRow("corrupt")["status"]?.GetValue<string>());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RefreshResearch_CoversLogTailTruncationNonZeroExitAndV2Fallbacks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-res-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var longLog = Path.Combine(dir, "long.log");
            File.WriteAllText(longLog, new string('x', 2000) + "\r\nend-marker\r\n");
            var nonZeroResult = Path.Combine(dir, "nonzero.txt");
            File.WriteAllText(nonZeroResult, "STATUS: COMPLETE\nEXIT: 2\n");
            var nowCalls = 0;
            var times = new[] { "2026-11-27T10:00:00.000Z", "2026-11-27T10:01:00.000Z", "2026-11-27T10:02:00.000Z" };
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => times[Math.Min(nowCalls++, times.Length - 1)],
                IsProcessAlive: _ => false));

            var v1Tail = new JsonObject { ["id"] = "v1-tail", ["executionVersion"] = 1, ["status"] = "watching", ["logPath"] = longLog };
            var v1BadExit = new JsonObject { ["id"] = "v1-exit2", ["executionVersion"] = 1, ["status"] = "watching", ["pid"] = 99, ["resultPath"] = nonZeroResult };
            var v2Empty = new JsonObject { ["id"] = "v2-empty", ["executionVersion"] = 2, ["megaTaskId"] = "mt-missing", ["status"] = "watching" };
            state.Store["execution"] = new JsonObject { ["results"] = new JsonArray(new JsonObject { ["megaTaskId"] = "mt-missing", ["reportPath"] = Path.Combine(dir, "gone.md") }) };
            ((JsonArray)state.Store["researches"]!).Add(v1Tail);
            ((JsonArray)state.Store["researches"]!).Add(v1BadExit);
            ((JsonArray)state.Store["researches"]!).Add(v2Empty);

            state.RefreshResearch();
            var tail = v1Tail["workerTail"]!.GetValue<string>();
            Assert.True(tail.Length <= 1600);
            Assert.DoesNotContain("\r", tail, StringComparison.Ordinal);
            Assert.EndsWith("end-marker", tail, StringComparison.Ordinal);
            Assert.Equal("watching", v1Tail["status"]?.GetValue<string>());
            var updatedAfterTail = v1Tail["updatedAt"]!.GetValue<string>();
            state.RefreshResearch();
            Assert.Equal(updatedAfterTail, v1Tail["updatedAt"]!.GetValue<string>());

            Assert.Equal("failed", v1BadExit["status"]?.GetValue<string>());
            Assert.Contains("without successful completion evidence", v1BadExit["reason"]?.GetValue<string>());
            Assert.False(v2Empty["reportAvailable"]!.GetValue<bool>());
            Assert.Equal("Awaiting MegaTask approval.", v2Empty["activity"]?.GetValue<string>());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ArchiveProjectAsync_PurgesScopedStoresAndRejectsLiveFallbackDispatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planwharf-arch-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = OrchestratorState.Load(dir, new OrchestratorStateCollaborators(
                Now: () => "2026-11-27T11:00:00.000Z",
                IsProcessAlive: pid => pid == "303",
                ReadPlan: _ => Task.FromResult(new NotebookPlan([]))));

            state.EntryRow("alpha")["status"] = "watching";
            state.EntryRow("alpha")["dispatch"] = new JsonArray(new JsonObject { ["pid"] = 303, ["alive"] = false });
            var blocked = await state.ArchiveProjectAsync("alpha", "remove");
            Assert.False(blocked.Ok);
            Assert.Equal("Stop the work on this project first.", blocked.Reason);

            state.EntryRow("alpha")["status"] = "complete";
            state.EntryRow("beta")["status"] = "idle";
            state.Store["thinkingByKey"] = new JsonObject { ["alpha"] = new JsonObject { ["draft"] = "a" }, ["beta"] = new JsonObject { ["draft"] = "b" } };
            state.Store["briefingBaselines"] = new JsonObject { ["alpha"] = new JsonObject { ["ok"] = true }, ["beta"] = new JsonObject { ["ok"] = true } };
            state.Store["workerHistory"] = new JsonArray(new JsonObject { ["id"] = "alpha" }, new JsonObject { ["id"] = "beta" });
            state.Store["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray(new JsonObject { ["id"] = "m-a", ["projectId"] = "alpha" }, new JsonObject { ["id"] = "m-b", ["projectId"] = "beta" }),
                ["subTasks"] = new JsonArray(),
                ["results"] = new JsonArray(),
                ["checkpoints"] = new JsonArray(new JsonObject { ["id"] = "c-a", ["projectId"] = "alpha" }, new JsonObject { ["id"] = "c-b", ["projectId"] = "beta" }),
                ["timeline"] = new JsonArray(new JsonObject { ["id"] = "t-a", ["projectId"] = "alpha" }, new JsonObject { ["id"] = "t-b", ["projectId"] = "beta" })
            };

            var res = await state.ArchiveProjectAsync("alpha", "cleanup");
            Assert.True(res.Ok);
            Assert.Null(state.Store["projectArchive"]![0]!["notebook"]);
            Assert.False(((JsonObject)state.Store["thinkingByKey"]!).ContainsKey("alpha"));
            Assert.True(((JsonObject)state.Store["thinkingByKey"]!).ContainsKey("beta"));
            Assert.False(((JsonObject)state.Store["briefingBaselines"]!).ContainsKey("alpha"));
            Assert.True(((JsonObject)state.Store["briefingBaselines"]!).ContainsKey("beta"));
            Assert.Single((JsonArray)state.Store["workerHistory"]!);
            Assert.Single((JsonArray)state.Store["execution"]!["checkpoints"]!);
            Assert.Single((JsonArray)state.Store["execution"]!["timeline"]!);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task SetFocusAsync_And_Snapshot_IsolateStoreFallbackAndTeamFocus()
    {
        var state = OrchestratorState.Load(null);
        state.EntryRow("alpha")["doing"] = "alpha-work";
        state.EntryRow("beta")["doing"] = "beta-work";

        Assert.True((await state.SetFocusAsync("alpha")).Ok);
        var missing = await state.SetFocusAsync("missing");
        Assert.False(missing.Ok);
        Assert.Equal("alpha", missing.SelectedProjectId);
        var empty = await state.SetFocusAsync(" ");
        Assert.False(empty.Ok);
        Assert.Equal("alpha", empty.SelectedProjectId);

        state.Store["models"] = new JsonArray(
            new JsonObject { ["id"] = "lead-a", ["label"] = "Lead A", ["enabled"] = true, ["available"] = true, ["roles"] = new JsonObject { ["orchestrator"] = true } },
            new JsonObject { ["id"] = "lead-b", ["label"] = "Lead B", ["enabled"] = true, ["available"] = true, ["roles"] = new JsonObject { ["orchestrator"] = true } });
        state.Store["projectTeams"] = new JsonObject
        {
            ["alpha"] = new JsonObject { ["orchestratorId"] = "lead-a", ["roles"] = new JsonObject() },
            ["beta"] = new JsonObject { ["orchestratorId"] = "lead-b", ["roles"] = new JsonObject() }
        };
        state.Store["projectTools"] = new JsonObject
        {
            ["alpha"] = new JsonObject { ["default"] = new JsonObject { ["Read"] = true } },
            ["beta"] = new JsonObject { ["default"] = new JsonObject { ["Edit"] = true } }
        };
        state.Store["thinkingByKey"] = new JsonObject
        {
            ["alpha"] = new JsonObject { ["draft"] = "alpha-thought", ["startedAt"] = "2026-11-27T10:00:00.000Z" },
            ["beta"] = new JsonObject { ["draft"] = "beta-thought", ["startedAt"] = "2026-11-27T10:01:00.000Z" }
        };

        var plan = new NotebookPlan([new("alpha", "Alpha"), new("beta", "Beta")]);
        var snapAlpha = state.Snapshot(plan, "alpha");
        Assert.Equal("lead-a", snapAlpha.SelectedOrchestratorId);
        Assert.Equal("alpha", snapAlpha.TeamProjectId);
        Assert.True(snapAlpha.OwnTeam);
        Assert.Single(snapAlpha.ProjectTools);
        Assert.True(snapAlpha.ProjectTools.ContainsKey("alpha"));
        Assert.Equal("alpha-thought", snapAlpha.Thinking["draft"]!.ToString());

        var snapBeta = state.Snapshot(plan, "beta");
        Assert.Equal("lead-b", snapBeta.SelectedOrchestratorId);
        Assert.Equal("beta", snapBeta.TeamProjectId);
        Assert.Single(snapBeta.ProjectTools);
        Assert.True(snapBeta.ProjectTools.ContainsKey("beta"));
        Assert.Equal("beta-thought", snapBeta.Thinking["draft"]!.ToString());
    }

    [Fact]
    public async Task TickAsync_DecisionsInbox_And_ActivitySessionPage_CoverRemainingPaths()
    {
        var decided = new List<string>();
        var oldTask = new NotebookTask("old-job", "Old", "v1", "running");
        var newTask = new NotebookTask("new-job", "New", "v2", "running");
        var plan = new NotebookPlan([new("alpha", "Alpha", Tasks: [newTask]), new("beta", "Beta", Tasks: [oldTask])]);

        var state = OrchestratorState.Load(null, new OrchestratorStateCollaborators(
            ReadPlan: _ => Task.FromResult(plan)));
        var row = state.EntryRow("alpha");
        row["jobId"] = oldTask.Id;
        row["jobFingerprint"] = OrchestratorState.JobFingerprint(oldTask);
        row["approved"] = true;
        row["holdApproval"] = true;
        row["attemptId"] = "old-att";
        row["modelId"] = "old-model";
        row["plan"] = new JsonArray("old-step");
        row["dispatch"] = new JsonArray(new JsonObject { ["modelId"] = "old-model" });
        row["tried"] = new JsonArray("old-model");
        row["resumeRequested"] = true;
        row["checkpoint"] = new JsonObject { ["jobId"] = oldTask.Id, ["reason"] = "shutdown" };

        await state.TickAsync("alpha");
        Assert.Equal("new-job", row["jobId"]?.GetValue<string>());
        Assert.False(row["approved"]!.GetValue<bool>());
        Assert.False(row["holdApproval"]!.GetValue<bool>());
        Assert.Null(row["attemptId"]);
        Assert.Null(row["modelId"]);
        Assert.Empty((JsonArray)row["plan"]!);
        Assert.Empty((JsonArray)row["dispatch"]!);
        Assert.Empty((JsonArray)row["tried"]!);
        Assert.False(row["resumeRequested"]!.GetValue<bool>());
        Assert.Null(row["checkpoint"]);
        Assert.Equal("needs_approval", row["status"]?.GetValue<string>());
        Assert.Equal("idle", state.Entry("beta").Status);

        var scoped = OrchestratorState.Load(null, new OrchestratorStateCollaborators(
            ReadPlan: _ => Task.FromResult(plan),
            Decide: ctx => decided.Add(ctx.Project.Id)));
        await scoped.TickAsync("beta");
        Assert.Equal(["beta"], decided);

        // DecisionsInbox merges pipelineJobs + execution.decisions and excludes CANCELLED megaTask open decisions
        scoped.Store["pipelineJobs"] = new JsonArray(new JsonObject
        {
            ["id"] = "pipe-1", ["projectId"] = "alpha", ["title"] = "Pipeline", ["status"] = "building",
            ["questions"] = new JsonArray(new JsonObject { ["id"] = "q1", ["text"] = "Pipe Q?", ["breaks"] = true, ["at"] = "2026-11-27T09:00:00.000Z" })
        });
        scoped.Store["execution"] = new JsonObject
        {
            ["megaTasks"] = new JsonArray(new JsonObject { ["id"] = "mt-c", ["projectId"] = "alpha", ["status"] = "CANCELLED" }),
            ["subTasks"] = new JsonArray(),
            ["decisions"] = new JsonArray(new JsonObject { ["id"] = "d1", ["projectId"] = "alpha", ["megaTaskId"] = "mt-c", ["text"] = "Cancelled?", ["breaks"] = false, ["at"] = "2026-11-27T08:00:00.000Z" })
        };
        scoped.EntryRow("alpha")["megaTaskId"] = "mt-c";
        var snap = scoped.Snapshot(plan, "alpha");
        Assert.Equal(2, snap.DecisionsInbox.OpenCount);
        Assert.Equal("pipeline:pipe-1:q1", snap.DecisionsInbox.Open[0].Id);
        Assert.Empty(snap.Projects[0].OpenDecisions);

        // ActivitySessionPageAsync without ReadPlan validates existence and page bounds
        var noPlanState = OrchestratorState.Load(null);
        noPlanState.EntryRow("alpha");
        await Assert.ThrowsAsync<InvalidOperationException>(() => noPlanState.ActivitySessionPageAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noPlanState.ActivitySessionPageAsync("alpha", -1, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noPlanState.ActivitySessionPageAsync("alpha", 0, ActivitySessions.SessionLimit + 1));
        var emptyPage = await noPlanState.ActivitySessionPageAsync("alpha", 0, ActivitySessions.SessionLimit);
        Assert.Equal(0, emptyPage.Total);
    }

    private static string RunGit(string folder, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--no-optional-locks");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var pair in ProjectWorkspace.GitEnvironment()) psi.Environment[pair.Key] = pair.Value;
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
        return stdout.Trim();
    }

    private sealed class RecordingAttemptAdapter(Action<string, string, string>? onClose = null) : IExecutionAttempts
    {
        public RecordingAttemptAdapter(Action onSimpleClose) : this((_, _, _) => onSimpleClose()) { }
        public JsonObject Claim(JsonObject input) => new() { ["ok"] = true, ["attempt"] = new JsonObject { ["id"] = "att-rec", ["modelId"] = input["modelId"]?.DeepClone() } };
        public JsonObject AttachResult(JsonObject input) => new() { ["ok"] = true };
        public JsonObject CloseAttempt(JsonObject input)
        {
            onClose?.Invoke("close", input["attemptId"]?.GetValue<string>() ?? "", input["reason"]?.GetValue<string>() ?? "");
            return new() { ["ok"] = true };
        }
    }
}
