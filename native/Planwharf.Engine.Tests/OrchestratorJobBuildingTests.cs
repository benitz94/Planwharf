using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class OrchestratorJobBuildingTests
{
    private sealed class FakeGitOperations : IGitOperations
    {
        public List<(string Folder, IReadOnlyList<string> Args)> Commands { get; } = [];
        public List<string> SweptProjects { get; } = [];
        public Func<string, IReadOnlyList<string>, PlanGitResult>? Handler { get; set; }
        public Action<string>? SweepHandler { get; set; }

        public PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000)
        {
            Commands.Add((folder, args.ToArray()));
            if (Handler != null) return Handler(folder, args);
            if (args.Count >= 2 && args[0] == "rev-parse" && args[1] == "--is-inside-work-tree")
                return new(false);
            return new(true);
        }

        public void SweepOrphans(string projectId)
        {
            SweptProjects.Add(projectId);
            SweepHandler?.Invoke(projectId);
        }
    }

    private sealed class FakeExecutionBridge : IExecutionBridge
    {
        public List<(string ProjectId, string Type, string Summary)> Events { get; } = [];
        public List<(ProjectItem Item, JobDescriptor Job, IReadOnlyList<DispatchPart> Parts)> Advances { get; } = [];
        public List<(string ProjectId, string MegaTaskId, IReadOnlyList<SubTaskProposal> Parts, string? Why)> Replans { get; } = [];
        public List<(string ProjectId, string MegaTaskId, IReadOnlyList<string> Assignments, string? Why)> Reassigns { get; } = [];
        public Func<string, string, string?>? StatusLookup { get; set; }
        public Func<ProjectItem, JobDescriptor, IReadOnlyList<DispatchPart>, string>? AdvanceHandler { get; set; }
        public Func<string, string, IReadOnlyList<SubTaskProposal>>? SubTasksHandler { get; set; }
        public Action<string, string, IReadOnlyList<SubTaskProposal>, string?>? ReplanHandler { get; set; }
        public Action<string, string, IReadOnlyList<string>, string?>? ReassignHandler { get; set; }

        public void Event(string projectId, string type, string summary) => Events.Add((projectId, type, summary));
        public string? FindStatus(string projectId, string jobId) => StatusLookup?.Invoke(projectId, jobId);

        public string Advance(ProjectItem item, JobDescriptor job, IReadOnlyList<DispatchPart> parts)
        {
            Advances.Add((item, job, parts));
            return AdvanceHandler?.Invoke(item, job, parts) ?? "running";
        }

        public void Replan(string projectId, string megaTaskId, IReadOnlyList<SubTaskProposal> parts, string? why)
        {
            Replans.Add((projectId, megaTaskId, parts, why));
            ReplanHandler?.Invoke(projectId, megaTaskId, parts, why);
        }

        public void Reassign(string projectId, string megaTaskId, IReadOnlyList<string> assignments, string? why)
        {
            Reassigns.Add((projectId, megaTaskId, assignments, why));
            ReassignHandler?.Invoke(projectId, megaTaskId, assignments, why);
        }

        public IReadOnlyList<SubTaskProposal> SubTasks(string projectId, string megaTaskId)
            => SubTasksHandler?.Invoke(projectId, megaTaskId) ?? [];
    }

    private sealed class FakeCrewSelector : ICrewSelector
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ModelCandidate> Workers { get; set; } = [];
        public Func<JobDescriptor, InspectionFacts?, IReadOnlyList<ModelCandidate>>? Handler { get; set; }

        public IReadOnlyList<ModelCandidate> PickWorkers(JobDescriptor job, InspectionFacts? facts)
        {
            Calls++;
            return Handler != null ? Handler(job, facts) : Workers;
        }
    }

    private sealed class FakeFolderInspector : IFolderInspector
    {
        public int Calls { get; private set; }
        public InspectionFacts Facts { get; set; } = new(Git: true, LastCommit: "abc1234 feat: base", Branch: "main");
        public Func<string, InspectionFacts>? Handler { get; set; }

        public InspectionFacts Inspect(string folder)
        {
            Calls++;
            return Handler != null ? Handler(folder) : Facts;
        }
    }

    private sealed class FakeProcessLiveness : IProcessLiveness
    {
        public HashSet<int> AlivePids { get; } = [];
        public bool IsAlive(int pid) => AlivePids.Contains(pid);
    }

    private sealed class FakeLeadModelAdapter : ILeadModelAdapter
    {
        public ModelCandidate? LeadModel { get; set; }
        public List<string> Questions { get; } = [];
        public Func<string, ModelCandidate, string, string?, CancellationToken, Task<string?>>? Handler { get; set; }

        public Task<string?> AskAsync(string projectId, ModelCandidate model, string question, string? folder, CancellationToken cancellationToken)
        {
            Questions.Add(question);
            return Handler != null ? Handler(projectId, model, question, folder, cancellationToken) : Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeAutonomyOperations : IAutonomyOperations
    {
        public IReadOnlyList<ModelCandidate> RosterModels { get; set; } = [];
        public Func<ProjectItem, IReadOnlyList<ModelCandidate>, IReadOnlyList<ModelCandidate>, string>? SplitPromptHandler { get; set; }
        public Func<string?, IReadOnlyList<ModelCandidate>, IReadOnlyList<ModelCandidate>, IReadOnlyList<SubTaskProposal>?>? ParseSplitHandler { get; set; }
        public Func<ProjectItem, IReadOnlyList<SubTaskProposal>, IReadOnlyList<ModelCandidate>, string>? AssignmentPromptHandler { get; set; }
        public Func<string?, IReadOnlyList<SubTaskProposal>, IReadOnlyList<ModelCandidate>, IReadOnlyList<string>?>? ParseAssignmentHandler { get; set; }

        public IReadOnlyList<ModelCandidate> Roster(string projectId) => RosterModels;
        public string SplitPrompt(ProjectItem item, IReadOnlyList<ModelCandidate> builders, IReadOnlyList<ModelCandidate> verifiers)
            => SplitPromptHandler?.Invoke(item, builders, verifiers) ?? "split prompt";
        public IReadOnlyList<SubTaskProposal>? ParseSplit(string? response, IReadOnlyList<ModelCandidate> builders, IReadOnlyList<ModelCandidate> verifiers)
            => ParseSplitHandler?.Invoke(response, builders, verifiers);
        public string AssignmentPrompt(ProjectItem item, IReadOnlyList<SubTaskProposal> subs, IReadOnlyList<ModelCandidate> roster)
            => AssignmentPromptHandler?.Invoke(item, subs, roster) ?? "assign prompt";
        public IReadOnlyList<string>? ParseAssignment(string? response, IReadOnlyList<SubTaskProposal> subs, IReadOnlyList<ModelCandidate> roster)
            => ParseAssignmentHandler?.Invoke(response, subs, roster);
    }

    private sealed class FakeProjectReader : IProjectReader
    {
        public ProjectItem? Item { get; set; }
        public Task<ProjectItem?> ReadAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(Item);
    }

    private sealed class FakeWorkPlanManager : IWorkPlanManager
    {
        public WorkPlanResult Result { get; set; } = new(true);
        public List<(string ProjectId, ProjectItem Item, IReadOnlyList<PlanStepEntry>? Steps)> Calls { get; } = [];

        public WorkPlanResult Open(string projectId, ProjectItem item, IReadOnlyList<PlanStepEntry>? steps)
        {
            Calls.Add((projectId, item, steps));
            return Result;
        }
    }

    private sealed class FakeGrillComposer : IGrillComposer
    {
        public IReadOnlyList<string>? Questions { get; set; }
        public Exception? ThrowError { get; set; }

        public Task<IReadOnlyList<string>?> ComposeAsync(ProjectItem item, InspectionFacts facts, CancellationToken cancellationToken)
        {
            if (ThrowError != null) throw ThrowError;
            return Task.FromResult(Questions);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "planwharf-jobbuilding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    // 1. JobFromFacts_GoalPropagation_Tests
    [Fact]
    public void JobFromFacts_PreservesGoalInTitleAndDetailUpTo4000CharsInGitRepo()
    {
        var longGoal = new string('a', 4100);
        var revision = new Revision("rev-1", $"  {longGoal}  ");
        var facts = new InspectionFacts(
            Git: true,
            LastCommit: "deadbeef Initial commit",
            LastSubject: "Initial commit",
            Recent: ["c1", "c2", "c3", "c4", "c5", "c6"],
            Dirty: Enumerable.Range(1, 15).Select(i => $" M file{i}.cs").ToArray(),
            Branch: "main"
        );

        var job = OrchestratorJobBuilding.JobFromFacts(revision, facts);

        Assert.Equal(120, job.Title.Length);
        Assert.EndsWith("…", job.Title);
        var sections = job.Detail.Split("\n\n");
        Assert.Equal("Goal: " + new string('a', 3999) + "…", sections[0]);
        Assert.Equal("Latest commit: deadbeef Initial commit", sections[1]);
        Assert.Equal("Recent:\nc1\nc2\nc3\nc4\nc5", sections[2]);
        Assert.DoesNotContain("c6", sections[2]);
        Assert.Equal("Uncommitted:\n" + string.Join("\n", Enumerable.Range(1, 12).Select(i => $" M file{i}.cs")), sections[3]);
        Assert.DoesNotContain("file13.cs", sections[3]);
        Assert.Equal("Work toward the goal. Read only what that needs.", sections[4]);
    }

    [Fact]
    public void JobFromFacts_FallbacksWhenGoalAbsentAndCleanRepo()
    {
        var fromSubject = OrchestratorJobBuilding.JobFromFacts(
            new Revision("rev-1", "   "),
            new InspectionFacts(Git: true, LastSubject: "Refactor parser", Dirty: []));
        Assert.Equal("Continue: Refactor parser", fromSubject.Title);
        Assert.Contains("Git present.", fromSubject.Detail);
        Assert.Contains("Working tree clean.", fromSubject.Detail);
        Assert.Contains("Read the repo, take the latest changes, and proceed with the notebook plan.", fromSubject.Detail);

        var noSubject = OrchestratorJobBuilding.JobFromFacts(null, new InspectionFacts(Git: false));
        Assert.Equal("Continue: latest repo changes", noSubject.Title);
        Assert.Contains("No git history.", noSubject.Detail);
        Assert.Contains("Working tree clean.", noSubject.Detail);
    }

    // 2. JobKind_SliceCount_PlanLines_Tests
    [Theory]
    [InlineData("Hard task", "", "hard")]
    [InlineData("", "Solve difficult race condition", "hard")]
    [InlineData("Escalated issue", "fix bug", "hard")]
    [InlineData("Premium migration", "implement", "hard")]
    [InlineData("Complex state", null, "hard")]
    [InlineData("Implement feature", "", "build")]
    [InlineData("Write unit test", "", "build")]
    [InlineData("Fix bug in code", "", "build")]
    [InlineData("Build module", "", "build")]
    [InlineData("Review documentation", "check status", "inspect")]
    [InlineData(null, null, "inspect")]
    public void JobKind_ClassifiesDeterministically(string? title, string? detail, string expected)
    {
        Assert.Equal(expected, OrchestratorJobBuilding.JobKind(title, detail));
        Assert.Equal(expected, OrchestratorJobBuilding.JobKind(new JobDescriptor("j1", title, detail)));
    }

    [Theory]
    [InlineData("Review docs", 0, 0, 1)]
    [InlineData("Review docs", 0, 1, 1)]
    [InlineData("Review docs", 3, 2, 1)]
    [InlineData("Review docs", 4, 2, 2)]
    [InlineData("Review docs", 12, 5, 2)]
    [InlineData("Implement API", 0, 1, 1)]
    [InlineData("Implement API", 0, 2, 2)]
    [InlineData("Implement API", 0, 5, 2)]
    [InlineData("Complex refactor", 0, 1, 1)]
    [InlineData("Complex refactor", 0, 3, 2)]
    public void SliceCount_ComputesAcrossPoolsAndDirtyThresholds(string title, int dirtyCount, int eligible, int expected)
    {
        var job = new JobDescriptor("j1", title, "");
        var facts = new InspectionFacts(Dirty: Enumerable.Range(0, dirtyCount).Select(i => $"f{i}.cs").ToArray());
        Assert.Equal(expected, OrchestratorJobBuilding.SliceCount(job, facts, eligible));
    }

    [Fact]
    public void PlanLines_GeneratesDeterministicLinesForSingleAndMultiSlice()
    {
        var item = new ProjectItem(
            new ProjectInfo("p1", "App", "/repo"),
            [new Revision("r1", "Ship   the   feature")],
            [new JobDescriptor("j1", "Build", "Details", Timing: "deadline", DeadlineAt: "2026-12-01T10:00:00.000Z") { Status = "running" }]
        );
        var gitFacts = new InspectionFacts(Git: true, LastCommit: "abc1234", Branch: "main", Dirty: ["a.cs", "b.cs"]);
        var shown = new ModelCandidate("m1", "Model One", "strong coder");

        var singleLines = OrchestratorJobBuilding.PlanLines(item, gitFacts, shown, active: true,
            new PlanExtras(Checkpoint: new CheckpointRecord("chk/1.json")));
        Assert.Equal(new[]
        {
            "Stay inside /repo.",
            "Notebook goal: Ship the feature",
            "Latest git: abc1234 on main.",
            "Uncommitted: 2 path(s). Read those changes.",
            "Proceed with the plan from those changes.",
            "Assigned Model One (strong coder).",
            "Finish by 2026-12-01T10:00:00.000Z. At that time the orchestrator delivers the output or writes a checkpoint.",
            "Resume from checkpoint chk/1.json.",
            "Watch the worker process, the result file (STATUS: COMPLETE), and the project folder."
        }, singleLines);

        var multiDispatch = new[]
        {
            new DispatchPart("m1", "Alpha", "Build module"),
            new DispatchPart("m2", "", "Verify module")
        };
        var nonGitItem = new ProjectItem(new ProjectInfo("p1", "App", "/plain"), []);
        var multiLines = OrchestratorJobBuilding.PlanLines(
            nonGitItem,
            new InspectionFacts(Git: false, FileCount: 7),
            new ModelCandidate("m1"),
            active: false,
            new PlanExtras(Dispatch: multiDispatch, Job: new JobDescriptor("j2", "T", "D", Timing: "discretion"))
        );
        Assert.Equal(new[]
        {
            "Stay inside /plain.",
            "Read-only inspect (7 top-level names).",
            "Proceed with the plan from those changes.",
            "Split into 2 slices for parallel workers.",
            "Slice 1: Alpha — Build module.",
            "Slice 2: m2 — Verify module.",
            "Timing is at the worker\u2019s discretion.",
            "Watch the worker process, the result file (STATUS: COMPLETE), and the project folder."
        }, multiLines);
    }

    // 3. LegacyPhase1_JobFlow_Tests
    [Fact]
    public void LegacyPhase1_DecideFlowTransitionsIdleBlockedNeedsApprovalAndRunning()
    {
        var bridge = new FakeExecutionBridge();
        var crew = new FakeCrewSelector { Workers = [new ModelCandidate("m1", "Model 1", "fit")] };
        var inspector = new FakeFolderInspector();
        var liveness = new FakeProcessLiveness();
        var opts = new DecideOptions();

        var idleRow = new ProjectRow { HoldApproval = true, Approved = true };
        var idleItem = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [new Revision("r1", "Goal")], []);
        var idle = OrchestratorJobBuilding.Decide(idleItem, idleRow, bridge, crew, inspector, liveness, opts);
        Assert.Equal("idle", idle.Status);
        Assert.False(idle.Row.Approved);
        Assert.False(idle.Row.HoldApproval);

        var runningJob = new JobDescriptor("j1", "Implement feature", "Write code") { Status = "running" };
        var noFolder = OrchestratorJobBuilding.Decide(
            new ProjectItem(new ProjectInfo("p1", "App", null), [], [runningJob]),
            new ProjectRow(), bridge, crew, inspector, liveness, opts);
        Assert.Equal("blocked_no_folder", noFolder.Status);

        var inspectErr = new FakeFolderInspector { Facts = new InspectionFacts(Error: "missing directory") };
        var badFolder = OrchestratorJobBuilding.Decide(
            new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [], [runningJob]),
            new ProjectRow(), bridge, crew, inspectErr, liveness, opts);
        Assert.Equal("blocked_no_folder", badFolder.Status);

        var noCrew = new FakeCrewSelector { Workers = [] };
        var blockedWorker = OrchestratorJobBuilding.Decide(
            new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [], [runningJob]),
            new ProjectRow(), bridge, noCrew, inspector, liveness, opts);
        Assert.Equal("blocked_no_worker", blockedWorker.Status);

        var row = new ProjectRow();
        var item = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [new Revision("r1", "Ship it")], [runningJob]);
        var needsApproval = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, opts);
        Assert.Equal("needs_approval", needsApproval.Status);
        Assert.Single(row.Dispatch);
        Assert.NotEmpty(row.Plan);

        row.Approved = true;
        var running = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, opts);
        Assert.Equal("running", running.Status);
    }

    [Fact]
    public async Task StartAutoTaskAsync_ReleasesRunningJobTriggersGrillAndBuildsProposedPlan()
    {
        var reader = new FakeProjectReader();
        var inspector = new FakeFolderInspector { Facts = new InspectionFacts(Git: true, LastCommit: "sha1 Fix", LastSubject: "Fix") };
        var workPlan = new FakeWorkPlanManager();
        var bridge = new FakeExecutionBridge();
        var grill = new FakeGrillComposer();

        var missing = await OrchestratorJobBuilding.StartAutoTaskAsync("", null, reader, inspector, workPlan, bridge, grill);
        Assert.False(missing.Ok);
        Assert.Equal("project not found", missing.Reason);

        var runningTask = new JobDescriptor("j-old", "Old", "Old") { Status = "running" };
        reader.Item = new ProjectItem(
            new ProjectInfo("p1", "App", "/repo"),
            [new Revision("r1", "Build dashboard", Plan.GenericFrontier)],
            [runningTask]
        );

        var badDeadline = await OrchestratorJobBuilding.StartAutoTaskAsync(
            "p1", new StartAutoTaskOptions(Timing: "deadline", DeadlineAt: "not-a-date"),
            reader, inspector, workPlan, bridge, grill);
        Assert.False(badDeadline.Ok);
        Assert.Equal("deadline required", badDeadline.Reason);

        grill.Questions = ["What scope?"];
        var grillResult = await OrchestratorJobBuilding.StartAutoTaskAsync("p1", null, reader, inspector, workPlan, bridge, grill);
        Assert.True(grillResult.Ok);
        Assert.Equal("grill", grillResult.Phase);
        Assert.True(grillResult.Replace);
        Assert.True(grillResult.ClearJob);
        Assert.Equal(["What scope?"], grillResult.Questions);

        var gold = new ModelCandidate("gold-1", "Gold");
        var planResult = await OrchestratorJobBuilding.StartAutoTaskAsync(
            "p1",
            new StartAutoTaskOptions(Timing: "deadline", DeadlineAt: "2026-12-01T12:30:00+02:00", GrillContext: "Focus on core", GoldModel: gold),
            reader, inspector, workPlan, bridge, grill);
        Assert.True(planResult.Ok);
        Assert.Equal("plan", planResult.Phase);
        Assert.True(planResult.ClearJob);
        Assert.NotNull(planResult.Job);
        Assert.Equal("deadline", planResult.Job!.Timing);
        Assert.Equal("2026-12-01T10:30:00.000Z", planResult.Job.DeadlineAt);
        Assert.Equal("chat", planResult.Job.Source);
        Assert.Equal(gold, planResult.Job.GoldModel);
        Assert.Contains("Grill discussion for this task:\nFocus on core", planResult.Job.Detail);
        Assert.Single(workPlan.Calls);
    }

    // 4. ExecutionV2_Decide_Tests
    [Fact]
    public void ExecutionV2_Decide_TranslatesProposedSubTasksAndSkipsRedundantInspection()
    {
        var tempDir = CreateTempDir();
        try
        {
            var gold = new ModelCandidate("gold-m", "Gold");
            var proposals = new[]
            {
                new SubTaskProposal("m1", "Build slice", "Detail", []) { Type = "IMPLEMENTATION", Title = "Build core" },
                new SubTaskProposal("m2", "Verify slice", "Detail", [0]) { Title = OrchestratorJobBuilding.VerifySliceTitle + ": tests", Label = "Verifier" },
                new SubTaskProposal("m3", "Audit slice", "Detail", [1]) { Type = "AUDIT", AssignedModelId = "m3-assigned" },
                new SubTaskProposal("m4", "Research slice", "Detail", []) { Type = "RESEARCH" }
            };
            var job = new JobDescriptor("j-v2", "Build", "Run v2", ExecutionVersion: 2, Source: "chat", GoldModel: gold, ProposedSubTasks: proposals)
            { Status = "running" };
            var item = new ProjectItem(new ProjectInfo("p1", "App", tempDir), [new Revision("r1", "Goal", ["open question"])], [job]);
            var linked = ProjectWorkspace.LinkedWorkspace(tempDir);
            var cachedFacts = new InspectionFacts(Git: true, Folder: linked, LastCommit: "cached");
            var row = new ProjectRow { Facts = cachedFacts };

            var bridge = new FakeExecutionBridge
            {
                StatusLookup = (_, _) => "COMPLETE",
                AdvanceHandler = (_, _, _) => "complete"
            };
            var crew = new FakeCrewSelector();
            var inspector = new FakeFolderInspector();
            var liveness = new FakeProcessLiveness();

            var outcome = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, new DecideOptions());

            Assert.True(outcome.Advanced);
            Assert.Equal("complete", outcome.Status);
            Assert.Equal(0, inspector.Calls);
            Assert.Equal(0, crew.Calls);
            Assert.Single(bridge.Advances);
            var parts = bridge.Advances[0].Parts;
            Assert.Equal(4, parts.Count);
            Assert.Equal("gold-m", parts[0].ModelId);
            Assert.Equal("Build core", parts[0].Slice);
            Assert.Equal("m2", parts[1].ModelId);
            Assert.Equal("Verifier", parts[1].Label);
            Assert.Equal("m3-assigned", parts[2].ModelId);
            Assert.Equal("m4", parts[3].ModelId);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void ExecutionV2_Decide_FallsBackToCrewSelectorAndHandlesBridgeException()
    {
        var job = new JobDescriptor("j-v2", "Implement app", "Details", ExecutionVersion: 2) { Status = "running" };
        var item = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [new Revision("r1", "Goal")], [job]);
        var bridge = new FakeExecutionBridge
        {
            StatusLookup = (_, _) => "RUNNING",
            AdvanceHandler = (_, _, _) => throw new InvalidOperationException("No worker available")
        };
        var crew = new FakeCrewSelector { Workers = [new ModelCandidate("m1", "M1")] };
        var inspector = new FakeFolderInspector();

        var outcome = OrchestratorJobBuilding.Decide(item, new ProjectRow(), bridge, crew, inspector, new FakeProcessLiveness(), new DecideOptions());

        Assert.False(outcome.Advanced);
        Assert.Equal("blocked_no_worker", outcome.Status);
        Assert.Equal("No worker available", outcome.Error);
        Assert.Equal(1, inspector.Calls);
        Assert.Equal(1, crew.Calls);
    }

    // 6. Decide_CompletionVerification_Tests
    [Fact]
    public void Decide_CompletionVerification_RequiresAllSlicesCompleteAndNeverRedispatches()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result1 = Path.Combine(tempDir, "slice1.txt");
            var result2 = Path.Combine(tempDir, "slice2.txt");
            File.WriteAllText(result1, "Summary\nSTATUS: COMPLETE\n");
            File.WriteAllText(result2, "Working\nSTATUS: WATCHING\n");

            Assert.Equal("COMPLETE", OrchestratorJobBuilding.ResultStatus(result1));
            Assert.Equal("WATCHING", OrchestratorJobBuilding.ResultStatus(result2));
            Assert.Equal("MISSING", OrchestratorJobBuilding.ResultStatus(Path.Combine(tempDir, "none.txt")));
            Assert.Equal("MISSING", OrchestratorJobBuilding.ResultStatus(null));

            var job = new JobDescriptor("j1", "Build", "Details") { Status = "running" };
            var item = new ProjectItem(new ProjectInfo("p1", "App", tempDir), [], [job]);
            var row = new ProjectRow
            {
                JobId = job.Id,
                JobFingerprint = OrchestratorJobBuilding.JobFingerprint(job),
                Approved = true,
                Dispatch =
                [
                    new DispatchPart("m1", "M1", "Build", ResultPath: result1),
                    new DispatchPart("m2", "M2", "Verify", ResultPath: result2)
                ]
            };
            var bridge = new FakeExecutionBridge();
            var crew = new FakeCrewSelector { Workers = [new ModelCandidate("m1", "M1")] };
            var inspector = new FakeFolderInspector();
            var liveness = new FakeProcessLiveness();

            var partial = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, new DecideOptions());
            Assert.Equal("running", partial.Status);
            Assert.Equal(1, inspector.Calls);

            File.WriteAllText(result2, "Verified\nstatus: complete\n");
            var done = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, new DecideOptions());
            Assert.Equal("complete", done.Status);
            Assert.Contains("Evidence complete", done.Row.Doing);
            Assert.Equal(1, inspector.Calls);

            var settled = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, new DecideOptions());
            Assert.Equal("complete", settled.Status);
            Assert.Equal(1, inspector.Calls);
            Assert.Equal(1, crew.Calls);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    // 7. ApprovalState_Transitions_Tests
    [Fact]
    public void ApprovalState_ResetsOnJobChangeAndEnforcesSessionBoundaryUnlessOngoing()
    {
        var job1 = new JobDescriptor("j1", "Build A", "Detail A", RevisionId: "r1") { Status = "running" };
        var job2 = new JobDescriptor("j1", "Build A", "Detail B", RevisionId: "r1") { Status = "running" };
        Assert.NotEqual(OrchestratorJobBuilding.JobFingerprint(job1), OrchestratorJobBuilding.JobFingerprint(job2));
        Assert.Equal("", OrchestratorJobBuilding.JobFingerprint(null));

        var item1 = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [], [job1]);
        var bridge = new FakeExecutionBridge();
        var crew = new FakeCrewSelector { Workers = [new ModelCandidate("m1", "M1")] };
        var inspector = new FakeFolderInspector();
        var liveness = new FakeProcessLiveness();
        liveness.AlivePids.Add(4242);

        var ongoingRow = new ProjectRow
        {
            JobId = job1.Id,
            JobFingerprint = OrchestratorJobBuilding.JobFingerprint(job1),
            Approved = true,
            AuthorizedSessionId = "old-session",
            Dispatch = [new DispatchPart("m1", "M1", "Build", Pid: 4242)]
        };
        var keptRunning = OrchestratorJobBuilding.Decide(
            item1, ongoingRow, bridge, crew, inspector, liveness,
            new DecideOptions(EnforceSessionBoundary: true, SessionId: "new-session"));
        Assert.Equal("running", keptRunning.Status);
        Assert.True(keptRunning.Row.Approved);

        ongoingRow.Dispatch = [new DispatchPart("m1", "M1", "Build", Pid: 9999)];
        var held = OrchestratorJobBuilding.Decide(
            item1, ongoingRow, bridge, crew, inspector, liveness,
            new DecideOptions(EnforceSessionBoundary: true, SessionId: "new-session"));
        Assert.Equal("needs_approval", held.Status);
        Assert.False(held.Row.Approved);
        Assert.True(held.Row.HoldApproval);

        var item2 = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [], [job2]);
        held.Row.Approved = true;
        held.Row.AttemptId = "att-1";
        held.Row.Tried = ["m1"];
        var mutated = OrchestratorJobBuilding.Decide(item2, held.Row, bridge, crew, inspector, liveness, new DecideOptions());
        Assert.Equal("needs_approval", mutated.Status);
        Assert.False(mutated.Row.Approved);
        Assert.False(mutated.Row.HoldApproval);
        Assert.Null(mutated.Row.AttemptId);
        Assert.Empty(mutated.Row.Tried);
        Assert.Contains(bridge.Events, e => e.Summary == "Job changed. Approval reset.");
    }

    // 5. OpenWorkPlan_GitContainment_Tests
    [Fact]
    public void OpenWorkPlan_CreatesPlanInNonGitAndHandlesMissingFolder()
    {
        var git = new FakeGitOperations();
        var bridge = new FakeExecutionBridge();
        var missingItem = new ProjectItem(new ProjectInfo("p1", "App", null), [new Revision("r1", "Goal")]);
        Assert.True(OrchestratorJobBuilding.OpenWorkPlan("p1", missingItem, [], git, bridge).Ok);

        var tempDir = CreateTempDir();
        try
        {
            var item = new ProjectItem(new ProjectInfo("p1", "App", tempDir), [new Revision("r1", "Ship feature")]);
            var steps = new[] { new PlanStepEntry("Build", "Alpha", "Implement core") };
            var res = OrchestratorJobBuilding.OpenWorkPlan("p1", item, steps, git, bridge);

            Assert.True(res.Ok);
            Assert.True(File.Exists(PlanFile.PlanPath(tempDir)));
            var md = File.ReadAllText(PlanFile.PlanPath(tempDir));
            Assert.Contains("Ship feature", md);
            Assert.Contains("Build (Alpha)", md);
            Assert.Empty(git.SweptProjects);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void OpenWorkPlan_PrunesWorktreesSweepsOrphansAndFailsClosedOnGitErrors()
    {
        var tempDir = CreateTempDir();
        try
        {
            var gitDir = Path.Combine(tempDir, ".git");
            Directory.CreateDirectory(Path.Combine(gitDir, "info"));
            Directory.CreateDirectory(Path.Combine(gitDir, "objects"));
            File.WriteAllText(Path.Combine(gitDir, "config"), "[core]\n\trepositoryformatversion = 0\n");

            FakeGitOperations BuildGit(bool failPrune = false, bool throwPrune = false, bool throwSweep = false, bool failPrepare = false) => new()
            {
                Handler = (_, args) =>
                {
                    if (args is ["rev-parse", "--is-inside-work-tree"]) return new(true, "true\n");
                    if (args is ["rev-parse", "--git-path", "info/exclude"]) return new(true, ".git/info/exclude\n");
                    if (args[0] == "status") return new(true, "");
                    if (args is ["fetch", "origin"]) return new(false, Stderr: "offline");
                    if (args is ["rev-parse", "HEAD"]) return new(true, "1234567890abcdef\n");
                    if (args is ["show-ref", "--verify", "--quiet", ..]) return new(true);
                    if (args is ["branch", "--show-current"]) return new(true, "main\n");
                    if (args[0] == "switch") return failPrepare ? new(false, Stderr: "switch denied") : new(true);
                    if (args is ["merge-base", "--is-ancestor", ..]) return new(true);
                    if (args is ["worktree", "prune"])
                    {
                        if (throwPrune) throw new InvalidOperationException("prune exploded");
                        return failPrune ? new(false, Stderr: "prune refused") : new(true);
                    }
                    return new(true);
                },
                SweepHandler = throwSweep ? _ => throw new InvalidOperationException("sweep exploded") : null
            };

            var item = new ProjectItem(new ProjectInfo("p1", "App", tempDir), [new Revision("r1", "Work on branch `feat/test`")]);
            var bridge = new FakeExecutionBridge();

            var okGit = BuildGit();
            var ok = OrchestratorJobBuilding.OpenWorkPlan("p1", item, [], okGit, bridge);
            Assert.True(ok.Ok);
            Assert.Contains("The remote could not be read, so the crew starts on the current commit 1234567.", ok.Chat ?? "");
            Assert.Contains(okGit.Commands, c => c.Args.SequenceEqual(["worktree", "prune"]));
            Assert.Equal(["p1"], okGit.SweptProjects);
            Assert.Single(bridge.Events);

            var pruneFail = OrchestratorJobBuilding.OpenWorkPlan("p1", item, [], BuildGit(failPrune: true), bridge);
            Assert.False(pruneFail.Ok);
            Assert.Contains("Git worktree cleanup failed: prune refused", pruneFail.Reason);

            var pruneThrow = OrchestratorJobBuilding.OpenWorkPlan("p1", item, [], BuildGit(throwPrune: true), bridge);
            Assert.False(pruneThrow.Ok);
            Assert.Contains("Git worktree cleanup failed: prune exploded", pruneThrow.Reason);

            var sweepThrow = OrchestratorJobBuilding.OpenWorkPlan("p1", item, [], BuildGit(throwSweep: true), bridge);
            Assert.False(sweepThrow.Ok);
            Assert.Contains("Git worktree cleanup failed: sweep exploded", sweepThrow.Reason);

            var prepBridge = new FakeExecutionBridge();
            var prepFail = OrchestratorJobBuilding.OpenWorkPlan("p1", item, [], BuildGit(failPrepare: true), prepBridge);
            Assert.False(prepFail.Ok);
            Assert.Contains("Could not switch to feat/test", prepFail.Reason);
            Assert.Single(prepBridge.Events);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    // 8. OpenWorkPlan_PlanSteps_Tests
    [Fact]
    public void PlanSteps_MapsRolesAndResolvesRosterLabels()
    {
        var roster = new[]
        {
            new ModelCandidate("m-rev", "Reviewer Pro"),
            new ModelCandidate("m-bld", "Builder Fast")
        };
        var subs = new[]
        {
            new SubTaskProposal("m-rev", "Audit slice", "D", []) { Type = "AUDIT", Title = "Audit changes" },
            new SubTaskProposal("m-res", "Lookup docs", "D", []) { Type = "RESEARCH", Label = "Researcher Label" },
            new SubTaskProposal("m-ver", OrchestratorJobBuilding.VerifySliceTitle + ": checks", "D", []),
            new SubTaskProposal("m-other", "Implement feature", "D", []) { AssignedModelId = "m-bld" },
            new SubTaskProposal("", "Fallback unassigned", "D", [])
        };

        var steps = OrchestratorJobBuilding.PlanSteps(subs, roster);

        Assert.Equal(5, steps.Count);
        Assert.Equal(new PlanStepEntry("Review", "Reviewer Pro", "Audit changes"), steps[0]);
        Assert.Equal(new PlanStepEntry("Research", "Researcher Label", "Lookup docs"), steps[1]);
        Assert.Equal(new PlanStepEntry("Verify", "m-ver", OrchestratorJobBuilding.VerifySliceTitle + ": checks"), steps[2]);
        Assert.Equal(new PlanStepEntry("Build", "Builder Fast", "Implement feature"), steps[3]);
        Assert.Equal(new PlanStepEntry("Build", "unassigned", "Fallback unassigned"), steps[4]);
    }

    // 9. SavingMode_JobBuilding_Tests
    [Fact]
    public void BuildDispatchPlan_HandlesReviewerSingleCleanDirtyAndMultiWorkerVerifierSlices()
    {
        Assert.Empty(OrchestratorJobBuilding.BuildDispatchPlan([], new JobDescriptor("j1", "T", "D"), null));

        var workers = new[]
        {
            new ModelCandidate("m1", "Alpha", "fast"),
            new ModelCandidate("m2", "Beta", "thorough"),
            new ModelCandidate("m3", "", "extra")
        };
        var reviewJob = new JobDescriptor("j1", "Review", "Check") { Type = "AUDIT" };
        var reviewPlan = OrchestratorJobBuilding.BuildDispatchPlan(workers, reviewJob, null);
        Assert.Equal("Review the approved scope and report findings.", reviewPlan[0].Slice);
        Assert.Equal("Review correctness and edge cases; report findings.", reviewPlan[1].Slice);
        Assert.Equal("Review tests and verification evidence; report findings.", reviewPlan[2].Slice);

        var buildJob = new JobDescriptor("j2", "Implement feature", "Build");
        var single = OrchestratorJobBuilding.BuildDispatchPlan([workers[0]], buildJob, null);
        Assert.Equal("Whole job", single[0].Slice);

        var dirtyFacts = new InspectionFacts(Dirty: ["a.cs"]);
        var multiDirty = OrchestratorJobBuilding.BuildDispatchPlan(workers, buildJob, dirtyFacts);
        Assert.Contains("starting from the uncommitted changes", multiDirty[0].Slice);
        Assert.StartsWith(OrchestratorJobBuilding.VerifySliceTitle, multiDirty[1].Slice);
        Assert.Contains("Needs your decision:", multiDirty[1].Slice);
        Assert.Equal("Parallel slice 3", multiDirty[2].Slice);
        Assert.Equal("m3", multiDirty[2].Label);

        var multiClean = OrchestratorJobBuilding.BuildDispatchPlan(workers[..2], buildJob, new InspectionFacts(Dirty: []));
        Assert.DoesNotContain("uncommitted changes", multiClean[0].Slice);
    }

    [Fact]
    public async Task PlanByLeadAsync_RespectsSavingModePoolsRetriesAndReplan()
    {
        var item = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), [new Revision("r1", "Goal")]);
        var bridge = new FakeExecutionBridge
        {
            SubTasksHandler = (_, _) => [new SubTaskProposal("m1", "Whole job", "D", [])]
        };
        var lead = new FakeLeadModelAdapter { LeadModel = new ModelCandidate("lead", "Lead") };
        var autonomy = new FakeAutonomyOperations
        {
            RosterModels =
            [
                new ModelCandidate("b1", "B1", CanCode: true, CanVerify: false, Available: true),
                new ModelCandidate("b2", "B2", CanCode: true, CanVerify: false, Available: false),
                new ModelCandidate("v1", "V1", CanCode: false, CanVerify: true, Available: true)
            ]
        };

        var blockedBySavingMode = await OrchestratorJobBuilding.PlanByLeadAsync("p1", item, "mt-1", lead, bridge, autonomy);
        Assert.False(blockedBySavingMode);
        Assert.Contains(bridge.Events, e => e.Summary.Contains("saving mode"));

        autonomy.RosterModels =
        [
            new ModelCandidate("b1", "B1", CanCode: true, CanVerify: false),
            new ModelCandidate("b2", "B2", CanCode: true, CanVerify: false),
            new ModelCandidate("v1", "V1", CanCode: false, CanVerify: true)
        ];
        var round = 0;
        lead.Handler = (_, _, _, _, _) =>
        {
            round++;
            return round == 1 ? Task.FromResult<string?>("not json") : Task.FromResult<string?>("{\"parts\":[{\"title\":\"ok\"}]}");
        };
        autonomy.ParseSplitHandler = (spoken, _, _) => spoken != null && spoken.Contains("parts")
            ? [new SubTaskProposal("b1", "Part 1", "D1", []), new SubTaskProposal("v1", OrchestratorJobBuilding.VerifySliceTitle, "D2", [0])]
            : null;

        var splitOk = await OrchestratorJobBuilding.PlanByLeadAsync("p1", item, "mt-1", lead, bridge, autonomy);
        Assert.True(splitOk);
        Assert.Equal(2, lead.Questions.Count);
        Assert.Contains("Your previous reply could not be used", lead.Questions[1]);
        Assert.Single(bridge.Replans);
        Assert.Equal(2, bridge.Replans[0].Parts.Count);
    }

    [Fact]
    public async Task AssignByLeadAsync_HandlesReassignUnreadableAndRefusal()
    {
        var item = new ProjectItem(new ProjectInfo("p1", "App", "/repo"), []);
        var subs = new[]
        {
            new SubTaskProposal("b1", "Part 1", "D1", []),
            new SubTaskProposal("v1", "Verify", "D2", [0])
        };
        var bridge = new FakeExecutionBridge { SubTasksHandler = (_, _) => subs };
        var lead = new FakeLeadModelAdapter
        {
            LeadModel = new ModelCandidate("lead", "Lead"),
            Handler = (_, _, _, _, _) => Task.FromResult<string?>("[\"b2\",\"v1\"]")
        };
        var autonomy = new FakeAutonomyOperations
        {
            RosterModels = [new ModelCandidate("b1"), new ModelCandidate("b2"), new ModelCandidate("v1")],
            ParseAssignmentHandler = (s, _, _) => s == "[\"b2\",\"v1\"]" ? ["b2", "v1"] : null
        };

        await OrchestratorJobBuilding.AssignByLeadAsync("p1", item, "mt-1", lead, bridge, autonomy);
        Assert.Single(bridge.Reassigns);
        Assert.Equal(["b2", "v1"], bridge.Reassigns[0].Assignments);

        lead.Handler = (_, _, _, _, _) => Task.FromResult<string?>("garbage");
        await OrchestratorJobBuilding.AssignByLeadAsync("p1", item, "mt-1", lead, bridge, autonomy);
        Assert.Contains(bridge.Events, e => e.Summary == "Lead crew choice was unreadable; keeping the proposed crew.");

        lead.Handler = (_, _, _, _, _) => Task.FromResult<string?>("[\"b2\",\"v1\"]");
        bridge.ReassignHandler = (_, _, _, _) => throw new InvalidOperationException("locked");
        await OrchestratorJobBuilding.AssignByLeadAsync("p1", item, "mt-1", lead, bridge, autonomy);
        Assert.Contains(bridge.Events, e => e.Summary == "Lead crew choice was refused: locked");
    }
}
