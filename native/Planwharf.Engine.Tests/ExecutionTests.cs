// Port of engine/execution.cjs owning helper cases.
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ExecutionTests
{
    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
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
