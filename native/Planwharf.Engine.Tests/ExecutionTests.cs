// Port of engine/execution.cjs owning helper cases.
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ExecutionTests
{
    static string Text(JsonNode? node) => node?.ToString() ?? "";
    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    static (ExecutionBridge Bridge, JsonObject Row, List<string> Calls) ProposedBridge(bool launcher = true)
    {
        var row = new JsonObject(); var calls = new List<string>();
        var models = new List<JsonObject> { Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","label":"A","enabled":true,"available":true}"""),
            Obj("""{"id":"codex:model-b","provider":"codex","cli":"model-b","label":"B","enabled":true,"available":true}""") };
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            IsProcessAlive = _ => true,
            Store = new JsonObject(), Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic",
            RowFor = (_, _) => { calls.Add("row"); return row; },
            Inspect = _ => { calls.Add("inspect"); return Obj("""{"git":false,"dirty":[]}"""); },
            Models = () => models, Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:lead",
            SessionId = "session-1", Persist = () => calls.Add("persist"),
            SpawnWorker = launcher ? _ => { calls.Add("spawn"); return new JsonObject { ["pid"] = 123 }; } : null,
            Files = new MemoryFiles(), NewId = () => "stable"
        });
        return (bridge, row, calls);
    }

    static JsonObject ProposalItem() => Obj("""{"project":{"id":"p","folder":"synthetic"},"revisions":[{"id":1,"direction":"Build it"}]}""");
    static JsonObject ProposalJob() => Obj("""{"id":"job","title":"Do work","detail":"Implement it","revision_id":1,"kind":"implementation"}""");
    static JsonObject ProposalPart() => Obj("""{"slice":"Build","modelId":"codex:model-a","scope":["engine"]}""");

    [Fact]
    public void ProposalApprovalPersistsSessionBeforeWorkerLaunch()
    {
        var (bridge, row, calls) = ProposedBridge();
        var mega = bridge.Propose(ProposalItem(), ProposalJob(), [ProposalPart()]);
        Assert.Equal("READY_FOR_APPROVAL", mega["status"]!.GetValue<string>());
        Assert.Equal("MT-stable", row["megaTaskId"]!.GetValue<string>());
        Assert.Equal("codex", bridge.Data.SubTasks[0]!["proposedProvider"]!.GetValue<string>());
        Assert.Equal(["inspect", "row", "persist"], calls.Where(c => c is "inspect" or "row" or "persist"));
        bridge.Approve("p", "MT-stable");
        Assert.Equal("session-1", mega["authorizedSessionId"]!.GetValue<string>());
        Assert.Equal("MT-stable", mega["approvedMegaTaskId"]!.GetValue<string>());
        Assert.Equal(2, calls.Count(c => c == "persist"));
        Assert.DoesNotContain("spawn", calls);
        Assert.Equal("Only a proposed MegaTask can be approved.", Assert.Throws<InvalidOperationException>(() => bridge.Approve("p", "MT-stable")).Message);
    }

    [Fact]
    public void ProposalRejectsForeignOrForwardDependenciesWithoutMutation()
    {
        var (bridge, _, calls) = ProposedBridge();
        var bad = Obj("""{"slice":"Build","modelId":"codex:model-a","dependencies":["foreign"]}""");
        Assert.Equal("Dependencies must reference an earlier SubTask in the same MegaTask.",
            Assert.Throws<InvalidOperationException>(() => bridge.Propose(ProposalItem(), ProposalJob(), [bad])).Message);
        Assert.Empty(bridge.Data.MegaTasks);
        Assert.DoesNotContain("persist", calls);
    }

    [Fact]
    public void ReplanAndReassignRequireUntouchedProposal()
    {
        var (bridge, _, _) = ProposedBridge();
        var mega = bridge.Propose(ProposalItem(), ProposalJob(), [ProposalPart()]);
        bridge.Reassign("p", "MT-stable", ["codex:model-b"], "Quota");
        Assert.Equal("model-b", bridge.Data.SubTasks[0]!["proposedCli"]!.GetValue<string>());
        bridge.Replan("p", "MT-stable", [Obj("""{"slice":"First","modelId":"codex:model-a"}"""),
            Obj("""{"slice":"Check","modelId":"codex:model-b","dependsOn":[0]}""")]);
        Assert.Equal("MT-stable-ST-01", bridge.Data.SubTasks[1]!["dependencies"]![0]!.GetValue<string>());
        bridge.Data.SubTasks[0]!["attemptIds"]!.AsArray().Add("attempt-1");
        Assert.Equal("A step of this proposal already ran.", Assert.Throws<InvalidOperationException>(() =>
            bridge.Replan("p", "MT-stable", [ProposalPart()])).Message);
        bridge.Approve("p", "MT-stable");
        Assert.Equal("Only a proposed MegaTask can be reassigned.", Assert.Throws<InvalidOperationException>(() =>
            bridge.Reassign("p", "MT-stable", ["codex:model-a", "codex:model-b"])).Message);
        Assert.Equal("RUNNING", mega["status"]!.GetValue<string>());
    }

    [Fact]
    public void AdvanceDispatchesOnlyAfterApprovalAndPersistsOwnedAttemptBeforeSpawn()
    {
        var row = new JsonObject(); var store = new JsonObject(); var calls = new List<string>();
        var files = new MemoryFiles(); var attempt = AttemptEngine.Create(new AttemptIo());
        JsonObject? seen = null;
        ExecutionBridge? bridge = null;
        bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            IsProcessAlive = _ => true,
            Store = store, Attempts = attempt, Directory = "synthetic", Files = files,
            RowFor = (_, _) => row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""),
            Models = () => [Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","label":"A","enabled":true,"available":true,"cost":2}""")],
            Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:lead", SessionId = "session-1",
            NewId = () => "stable", Persist = () => calls.Add("persist"),
            SpawnWorker = request =>
            {
                calls.Add("spawn"); seen = request;
                Assert.True(calls.Count(c => c == "persist") >= 3);
                Assert.Equal("session-1", bridgeForAssertion()!["authorizedSessionId"]!.GetValue<string>());
                Assert.Equal("RUNNING", bridgeForAssertion()!["status"]!.GetValue<string>());
                Assert.Equal("attempt-1", row["attemptId"]!.GetValue<string>());
                Assert.Equal("STATUS: WATCHING\n", Encoding.UTF8.GetString(files.Content[Path.Combine("synthetic", "results", "attempt-1.txt")]));
                return new JsonObject { ["pid"] = 441 };
            }
        });
        JsonObject? bridgeForAssertion() => bridge?.Data.MegaTasks[0] as JsonObject;
        var item = ProposalItem(); var job = ProposalJob();
        var mega = bridge.Advance(item, job, [ProposalPart()]);
        Assert.Equal("needs_approval", mega["status"]!.GetValue<string>());
        Assert.DoesNotContain("spawn", calls);
        bridge.Approve("p", "MT-stable");
        bridge.Advance(item, job, [ProposalPart()]);
        Assert.NotNull(seen);
        Assert.Equal("codex", seen!["provider"]!.GetValue<string>());
        Assert.Equal("model-a", seen["cli"]!.GetValue<string>());
        Assert.Equal("R-attempt-1", seen["resultId"]!.GetValue<string>());
        Assert.Equal("synthetic", seen["linkedFolder"]!.GetValue<string>());
        Assert.Equal("watching", row["status"]!.GetValue<string>());
        Assert.Equal(441, row["workerPid"]!.GetValue<int>());
        Assert.Equal(2, store["usage"]!["codex:model-a"]!["used"]!.GetValue<double>());
    }

    [Fact]
    public void AdvanceCapsParallelWorkersAndRespectsEarlierDependencies()
    {
        var row = new JsonObject(); var launches = new List<string>(); var files = new MemoryFiles();
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            IsProcessAlive = _ => true,
            Store = new JsonObject(), Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic", Files = files,
            RowFor = (_, _) => row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""),
            Models = () => [Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","enabled":true,"available":true}""")],
            Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:lead", SessionId = "session-1",
            NewId = () => "stable", Persist = () => { },
            SpawnWorker = request => { launches.Add(request["subTaskId"]!.GetValue<string>()); return new JsonObject { ["pid"] = launches.Count + 100 }; }
        });
        var parts = Enumerable.Range(0, 12).Select(i => Obj($$"""{"slice":"Part {{i}}","modelId":"codex:model-a","dependencies":[]}""")).ToArray();
        parts[11]["dependsOn"] = new JsonArray(0);
        var item = ProposalItem(); var job = ProposalJob();
        bridge.Propose(item, job, parts);
        bridge.Approve("p", "MT-stable");
        bridge.Advance(item, job, parts);
        Assert.Equal(10, launches.Count);
        Assert.DoesNotContain("MT-stable-ST-12", launches);
        Assert.Equal(10, bridge.Data.SubTasks.OfType<JsonObject>().Count(s => s["status"]!.GetValue<string>() == "RUNNING"));
    }

    [Fact]
    public void DeadlineCrossingAndChangedSessionDoNotDispatch()
    {
        var (bridge, _, calls) = ProposedBridge();
        var item = ProposalItem(); var job = ProposalJob();
        job["timing"] = "deadline"; job["deadlineAt"] = "2025-12-31T23:00:00.000Z";
        var mega = bridge.Propose(item, job, [ProposalPart()]);
        bridge.Approve("p", "MT-stable");
        bridge.Advance(item, job, [ProposalPart()]);
        Assert.DoesNotContain("spawn", calls);
        Assert.True(mega["pauseRequested"]!.GetValue<bool>());
        Assert.Equal("2026-01-01T00:00:00.000Z", mega["deadlineReachedAt"]!.GetValue<string>());
        Assert.Single(bridge.Data.Timeline.OfType<JsonObject>(), e => e["type"]!.GetValue<string>() == "DEADLINE_REACHED");
        var (second, _, secondCalls) = ProposedBridge();
        var other = second.Propose(item, ProposalJob(), [ProposalPart()]);
        second.Approve("p", "MT-stable");
        other["authorizedSessionId"] = "old-session";
        second.Advance(item, ProposalJob(), [ProposalPart()]);
        Assert.DoesNotContain("spawn", secondCalls);
        Assert.Equal("Resume the remaining work explicitly after backend restart.", other["nextAction"]!.GetValue<string>());
    }

    [Fact]
    public void QuotaWaitingKeepsPendingWithoutInventingAttemptOrUsage()
    {
        var store = new JsonObject(); var row = new JsonObject(); var launched = false;
        var model = Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","enabled":true,"available":true}""");
        var quota = false;
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            IsProcessAlive = _ => true,
            Store = store, Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic", Files = new MemoryFiles(),
            RowFor = (_, _) => row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""), Models = () => [model],
            Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:lead", SessionId = "session-1",
            NewId = () => "stable", Persist = () => { }, SpawnWorker = _ => { launched = true; return new JsonObject { ["pid"] = 1 }; },
            ResolveWorker = (worker, _) => quota ? null : worker,
            ExplainNoWorker = (_, _) => Obj("""{"waiting":true,"message":"Waiting for crew quota."}""")
        });
        var item = ProposalItem(); var job = ProposalJob();
        bridge.Propose(item, job, [ProposalPart()]); bridge.Approve("p", "MT-stable");
        quota = true; bridge.Advance(item, job, [ProposalPart()]);
        Assert.False(launched);
        Assert.Equal("PENDING", bridge.Data.SubTasks[0]!["status"]!.GetValue<string>());
        Assert.Equal("Waiting for crew quota.", bridge.Data.SubTasks[0]!["waitingFor"]!.GetValue<string>());
        Assert.Empty(bridge.Data.SubTasks[0]!["attemptIds"]!.AsArray());
        Assert.Null(store["usage"]);
    }

    [Fact]
    public void WorktreeLaunchUsesItsFolderAndPreservesLinkedProjectIdentity()
    {
        var row = new JsonObject(); var requests = new List<JsonObject>(); var created = new List<string>();
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            IsProcessAlive = _ => true,
            Store = new JsonObject(), Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic", Files = new MemoryFiles(),
            RowFor = (_, _) => row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""),
            Models = () => [Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","enabled":true,"available":true}""")],
            Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:lead", SessionId = "session-1",
            NewId = () => "stable", Persist = () => { },
            CreateWorktree = (folder, _, _, _, subId, _) => { created.Add(subId); return new Worktrees.Part("isolated-" + subId, "branch-" + subId, subId); },
            SpawnWorker = request => { requests.Add((JsonObject)request.DeepClone()); return new JsonObject { ["pid"] = requests.Count + 200 }; }
        });
        var first = ProposalPart(); first["dependencies"] = new JsonArray();
        var second = Obj("""{"slice":"Second","modelId":"codex:model-a","dependencies":[],"scope":["native"]}""");
        var item = ProposalItem(); var job = ProposalJob();
        bridge.Propose(item, job, [first, second]); bridge.Approve("p", "MT-stable");
        bridge.Advance(item, job, [first, second]);
        Assert.Equal(2, requests.Count); Assert.Equal(2, created.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("synthetic", request["linkedFolder"]!.GetValue<string>());
            Assert.StartsWith("isolated-MT-stable-ST-", request["folder"]!.GetValue<string>());
            Assert.Contains("relative to this worker folder", request["job"]!["detail"]!.GetValue<string>());
        });
    }

    [Fact]
    public void ObserveDeduplicatesEventsAndViewIsAnIsolatedProjectSnapshot()
    {
        var (bridge, _, _) = ProposedBridge();
        var item = Obj("""{"project":{"id":"p","folder":"synthetic"},"revisions":[{"id":1,"title":"First"}],"home":{"notes":[{"id":"n","text":"Remember"}]}}""");
        bridge.Observe(item); bridge.Observe(item);
        Assert.Equal(2, bridge.Data.Timeline.Count);
        bridge.Propose(item, ProposalJob(), [ProposalPart()]);
        var view = bridge.View("p");
        Assert.Single(view["megaTasks"]!.AsArray());
        view["megaTasks"]![0]!["status"] = "CHANGED";
        Assert.Equal("READY_FOR_APPROVAL", bridge.Data.MegaTasks[0]!["status"]!.GetValue<string>());
        Assert.Equal("MegaTask does not belong to the target project.", Assert.Throws<InvalidOperationException>(() => bridge.Target("other", "MT-stable")).Message);
    }

    [Fact]
    public void ProposalRequiresLinkedFolderAndAnEligibleExactModel()
    {
        var (bridge, _, _) = ProposedBridge(launcher: false);
        var missing = ProposalItem(); missing["project"]!["folder"] = null;
        Assert.Equal("Set a local source folder before proposing implementation work.",
            Assert.Throws<InvalidOperationException>(() => bridge.Propose(missing, ProposalJob(), [ProposalPart()])).Message);
        var mega = bridge.Propose(ProposalItem(), ProposalJob(), [ProposalPart()]);
        Assert.Equal("The worker launcher is unavailable. Start Planwharf through its desktop launcher.",
            Assert.Throws<InvalidOperationException>(() => bridge.Approve("p", "MT-stable")).Message);
        Assert.Equal("READY_FOR_APPROVAL", mega["status"]!.GetValue<string>());
    }

    [Fact]
    public void ProposalIdentityAndConfiguredProviderCannotChangeBeforeApproval()
    {
        var model = Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","enabled":true,"available":true}""");
        var persisted = 0;
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            IsProcessAlive = _ => true,
            Store = new JsonObject(), Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic", Files = new MemoryFiles(),
            RowFor = (_, _) => new JsonObject(), Inspect = _ => Obj("""{"git":false,"dirty":[]}"""), Models = () => [model],
            Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:lead", SessionId = "s",
            NewId = () => "stable", Persist = () => persisted++, SpawnWorker = _ => new JsonObject { ["pid"] = 5 }
        });
        var job = ProposalJob(); var item = ProposalItem();
        var mega = bridge.Propose(item, job, [ProposalPart()]);
        job["detail"] = "Changed objective";
        Assert.Equal("MegaTask job identity changed. Prepare a new objective.", Assert.Throws<InvalidOperationException>(() =>
            bridge.Propose(item, job, [ProposalPart()])).Message);
        model["cli"] = "other-cli";
        Assert.Equal("Provider/model configuration changed for codex:model-a; review a new proposal.", Assert.Throws<InvalidOperationException>(() =>
            bridge.Approve("p", "MT-stable")).Message);
        Assert.Equal("READY_FOR_APPROVAL", mega["status"]!.GetValue<string>());
        Assert.Equal(2, persisted);
    }
    [Fact]
    public void OneBuildStepAndACheckNeverReadsAsZeroParts()
    {
        var subs = new[] { Obj("""{"id":"s1","title":"Implement job 26","assignedModelId":"grok","dependencies":[]}"""),
            Obj("""{"id":"s2","title":"Check","assignedModelId":"fable","dependencies":["s1"]}""") };
        Assert.Equal("Lead kept the work as one build step and a check of the whole: Implement job 26 (GROK) · Check (FABLE).", Execution.SplitSummary(subs, s => s.ToUpperInvariant()));
    }
    [Fact]
    public void SeveralPartsAndACheck()
    {
        var subs = new[] { Obj("""{"id":"a","title":"A","assignedModelId":"x"}"""), Obj("""{"id":"b","title":"B","assignedModelId":"y"}"""),
            Obj("""{"id":"c","title":"C","assignedModelId":"z","dependencies":["a","b"]}""") };
        Assert.Equal("Lead split the work into 2 parts and a check of the whole: A (X) · B (Y) · C (Z). Why: Two at once.", Execution.SplitSummary(subs, s => s.ToUpperInvariant(), "Two at once."));
        Assert.Equal("Lead split the work into 0 parts: .", Execution.SplitSummary([], s => s));
    }
    [Theory]
    [InlineData(0, "retry-same")]
    [InlineData(1, "switch")]
    [InlineData(2, "stop")]
    [InlineData(3, "stop")]
    public void NoVerdictRetryPlan(double tries, string expected) => Assert.Equal(expected, Execution.NoVerdictPlan(tries));

    [Fact]
    public void DeliveredWithoutVerdictRequiresNewCommitExitZeroAndScope()
    {
        var input = new JsonObject { ["folder"] = "synthetic", ["before"] = new string('a', 40), ["after"] = new string('b', 40), ["exitCode"] = 0,
            ["scope"] = new JsonArray("validation"), ["names"] = new JsonArray("./validation/log.txt") };
        Assert.Equal("validation/log.txt", Execution.DeliveredWithoutVerdict(input)!["filesChanged"]![0]!.GetValue<string>());
        input["scope"] = new JsonArray("engine"); Assert.Null(Execution.DeliveredWithoutVerdict(input));
        input.Remove("scope"); input["after"] = new string('a', 40); Assert.Null(Execution.DeliveredWithoutVerdict(input));
        input["after"] = new string('b', 40); input["exitCode"] = 1; Assert.Null(Execution.DeliveredWithoutVerdict(input));
        input["exitCode"] = "0"; Assert.Null(Execution.DeliveredWithoutVerdict(input));
    }

    [Fact]
    public void DeliveryKeepsOrderAndDuplicatesLimitsEvidenceAndCountsAllFiles()
    {
        var names = new JsonArray(Enumerable.Range(0, 42).Select(i => (JsonNode?)JsonValue.Create($"./src/{i}.cs")).ToArray());
        names[1] = "./src/0.cs";
        var input = new JsonObject { ["folder"] = "synthetic", ["before"] = new string('a', 64),
            ["after"] = new string('b', 64), ["exitCode"] = 0, ["scope"] = new JsonArray("./src/"), ["names"] = names };
        var original = input.ToJsonString();
        var result = Execution.DeliveredWithoutVerdict(input)!;
        Assert.Equal(40, result["filesChanged"]!.AsArray().Count);
        Assert.Equal("src/0.cs", result["filesChanged"]![1]!.GetValue<string>());
        Assert.Equal("src/39.cs", result["filesChanged"]![39]!.GetValue<string>());
        Assert.Equal("Delivered on disk without a structured result: 42 file(s) committed after the step started.", result["summary"]!.GetValue<string>());
        Assert.Equal(original, input.ToJsonString());
        input["names"] = new JsonArray("src-other/file.cs");
        Assert.Null(Execution.DeliveredWithoutVerdict(input));
        input["scope"] = new JsonArray();
        Assert.NotNull(Execution.DeliveredWithoutVerdict(input));
        input["before"] = new string('A', 40);
        Assert.Null(Execution.DeliveredWithoutVerdict(input));
    }

    [Fact]
    public void RepoStateUsesRenameDestinationAndCumulativeByteLimit()
    {
        var root = Path.GetFullPath("synthetic-project");
        var fs = new MemoryFiles { Info = new(4 * 1024 * 1024, true, false) };
        foreach (var name in new[] { "new.txt", "second.txt", "third.txt" })
            fs.Content[Path.Combine(root, name)] = Encoding.UTF8.GetBytes("controlled content");
        var facts = Obj("""{"git":true,"commitId":"preferred","lastCommit":"old","head":"older","dirty":["R old.txt -> new.txt"," M second.txt"]}""");
        var comparable = Execution.RepoState(root, facts, fs);
        Assert.True(comparable["comparable"]!.GetValue<bool>());
        Assert.Equal("preferred", comparable["repoHead"]!.GetValue<string>());
        Assert.Contains("read:" + Path.Combine(root, "new.txt"), fs.Calls);
        facts["dirty"]!.AsArray().Add(" M third.txt");
        var overLimit = Execution.RepoState(root, facts, fs);
        Assert.False(overLimit["comparable"]!.GetValue<bool>());
        Assert.Null(overLimit["fingerprint"]);
        Assert.DoesNotContain("read:" + Path.Combine(root, "third.txt"), fs.Calls);
        Assert.Equal(facts["dirty"]!.ToJsonString(), overLimit["dirtyFiles"]!.ToJsonString());
    }

    [Fact]
    public void DurableSchemaPreservesUnknownLegacyFieldsNullAndOrdering()
    {
        var store = Obj("""{"execution":{"megaTasks":[{"id":"old","custom":null}],"subTasks":[],"results":[],"checkpoints":[],"timeline":[],"decisions":[],"observations":{},"unknown":{"keep":1}}}""");
        var original = store.ToJsonString();
        var data = new ExecutionData(store);
        Assert.Same(store["execution"], data.Node);
        Assert.Equal(original, store.ToJsonString());
        Assert.True(data.MegaTasks[0]!.AsObject().ContainsKey("custom"));
        Assert.False(data.MegaTasks[0]!.AsObject().ContainsKey("status"));
        var other = new ExecutionData(new JsonObject()); other.MegaTasks.Add(new JsonObject());
        Assert.Single(data.MegaTasks);
    }

    [Fact]
    public void DurableInitializationRepairsCollectionsAndKeepsTruthyLegacyObservations()
    {
        var store = Obj("""{"outside":null,"execution":{"megaTasks":null,"subTasks":{},"observations":["legacy"],"future":null}}""");
        var data = new ExecutionData(store);
        Assert.Empty(data.MegaTasks);
        Assert.Empty(data.SubTasks);
        Assert.Empty(data.Results);
        Assert.Empty(data.Checkpoints);
        Assert.Empty(data.Timeline);
        Assert.Empty(data.Decisions);
        Assert.Same(store["execution"]!["observations"], data.Observations);
        Assert.Equal("[\"legacy\"]", data.Observations!.ToJsonString());
        Assert.True(data.Node.ContainsKey("future"));
        Assert.True(store.ContainsKey("outside"));
        Assert.Null(store["outside"]);
        data.Node["observations"] = false;
        Assert.IsType<JsonObject>(new ExecutionData(store).Observations);
    }

    sealed class CollectionFixture
    {
        public readonly MemoryFiles Files = new();
        public readonly AttemptIo Io = new();
        public readonly JsonObject Store = new(), Row = new();
        public readonly List<string> Calls = [];
        public readonly List<JsonObject> Models = [Obj("""{"id":"a","provider":"codex","cli":"a","enabled":true,"available":true}"""),
            Obj("""{"id":"b","provider":"codex","cli":"b","enabled":true,"available":true}"""),
            Obj("""{"id":"c","provider":"codex","cli":"c","enabled":true,"available":true}""")];
        public readonly AttemptEngine Attempts;
        public ExecutionBridge Bridge;
        public JsonObject Mega;
        public bool Alive = true;
        public bool FailPersist { get; set; }
        public int Launches;
        public string Clock = "2026-01-01T00:00:00.000Z";
        public Worktrees.GitRunner? Git { get; set; }
        public JsonObject? Next;
        public PlanFile? Plan { get; set; }
        public string Session = "session";
        public CollectionFixture()
        {
            Attempts = AttemptEngine.Create(Io);
            Bridge = Create();
            Mega = Bridge.Propose(ProposalItem(), ProposalJob(), [Obj("""{"slice":"Build","modelId":"a","scope":["src"]}""")]);
            Bridge.Approve("p", Text(Mega["id"]));
            Bridge.Advance(ProposalItem(), ProposalJob(), []);
            Clock = "2026-01-01T00:01:00.000Z";
        }
        public ExecutionBridge Create() => Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = Store, Attempts = Attempts, Directory = "synthetic", Files = Files, Models = () => Models,
            RowFor = (_, _) => Row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""), Now = () => Clock,
            LeadId = () => "lead", SessionId = Session, NewId = () => "stable", Plan = Plan,
            IsProcessAlive = pid => { Calls.Add("alive:" + pid); return Alive; },
            SpawnWorker = _ => { Launches++; return new JsonObject { ["pid"] = 100 + Launches }; },
            Persist = () => { Calls.Add("persist"); if (FailPersist) throw new IOException("persist failed"); },
            OnRefusal = (model, reason) => Calls.Add("refusal:" + model + ":" + reason),
            Replacement = (_, excluded) => { Calls.Add("replacement:" + string.Join(",", excluded)); return Next; },
            SafeGitWorkspace = _ => true,
            Git = call => Git?.Invoke(call) ?? new Worktrees.GitReply(1)
        });
        public JsonObject Sub => Bridge.Data.SubTasks[0]!.AsObject();
        public JsonObject Part => Row["dispatch"]!.AsArray().OfType<JsonObject>().First(p => JsonNode.DeepEquals(p["subTaskId"], Sub["id"]));
        public void Write(string path, JsonObject value) => Files.Content[path] = Encoding.UTF8.GetBytes(value.ToJsonString());
        public JsonObject Result(string status = "COMPLETE") => new()
        {
            ["resultId"] = "R-" + Text(Part["attemptId"]), ["attemptId"] = Part["attemptId"]?.DeepClone(),
            ["subTaskId"] = Sub["id"]?.DeepClone(), ["megaTaskId"] = Mega["id"]?.DeepClone(), ["status"] = status,
            ["summary"] = "Delivered", ["filesChanged"] = new JsonArray("src/a.cs"), ["tests"] = new JsonArray(),
            ["blockers"] = new JsonArray(), ["nextAction"] = "Review", ["future"] = new JsonObject { ["keep"] = null }
        };
        public void Evidence(JsonObject? result = null)
        {
            Write(Text(Part["structuredResultPath"]), result ?? Result());
            Write(Text(Part["exitPath"]), new JsonObject { ["attemptId"] = Part["attemptId"]?.DeepClone(),
                ["provider"] = Sub["provider"]?.DeepClone(), ["model"] = Sub["cli"]?.DeepClone(), ["exitCode"] = 0,
                ["createdAt"] = Clock });
        }
        public void Collect() { Alive = false; Bridge.Reconcile(Mega); }
        public void LaunchAgain() { Alive = true; Bridge.Advance(ProposalItem(), ProposalJob(), []); Alive = false; }
    }

    [Fact]
    public void DeadWorkerCollectsOnceAcrossRestartWithUnknownFieldsAndAccounting()
    {
        var f = new CollectionFixture(); f.Evidence(); f.Collect();
        Assert.Equal("COMPLETE", Text(f.Sub["status"]));
        Assert.Single(f.Bridge.Data.Results);
        Assert.Equal("{\"keep\":null}", f.Bridge.Data.Results[0]!["future"]!.ToJsonString());
        Assert.Equal("closed", Text(f.Attempts.GetAttempts()[0]["status"]));
        Assert.Single(Execution.ReadStepTimes("synthetic", f.Files));
        Assert.Single(Execution.ReadStepOutcomes("synthetic", f.Files));
        var snapshot = f.Store.ToJsonString();
        f.Session = "restarted"; f.Bridge = f.Create(); f.Bridge.Reconcile(f.Mega); f.Bridge.Reconcile(f.Mega);
        Assert.Equal(snapshot, f.Store.ToJsonString());
        Assert.Single(f.Bridge.Data.Timeline.OfType<JsonObject>(), e => Text(e["type"]) == "SUBTASK_COMPLETED");
        Assert.Single(Execution.ReadStepTimes("synthetic", f.Files));
    }

    [Theory]
    [InlineData("attemptId")]
    [InlineData("subTaskId")]
    [InlineData("megaTaskId")]
    [InlineData("resultId")]
    [InlineData("exit-provider")]
    [InlineData("exit-model")]
    [InlineData("exit-attempt")]
    [InlineData("exit-code")]
    [InlineData("corrupt")]
    [InlineData("partial")]
    public void InvalidIdentityOrCorruptEvidenceNeverCompletes(string mutation)
    {
        var f = new CollectionFixture(); var result = f.Result(); f.Evidence(result);
        if (mutation.StartsWith("exit-", StringComparison.Ordinal))
        {
            var path = Text(f.Part["exitPath"]);
            var exit = JsonNode.Parse(f.Files.Content[path])!.AsObject();
            exit[mutation == "exit-attempt" ? "attemptId" : mutation == "exit-code" ? "exitCode" : mutation[5..]] = "wrong";
            f.Write(path, exit);
        }
        else if (mutation == "corrupt") f.Files.Content[Text(f.Part["structuredResultPath"])] = Encoding.UTF8.GetBytes("{broken");
        else if (mutation == "partial") { result.Remove("tests"); f.Evidence(result); }
        else { result[mutation] = "wrong"; f.Evidence(result); }
        f.Collect();
        Assert.Equal("PENDING", Text(f.Sub["status"])); Assert.Empty(f.Bridge.Data.Results);
        Assert.Equal(1, f.Sub["noVerdictTries"]!.GetValue<int>());
        Assert.Equal("closed", Text(f.Attempts.GetAttempts()[0]["status"]));
        Assert.Empty(Execution.ReadStepTimes("synthetic", f.Files));
    }

    [Fact]
    public void NoVerdictRetriesSameThenSwitchesThenStops()
    {
        var f = new CollectionFixture(); f.Next = f.Models[1];
        f.Collect(); Assert.Equal("a", Text(f.Sub["assignedModelId"]));
        f.LaunchAgain(); f.Collect(); Assert.Equal("b", Text(f.Sub["assignedModelId"]));
        Assert.Equal(2, f.Sub["noVerdictTries"]!.GetValue<int>());
        f.LaunchAgain(); f.Collect(); f.Bridge.Reconcile(f.Mega);
        Assert.Equal("FAILED", Text(f.Sub["status"])); Assert.Single(f.Bridge.Data.Results);
        Assert.Equal(3, f.Attempts.GetAttempts().Count); Assert.All(f.Attempts.GetAttempts(), a => Assert.Equal("closed", Text(a["status"])));
        Assert.Equal(2, Execution.ReadStepOutcomes("synthetic", f.Files).Count);
        Assert.Single(Execution.ReadStepTimes("synthetic", f.Files));
        Assert.Contains("replacement:a", f.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveOrReusedPidDefersCollectionEvenWithValidEvidence(bool receipt)
    {
        var f = new CollectionFixture(); f.Evidence();
        if (receipt)
        {
            f.Part["pid"] = null;
            f.Write(Text(f.Part["exitPath"]) + ".process.json", new JsonObject { ["attemptId"] = f.Part["attemptId"]?.DeepClone(), ["pid"] = 987 });
        }
        f.Bridge = f.Create(); f.Bridge.Reconcile(f.Mega);
        Assert.Equal("RUNNING", Text(f.Sub["status"])); Assert.Empty(f.Bridge.Data.Results);
        Assert.True(f.Row["workerAlive"]!.GetValue<bool>());
        Assert.Contains("alive:" + (receipt ? "987" : "101"), f.Calls);
        f.Collect(); Assert.Single(f.Bridge.Data.Results);
    }

    [Fact]
    public void LogWriteFailureIsBestEffortAndDoesNotLoseResult()
    {
        var f = new CollectionFixture(); f.Evidence(); f.Files.FailWrite = true; f.Collect();
        Assert.Single(f.Bridge.Data.Results); Assert.Equal("COMPLETE", Text(f.Sub["status"]));
        Assert.Equal("closed", Text(f.Attempts.GetAttempts()[0]["status"]));
        Assert.Empty(Execution.ReadStepTimes("synthetic", f.Files));
        Assert.Empty(Execution.ReadStepOutcomes("synthetic", f.Files));
        f.Files.FailWrite = false; f.Bridge.Reconcile(f.Mega);
        Assert.Empty(Execution.ReadStepTimes("synthetic", f.Files));
    }

    [Fact]
    public void RefusalReroutesAreBoundedAndCooldownPrecedesReplacement()
    {
        var f = new CollectionFixture();
        for (var i = 0; i < 3; i++)
        {
            f.Files.Content[Text(f.Part["logPath"])] = Encoding.UTF8.GetBytes("insufficient credits");
            f.Next = f.Models[Math.Min(i + 1, 2)]; f.Collect();
            if (i < 2)
            {
                Assert.Equal("PENDING", Text(f.Sub["status"]));
                Assert.Equal(i + 1, f.Sub["refusalReroutes"]!.GetValue<double>());
                var refusal = f.Calls.FindLastIndex(c => c.StartsWith("refusal:", StringComparison.Ordinal));
                var replacement = f.Calls.FindLastIndex(c => c.StartsWith("replacement:", StringComparison.Ordinal));
                Assert.True(refusal < replacement);
                f.LaunchAgain();
            }
        }
        Assert.Equal(2, f.Calls.Count(c => c.StartsWith("refusal:", StringComparison.Ordinal)));
        Assert.Equal(1, f.Sub["noVerdictTries"]!.GetValue<int>());
        Assert.Empty(f.Bridge.Data.Results);
        Assert.Equal(2, Execution.ReadStepOutcomes("synthetic", f.Files).Count);
    }

    [Theory]
    [InlineData(false, "BLOCKED")]
    [InlineData(true, "COMPLETE")]
    public void ConcurrentSharedFolderChangesAreNotAttributedToOneWorker(bool concurrent, string expected)
    {
        var f = new CollectionFixture();
        var other = (JsonObject)f.Sub.DeepClone(); other["id"] = "other"; other["scope"] = new JsonArray("other");
        other["status"] = concurrent ? "RUNNING" : "PENDING";
        f.Bridge.Data.SubTasks.Add((JsonNode)other);
        f.Git = call => call.Args.Contains("status") ? new Worktrees.GitReply(0, " M outside.cs\0") : new Worktrees.GitReply(1);
        f.Evidence(); f.Collect();
        Assert.Equal(expected, Text(f.Sub["status"]));
        Assert.Single(f.Bridge.Data.Results);
        if (!concurrent) Assert.Equal("[\"outside.cs\"]", f.Sub["scopeViolation"]!.ToJsonString());
    }

    [Fact]
    public void ReportedScopeViolationStillBlocksWhileOthersRunAndIsolatedCommitInspectionFailsClosed()
    {
        var f = new CollectionFixture();
        var other = (JsonObject)f.Sub.DeepClone(); other["id"] = "other";
        f.Bridge.Data.SubTasks.Add((JsonNode)other);
        var result = f.Result(); result["filesChanged"] = new JsonArray("outside.cs"); f.Evidence(result); f.Collect();
        Assert.Equal("BLOCKED", Text(f.Sub["status"]));
        Assert.Equal("Stopped: changed files outside its scope (outside.cs). Delivered", Text(f.Bridge.Data.Results[0]!["summary"]));
        var isolated = new CollectionFixture(); isolated.Part["branch"] = "part"; isolated.Part["baseHead"] = new string('a', 40);
        isolated.Evidence(); isolated.Collect();
        Assert.Equal("BLOCKED", Text(isolated.Sub["status"]));
        Assert.Equal("[\"(commit inspection unavailable)\"]", isolated.Sub["scopeViolation"]!.ToJsonString());
    }

    [Fact]
    public void DependencyScopeAllowsFinishedFilesAndUnownedFilesButProtectsBusyScope()
    {
        var f = new CollectionFixture();
        var parent = (JsonObject)f.Sub.DeepClone(); parent["id"] = "parent"; parent["status"] = "COMPLETE"; parent["scope"] = new JsonArray("parent");
        var busy = (JsonObject)f.Sub.DeepClone(); busy["id"] = "busy"; busy["status"] = "PENDING"; busy["scope"] = new JsonArray("busy");
        f.Bridge.Data.SubTasks.Add((JsonNode)parent); f.Bridge.Data.SubTasks.Add((JsonNode)busy);
        f.Sub["dependencies"] = new JsonArray("parent");
        var result = f.Result(); result["filesChanged"] = new JsonArray("src/a.cs", "parent/a.cs", "unowned.cs", "busy/a.cs");
        f.Evidence(result); f.Collect();
        Assert.Equal("[\"busy/a.cs\"]", f.Sub["scopeViolation"]!.ToJsonString());
    }

    [Fact]
    public void PreexistingDirtyAndFinishedWorkerOwnedFilesAreNotBlamed()
    {
        var f = new CollectionFixture(); f.Mega["repoBefore"]!["dirtyFiles"] = new JsonArray(" M before.cs");
        var other = (JsonObject)f.Sub.DeepClone(); other["id"] = "other"; other["status"] = "COMPLETE";
        other["scope"] = new JsonArray("other"); other["finishedAt"] = "2025-12-31T00:00:00.000Z";
        f.Bridge.Data.SubTasks.Add((JsonNode)other);
        f.Git = call => call.Args.Contains("status") ? new Worktrees.GitReply(0, " M before.cs\0 M other/a.cs\0") : new Worktrees.GitReply(1);
        f.Evidence(); f.Collect(); Assert.Equal("COMPLETE", Text(f.Sub["status"])); Assert.Null(f.Sub["scopeViolation"]);
    }

    [Theory]
    [InlineData(0, "src/a.cs", "COMPLETE", "Delivered on disk without a structured result:")]
    [InlineData(1, "src/a.cs", "COMPLETE", "Delivered on disk:")]
    [InlineData(0, "outside.cs", "BLOCKED", "Stopped:")]
    public void CommitFallbackPreservesJavaScriptExitAndScopeBehavior(int exitCode, string name, string status, string summary)
    {
        var f = new CollectionFixture(); f.Mega["repoBefore"]!["repoHead"] = new string('a', 40);
        f.Write(Text(f.Part["exitPath"]), new JsonObject { ["attemptId"] = f.Part["attemptId"]?.DeepClone(), ["exitCode"] = exitCode });
        f.Git = call => new Worktrees.GitReply(0, call.Args.Contains("rev-parse") ? new string('b', 40) : call.Args.Contains("diff") ? name + "\n" : "");
        f.Collect();
        Assert.Equal(status, Text(f.Sub["status"])); Assert.StartsWith(summary, Text(f.Bridge.Data.Results[0]!["summary"]));
        Assert.Equal("TECHNICAL", Text(f.Bridge.Data.Results[0]!["evidenceKind"]));
    }

    [Fact]
    public void CancelledMegaTaskIsNotDispatchedAndMissingOwnedPartFailsWithoutInventedResult()
    {
        var f = new CollectionFixture(); f.Mega["status"] = "CANCELLED"; f.Evidence(); f.Alive = false;
        f.Bridge.Advance(ProposalItem(), ProposalJob(), []);
        Assert.Equal("CANCELLED", Text(f.Mega["status"])); Assert.Single(f.Bridge.Data.Results); Assert.Equal(1, f.Launches);
        var missing = new CollectionFixture(); missing.Row["dispatch"] = new JsonArray(); missing.Bridge.Reconcile(missing.Mega);
        Assert.Equal("FAILED", Text(missing.Sub["status"])); Assert.Empty(missing.Bridge.Data.Results);
        Assert.Equal("active", Text(missing.Attempts.GetAttempts()[0]["status"]));
    }

    [Fact]
    public void PersistenceFailureAfterCollectionKeepsInMemoryEvidenceWithoutDuplicatingAccounting()
    {
        var f = new CollectionFixture(); f.Evidence(); f.Alive = false; f.FailPersist = true;
        Assert.Equal("persist failed", Assert.Throws<IOException>(() => f.Bridge.Advance(ProposalItem(), ProposalJob(), [])).Message);
        Assert.Equal("COMPLETE", Text(f.Mega["status"])); Assert.Single(f.Bridge.Data.Results);
        Assert.Equal("closed", Text(f.Attempts.GetAttempts()[0]["status"]));
        f.FailPersist = false; f.Bridge.Advance(ProposalItem(), ProposalJob(), []);
        Assert.Single(Execution.ReadStepTimes("synthetic", f.Files)); Assert.Single(Execution.ReadStepOutcomes("synthetic", f.Files));
        Assert.Single(f.Bridge.Data.Timeline.OfType<JsonObject>(), e => Text(e["type"]) == "MEGATASK_COMPLETED");
    }

    [Fact]
    public void AttemptWriteFailurePreservesJavaScriptPartialCollectionOrder()
    {
        var f = new CollectionFixture(); f.Evidence(); f.Io.FailWrite = true;
        Assert.Throws<IOException>(() => f.Collect());
        Assert.Single(f.Bridge.Data.Results); Assert.Equal("COMPLETE", Text(f.Sub["status"]));
        Assert.Empty(Execution.ReadStepTimes("synthetic", f.Files));
        Assert.DoesNotContain(f.Bridge.Data.Timeline.OfType<JsonObject>(), e => Text(e["type"]) == "SUBTASK_COMPLETED");
        f.Io.FailWrite = false; f.Bridge.Reconcile(f.Mega);
        Assert.Single(f.Bridge.Data.Results); Assert.Empty(Execution.ReadStepTimes("synthetic", f.Files));
        Assert.Equal("active", Text(f.Attempts.GetAttempts()[0]["status"]));
    }

    [Fact]
    public void RestartRequiresExplicitCheckpointBeforeRemainingWorkDispatch()
    {
        var f = new CollectionFixture(); f.Collect(); f.Session = "new-session"; f.Bridge = f.Create();
        f.Bridge.Advance(ProposalItem(), ProposalJob(), []);
        Assert.Equal("PAUSED", Text(f.Mega["status"])); Assert.Equal(1, f.Launches);
        var checkpoint = Assert.Single(f.Bridge.Data.Checkpoints);
        Assert.Equal("backend restarted", Text(checkpoint!["reason"]));
        Assert.Equal("[\"MT-stable-ST-01\"]", checkpoint["pendingSubTaskIds"]!.ToJsonString());
        f.Bridge.Advance(ProposalItem(), ProposalJob(), []); Assert.Single(f.Bridge.Data.Checkpoints);
    }

    [Fact]
    public void NewDecisionLinePreservesJavaScriptReferenceErrorAndPartialState()
    {
        var f = new CollectionFixture(); var result = f.Result();
        result["summary"] = "Needs your decision: Choose | suggestion: yes | breaks: no";
        f.Evidence(result);
        Assert.Equal("item is not defined", Assert.Throws<InvalidOperationException>(() => f.Collect()).Message);
        Assert.Single(f.Bridge.Data.Results); Assert.Equal("COMPLETE", Text(f.Sub["status"]));
        Assert.Empty(f.Bridge.Data.Decisions); Assert.Equal("active", Text(f.Attempts.GetAttempts()[0]["status"]));
        Assert.Null(f.Part["validatedResultStatus"]); Assert.Empty(Execution.ReadStepTimes("synthetic", f.Files));
        f.Bridge.Reconcile(f.Mega); Assert.Single(f.Bridge.Data.Results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void VerifyRecordsBuildAcceptanceOrFixesOnceWithQuota(int fixedFiles)
    {
        var f = new CollectionFixture();
        var verify = (JsonObject)f.Sub.DeepClone(); verify["id"] = "verify"; verify["title"] = "Verify the work independently";
        verify["status"] = "PENDING"; verify["dependencies"] = new JsonArray(f.Sub["id"]!.DeepClone()); verify["attemptIds"] = new JsonArray(); verify.Remove("scope");
        f.Bridge.Data.SubTasks.Add((JsonNode)verify);
        f.Write(Text(f.Part["structuredResultPath"]) + ".usage.json", Obj("""{"inputTokens":22,"outputTokens":7}"""));
        f.Evidence(); f.Collect(); Assert.Empty(Execution.ReadStepOutcomes("synthetic", f.Files));
        f.Alive = true; f.Bridge.Advance(ProposalItem(), ProposalJob(), []); f.Alive = false;
        var part = f.Row["dispatch"]!.AsArray().OfType<JsonObject>().Single(p => Text(p["subTaskId"]) == "verify");
        var result = f.Result(); result["resultId"] = "R-" + Text(part["attemptId"]); result["attemptId"] = part["attemptId"]?.DeepClone(); result["subTaskId"] = "verify";
        result["filesChanged"] = new JsonArray(Enumerable.Range(0, fixedFiles).Select(i => (JsonNode?)JsonValue.Create("src/fix" + i)).ToArray());
        f.Write(Text(part["structuredResultPath"]), result);
        f.Write(Text(part["exitPath"]), new JsonObject { ["attemptId"] = part["attemptId"]?.DeepClone(), ["provider"] = "codex", ["model"] = "a", ["exitCode"] = 0 });
        f.Bridge.Advance(ProposalItem(), ProposalJob(), [], schedule: false);
        var outcomes = Execution.ReadStepOutcomes("synthetic", f.Files);
        Assert.Equal(2, outcomes.Count); Assert.Equal("Verify", Text(outcomes[0]["role"])); Assert.Equal("Build", Text(outcomes[1]["role"]));
        Assert.All(outcomes, r => { Assert.Equal(fixedFiles == 0, r["accepted"]!.GetValue<bool>()); Assert.Equal(fixedFiles, r["fixed"]!.GetValue<int>()); });
        Assert.Equal(22, outcomes[1]["quota"]!["inputTokens"]!.GetValue<double>());
        Assert.Equal("COMPLETE", Text(f.Mega["status"]));
        f.Bridge.Advance(ProposalItem(), ProposalJob(), [], schedule: false);
        Assert.Equal(2, Execution.ReadStepOutcomes("synthetic", f.Files).Count);
        Assert.Equal(2, Execution.ReadStepTimes("synthetic", f.Files).Count);
    }

    sealed class NoPlanGit : IPlanGit
    {
        public PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000) => new(false);
    }

    [Fact]
    public void CompleteTicksPlanOnceAndRetainsExistingPlanRules()
    {
        var folder = Directory.CreateTempSubdirectory("pw-collection-plan-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".planwharf"));
            var path = PlanFile.PlanPath(folder);
            File.WriteAllText(path, "# Work\n\nRules: preserve evidence\n\n- [ ] Build\n");
            var f = new CollectionFixture(); f.Plan = new PlanFile(new NoPlanGit()); f.Bridge = f.Create(); f.Part["projectFolder"] = folder;
            f.Evidence(); f.Collect(); var ticked = File.ReadAllText(path);
            Assert.Contains("- [x] Build — not committed", ticked); Assert.Contains("Rules: preserve evidence", ticked);
            f.Bridge.Reconcile(f.Mega); Assert.Equal(ticked, File.ReadAllText(path));
            f.Bridge.Advance(ProposalItem(), ProposalJob(), [], schedule: false);
            Assert.Equal("COMPLETE", Text(f.Mega["status"]));
            Assert.Equal("Review", Text(f.Mega["nextAction"])); Assert.False(f.Mega["repoAfter"]!["gitReadable"]!.GetValue<bool>());
            Assert.Empty(f.Mega["repoAfter"]!["commitIds"]!.AsArray());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void BrokenPlanDoesNotBlockSavedResultAndCompletionWaitsForIntegration()
    {
        var f = new CollectionFixture(); f.Plan = new PlanFile(new NoPlanGit()); f.Bridge = f.Create();
        f.Part["projectFolder"] = "\0invalid"; f.Evidence(); f.Collect();
        Assert.Equal("COMPLETE", Text(f.Sub["status"])); Assert.Single(f.Bridge.Data.Results);
        f.Mega["activeGroup"] = new JsonArray(f.Sub["id"]!.DeepClone());
        f.Mega["worktreeParts"] = new JsonArray(new JsonObject { ["subId"] = f.Sub["id"]!.DeepClone(), ["folder"] = "isolated", ["branch"] = "part" });
        f.Bridge.Advance(ProposalItem(), ProposalJob(), [], schedule: false);
        Assert.Equal("RUNNING", Text(f.Mega["status"])); Assert.Null(f.Mega["completedAt"]);
    }

    [Fact]
    public void LaunchAndResultFileFailuresRetainTheirDifferentJavaScriptFinalStates()
    {
        var f = new CollectionFixture(); f.Collect(); f.Files.FailWrite = true;
        Assert.Throws<IOException>(() => f.Bridge.Advance(ProposalItem(), ProposalJob(), []));
        Assert.Equal("PENDING", Text(f.Sub["status"])); Assert.Single(f.Sub["attemptIds"]!.AsArray());
        Assert.Equal("active", Text(f.Attempts.GetAttempts()[1]["status"])); Assert.Empty(f.Bridge.Data.Results);
    }

    [Fact]
    public void ResearchSubTaskRequiresValidReportMdBeforeCompletion()
    {
        var f = new CollectionFixture();
        f.Next = f.Models[1];
        f.Sub["type"] = "RESEARCH";

        var report1 = Path.Combine(Path.GetFullPath(Text(f.Part["folder"])), "report.md");
        var res1 = f.Result(); res1["reportPath"] = report1;
        f.Evidence(res1);
        f.Files.Info = new ExecutionFileStat(0, false, false);
        f.Collect();
        Assert.Equal("PENDING", Text(f.Sub["status"]));
        Assert.Equal(1, f.Sub["noVerdictTries"]!.GetValue<int>());
        Assert.Empty(f.Bridge.Data.Results);

        f.Files.Info = new ExecutionFileStat(0, true, false);
        f.LaunchAgain();
        var report2 = Path.Combine(Path.GetFullPath(Text(f.Part["folder"])), "report.md");
        var res2 = f.Result(); res2["reportPath"] = report2;
        f.Files.Content[report2] = [];
        f.Evidence(res2);
        f.Collect();
        Assert.Equal("PENDING", Text(f.Sub["status"]));
        Assert.Equal(2, f.Sub["noVerdictTries"]!.GetValue<int>());

        f.Files.Info = new ExecutionFileStat(100, true, false);
        f.LaunchAgain();
        var report3 = Path.Combine(Path.GetFullPath(Text(f.Part["folder"])), "report.md");
        var res3 = f.Result(); res3["reportPath"] = report3;
        f.Files.Content[report3] = Encoding.UTF8.GetBytes("# Research Findings");
        f.Evidence(res3);
        f.Collect();
        Assert.Equal("COMPLETE", Text(f.Sub["status"]));
        Assert.Single(f.Bridge.Data.Results);
    }

    [Fact]
    public void TakeoverNoteFormulatesExactHandoffTextInSubsequentLaunch()
    {
        var f = new CollectionFixture();
        var requests = new List<JsonObject>();
        f.Bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = f.Store, Attempts = f.Attempts, Directory = "synthetic", Files = f.Files, Models = () => f.Models,
            RowFor = (_, _) => f.Row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""), Now = () => f.Clock,
            LeadId = () => "lead", SessionId = f.Session, NewId = () => "stable",
            IsProcessAlive = _ => true,
            SpawnWorker = req => { requests.Add(req); return new JsonObject { ["pid"] = 999 }; },
            Persist = () => { },
            SafeGitWorkspace = _ => true
        });
        f.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = f.Part["attemptId"]?.DeepClone(), ["reason"] = "no verdict" });
        f.Sub["takeover"] = new JsonObject
        {
            ["fromModelId"] = "gpt-6.1-sol",
            ["filesChanged"] = new JsonArray("src/fix.cs"),
            ["tests"] = new JsonArray(new JsonObject { ["command"] = "dotnet test", ["status"] = "PASSED" }),
            ["summary"] = "Almost finished",
            ["logTail"] = "line 1\nline 2"
        };
        f.Sub["status"] = "PENDING";
        f.Bridge.Advance(ProposalItem(), ProposalJob(), []);
        Assert.Single(requests);
        var detail = Text(requests[0]["job"]!["detail"]);
        Assert.StartsWith("HANDOFF: you take over this step from gpt-6.1-sol, which was stopped before finishing.\n" +
            "Continue from the current state of the project folder. Do not start over: first look at what is already there (git status and the files below), keep what works, then finish the step.\n" +
            "Files it changed: src/fix.cs\n" +
            "Tests it reported: dotnet test PASSED\n" +
            "Its last report: Almost finished\n" +
            "Its last activity (log tail):\nline 1\nline 2\n\n", detail);
    }

    [Fact]
    public void ScopeCheckAllowsFilesFinishedInContinuedCancelledMegaTask()
    {
        var f = new CollectionFixture();
        var cancelledMega = new JsonObject { ["id"] = "MT-cancelled", ["projectId"] = "p", ["status"] = "CANCELLED" };
        f.Bridge.Data.MegaTasks.Add((JsonNode)cancelledMega);
        var cancelledResult = new JsonObject
        {
            ["resultId"] = "R-cancelled", ["megaTaskId"] = "MT-cancelled", ["status"] = "COMPLETE",
            ["filesChanged"] = new JsonArray("completed_before.cs")
        };
        f.Bridge.Data.Results.Add((JsonNode)cancelledResult);

        var result = f.Result();
        result["filesChanged"] = new JsonArray("src/a.cs", "completed_before.cs");
        f.Evidence(result);
        f.Collect();

        Assert.Equal("COMPLETE", Text(f.Sub["status"]));
        Assert.Null(f.Sub["scopeViolation"]);
    }

    [Fact]
    public void WorktreeCreationFailureCollectsTechnicalResultAndFailsSubTask()
    {
        var store = new JsonObject();
        var row = new JsonObject();
        var attempts = AttemptEngine.Create(new AttemptIo());
        var files = new MemoryFiles();
        var models = new List<JsonObject> {
            Obj("""{"id":"a","provider":"codex","cli":"a","enabled":true,"available":true}"""),
            Obj("""{"id":"b","provider":"codex","cli":"b","enabled":true,"available":true}""")
        };
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = store, Attempts = attempts, Directory = "synthetic", Files = files, Models = () => models,
            RowFor = (_, _) => row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""), Now = () => "2026-01-01T00:00:00.000Z",
            LeadId = () => "lead", SessionId = "s", NewId = () => "stable",
            IsProcessAlive = _ => false,
            SpawnWorker = _ => new JsonObject { ["pid"] = 999 },
            CreateWorktree = (_, _, _, _, _, _) => throw new InvalidOperationException("disk full"),
            Persist = () => { },
            SafeGitWorkspace = _ => true
        });
        var parallel1 = Obj("""{"slice":"Build1","modelId":"a","scope":["src"],"dependsOn":[]}""");
        var parallel2 = Obj("""{"slice":"Build2","modelId":"b","scope":["src"],"dependsOn":[]}""");
        var mega = bridge.Propose(ProposalItem(), ProposalJob(), [parallel1, parallel2]);
        bridge.Approve("p", Text(mega["id"]));
        bridge.Advance(ProposalItem(), ProposalJob(), [parallel1, parallel2]);
        var sub1 = bridge.Data.SubTasks.OfType<JsonObject>().First(s => Text(s["title"]) == "Build1");
        Assert.Equal("FAILED", Text(sub1["status"]));
        var res = bridge.Data.Results.OfType<JsonObject>().First(r => JsonNode.DeepEquals(r["subTaskId"], sub1["id"]));
        Assert.Equal("FAILED", Text(res["status"]));
        Assert.Contains("Worktree creation failed: disk full", Text(res["summary"]));
    }

    [Fact]
    public void AuditSubTaskRoleRecordedAsReview()
    {
        var f = new CollectionFixture();
        var audit = (JsonObject)f.Sub.DeepClone();
        audit["id"] = "audit-sub";
        audit["type"] = "AUDIT";
        audit["title"] = "Audit the codebase";
        audit["status"] = "PENDING";
        audit["attemptIds"] = new JsonArray();
        audit["dependencies"] = new JsonArray();
        f.Bridge.Data.SubTasks.Add((JsonNode)audit);

        f.Alive = true;
        f.Bridge.Advance(ProposalItem(), ProposalJob(), []);
        f.Alive = false;

        var part = f.Row["dispatch"]!.AsArray().OfType<JsonObject>().First(p => Text(p["subTaskId"]) == "audit-sub");
        var result = f.Result();
        result["resultId"] = "R-" + Text(part["attemptId"]);
        result["attemptId"] = part["attemptId"]?.DeepClone();
        result["subTaskId"] = "audit-sub";
        f.Write(Text(part["structuredResultPath"]), result);
        f.Write(Text(part["exitPath"]), new JsonObject { ["attemptId"] = part["attemptId"]?.DeepClone(), ["provider"] = "codex", ["model"] = "a", ["exitCode"] = 0 });

        f.Bridge.Reconcile(f.Mega);
        Assert.Equal("COMPLETE", Text(audit["status"]));

        var outcomes = Execution.ReadStepOutcomes("synthetic", f.Files);
        var auditOutcome = outcomes.FirstOrDefault(o => Text(o["role"]) == "Review");
        Assert.NotNull(auditOutcome);
    }

    sealed class MemoryFiles : IExecutionFiles
    {
        public readonly Dictionary<string, byte[]> Content = new();
        public readonly List<string> Calls = [];
        public ExecutionFileStat Info = new(3, true, false);
        public bool FailWrite;
        public string? Parent;
        public byte[] Read(string path) { Calls.Add("read:" + path); return Content[path]; }
        public void Write(string path, byte[] bytes) { Calls.Add("write:" + path); if (FailWrite) throw new IOException("write failed"); Content[path] = bytes; }
        public void Append(string path, byte[] bytes) => Write(path, Content.GetValueOrDefault(path, []).Concat(bytes).ToArray());
        public void CreateDirectory(string path) => Calls.Add("mkdir:" + path);
        public string? LinkedWorkspace(string? path) => path;
        public string RealDirectory(string path) => Parent ?? path;
        public ExecutionFileStat Stat(string path) { Calls.Add("stat:" + path); return Info; }
    }

    [Fact]
    public void RepoStateHashesContentAndPreservesDirtyOrdering()
    {
        var root = Path.GetFullPath("synthetic-project");
        var fs = new MemoryFiles(); fs.Content[Path.Combine(root, "é.txt")] = Encoding.UTF8.GetBytes("one");
        var facts = Obj("""{"git":true,"head":"head","dirty":[" M é.txt"]}""");
        var state = Execution.RepoState(root, facts, fs);
        Assert.True(state["comparable"]!.GetValue<bool>());
        Assert.Equal("UNCOMMITTED", state["disposition"]!.GetValue<string>());
        Assert.Equal(" M é.txt", state["dirtyFiles"]![0]!.GetValue<string>());
        fs.Content[Path.Combine(root, "é.txt")] = Encoding.UTF8.GetBytes("two");
        Assert.NotEqual(state["fingerprint"]!.ToString(), Execution.RepoState(root, facts, fs)["fingerprint"]!.ToString());
        Assert.Equal(new[] { "stat:" + Path.Combine(root, "é.txt"), "read:" + Path.Combine(root, "é.txt"), "stat:" + Path.Combine(root, "é.txt"), "read:" + Path.Combine(root, "é.txt") }, fs.Calls);
        Assert.True(Execution.RepoState(null)["comparable"]!.GetValue<bool>());
        Assert.Equal("NOT APPLICABLE", Execution.RepoState(null)["disposition"]!.GetValue<string>());
        Assert.Equal("797e3dc8a48bf4fe78ba892d5e966be12cef8bef05dc91043d6421768de0b5c0", Execution.RepoState(null)["fingerprint"]!.GetValue<string>());
        Assert.Equal("d016344575570bb2ffc224a4eb3a377dd414f2ad071477674ed426d065f18b26", Execution.RepoState(null, Obj("""{"git":true,"head":"abc","dirty":[]}"""))["fingerprint"]!.GetValue<string>());
        Assert.False(Execution.RepoState(null, Obj("""{"error":"false"}"""))["comparable"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("../outside", 3, false, false)]
    [InlineData("a", 8388609, false, false)]
    [InlineData("a", 3, true, false)]
    [InlineData("a", 3, false, true)]
    public void RepoStateFailsClosedForEscapeOversizeSymlinkOrRedirectedParent(string name, long size, bool symlink, bool redirected)
    {
        var root = Path.GetFullPath("synthetic-project");
        var fs = new MemoryFiles { Info = new(size, true, symlink), Parent = redirected ? Path.GetDirectoryName(root) : null };
        var state = Execution.RepoState(root, new JsonObject { ["git"] = true, ["dirty"] = new JsonArray(" M " + name) }, fs);
        Assert.False(state["comparable"]!.GetValue<bool>());
        Assert.Null(state["fingerprint"]);
        Assert.DoesNotContain(fs.Calls, c => c.StartsWith("read:", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDirtyFileAndInspectionErrorAreNotComparable()
    {
        var state = Execution.RepoState(Path.GetFullPath("synthetic"), Obj("""{"dirty":[" D gone"]}"""), new MemoryFiles());
        Assert.False(state["comparable"]!.GetValue<bool>());
        Assert.Null(state["fingerprint"]);
        Assert.False(Execution.RepoState(null, Obj("""{"error":"unreadable"}"""))["comparable"]!.GetValue<bool>());
    }

    [Fact]
    public void TimeFilesSkipCorruptionKeepLastFiveHundredAndPropagateWrites()
    {
        var fs = new MemoryFiles();
        var path = Execution.StepTimesFile("store");
        fs.Content[path] = Encoding.UTF8.GetBytes("broken\nnull\n{\"role\":\"Build\",\"model\":\"old\",\"minutes\":1,\"outcome\":\"done\"}\n");
        Assert.Single(Execution.ReadStepTimes("store", fs));
        for (var i = 0; i < 502; i++) Execution.AppendStepTime("store", new JsonObject { ["role"] = "Build", ["model"] = "m" + i, ["minutes"] = 2, ["outcome"] = "done", ["secret"] = "never saved" }, fs);
        var rows = Execution.ReadStepTimes("store", fs);
        Assert.Equal(500, rows.Count); Assert.Equal("m2", rows[0]["model"]!.GetValue<string>());
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(fs.Content[path]));
        fs.FailWrite = true;
        Assert.Throws<IOException>(() => Execution.AppendStepTime("store", rows[0], fs));
        Assert.Equal(500, Execution.ReadStepTimes("store", fs).Count);
        Assert.Empty(Execution.ReadStepTimes("other", fs));
    }

    [Fact]
    public void OutcomeFilesCleanRecordsSkipCorruptionAndReturnNullOnWriteFailure()
    {
        var fs = new MemoryFiles();
        var input = Obj("""{"model":"codex:gpt-6.1-sol","role":"Build","accepted":true,"durationMs":45000,"secret":"not persisted"}""");
        Assert.NotNull(Execution.AppendStepOutcome("one", input, fs));
        var path = Execution.StepOutcomesFile("one");
        fs.Content[path] = Encoding.UTF8.GetBytes("broken\nnull\n" + Encoding.UTF8.GetString(fs.Content[path]));
        Assert.Single(Execution.ReadStepOutcomes("one", fs));
        Assert.Empty(Execution.ReadStepOutcomes("two", fs));
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(fs.Content[path]));
        fs.FailWrite = true;
        Assert.Null(Execution.AppendStepOutcome("one", input, fs));
        Assert.Single(Execution.ReadStepOutcomes("one", fs));
    }

    [Fact]
    public void RealFileHelpersUseIndependentTemporaryStores()
    {
        var root = Directory.CreateTempSubdirectory("pw execution é ").FullName;
        try
        {
            var first = Path.Combine(root, "one"); var second = Path.Combine(root, "two");
            Execution.AppendStepTime(first, Obj("""{"role":"Build","model":"exact-provider:model","minutes":2,"outcome":"done"}"""));
            Assert.Single(Execution.ReadStepTimes(first)); Assert.Empty(Execution.ReadStepTimes(second));
            Assert.NotNull(Execution.AppendStepOutcome(second, Obj("""{"role":"Build","model":"exact-provider:model","accepted":true}""")));
            Assert.Single(Execution.ReadStepOutcomes(second)); Assert.Empty(Execution.ReadStepOutcomes(first));
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class AttemptIo : IAttemptIo
    {
        JsonObject data = new();
        public bool FailWrite { get; set; }
        public T WithLock<T>(Func<T> work) => work();
        public JsonObject Read() => data;
        public void Write(JsonObject store) { if (FailWrite) throw new IOException("attempt write failed"); data = store; }
    }

    [Fact]
    public void BridgeConstructionSharesStoreAndDoesNotInspectPersistOrLaunch()
    {
        var store = Obj("""{"execution":{"megaTasks":[{"id":"legacy","future":null}],"unknown":[1,2]}}""");
        var calls = new List<string>();
        var options = new ExecutionOptions
        {
            Store = store, Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic",
            RowFor = (_, _) => throw new InvalidOperationException("unexpected row read"),
            Inspect = _ => throw new InvalidOperationException("unexpected inspection"),
            Models = () => [], Now = () => "2026-01-01T00:00:00.000Z", LeadId = () => "codex:gpt-6.1-sol",
            Persist = () => calls.Add("persist"), SpawnWorker = _ => throw new InvalidOperationException("unexpected launch"),
            Files = new MemoryFiles(), Git = _ => throw new InvalidOperationException("unexpected Git")
        };
        var bridge = Execution.CreateExecutionBridge(options);
        Assert.Same(options, bridge.Options);
        Assert.Same(store["execution"], bridge.Data.Node);
        Assert.Equal("legacy", bridge.Data.MegaTasks[0]!["id"]!.GetValue<string>());
        Assert.True(bridge.Data.MegaTasks[0]!.AsObject().ContainsKey("future"));
        Assert.Equal("[1,2]", bridge.Data.Node["unknown"]!.ToJsonString());
        Assert.Empty(calls);
    }
}
