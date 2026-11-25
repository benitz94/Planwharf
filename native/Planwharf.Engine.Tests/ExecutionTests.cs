// Port of engine/execution.cjs owning helper cases.
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ExecutionTests
{
    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    static (ExecutionBridge Bridge, JsonObject Row, List<string> Calls) ProposedBridge(bool launcher = true)
    {
        var row = new JsonObject(); var calls = new List<string>();
        var models = new List<JsonObject> { Obj("""{"id":"codex:model-a","provider":"codex","cli":"model-a","label":"A","enabled":true,"available":true}"""),
            Obj("""{"id":"codex:model-b","provider":"codex","cli":"model-b","label":"B","enabled":true,"available":true}""") };
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
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
        public T WithLock<T>(Func<T> work) => work();
        public JsonObject Read() => data;
        public void Write(JsonObject store) => data = store;
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
