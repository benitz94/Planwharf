using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

// The module-by-module comparison (native/porting/PLAN.md, phase 1b): every case recorded from the
// JavaScript in native/porting/parity/<module>.json is given to the C# port, which must return the same
// result, or fail with the same message.
public class ParityTests
{
    static readonly string Folder = FindFolder();

    static string FindFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "porting", "parity");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("native/porting/parity not found above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(file => file, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(file), "required.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                if (node is not JsonArray arr) continue;
                var count = arr.Count;
                for (var i = 0; i < count; i++) data.Add(Path.GetFileNameWithoutExtension(file), i);
            }
            catch (JsonException) { }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_CSharp_port_gives_the_JavaScript_result(string module, int index)
    {
        var recorded = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, module + ".json")))!.AsArray()[index]!.AsObject();
        var fn = recorded["fn"]!.GetValue<string>();
        var args = recorded["args"]!.AsArray();
        if (!Ports.TryGetValue($"{module}/{fn}", out var port)) throw new Xunit.Sdk.XunitException($"No C# port is wired for {module}/{fn}: add it to ParityTests.Ports.");
        JsonNode? result = null; string? error = null;
        try { result = port(args); }
        catch (Exception failure) when (failure is not Xunit.Sdk.XunitException)
        {
            error = failure.InnerException?.Message ?? failure.Message;
        }
        var where = $"{module}/{fn} case {index}: {args.ToJsonString()}";
        if (recorded["error"] is JsonNode expectedError)
        {
            Assert.True(error != null, $"{where}\nThe JavaScript fails with \"{expectedError}\", the C# returned {result?.ToJsonString() ?? "null"}.");
            Assert.Equal(expectedError.GetValue<string>(), error);
            return;
        }
        Assert.True(error == null, $"{where}\nThe C# fails with \"{error}\", the JavaScript returned {recorded["result"]?.ToJsonString() ?? "null"}.");
        Assert.True(Same(recorded["result"], result), $"{where}\nJavaScript: {recorded["result"]?.ToJsonString() ?? "null"}\nC#:         {result?.ToJsonString() ?? "null"}");
    }

    public static List<string> ValidateParityDirectory(JsonObject required, string parityDir, IReadOnlySet<string> wiredPorts)
    {
        var errors = new List<string>();
        foreach (var (moduleName, spec) in required)
        {
            if (spec is JsonObject specObj && specObj.TryGetPropertyValue("scenarioOnly", out var so) && so is JsonValue sov && sov.TryGetValue<bool>(out var isSo) && isSo)
            {
                // Scenario-only modules do not have pure parity .json files or ParityTests.Ports wiring
                continue;
            }

            var minCases = spec is JsonObject obj && obj.TryGetPropertyValue("minCases", out var mc) && mc is JsonValue mv && mv.TryGetValue<int>(out var val)
                ? val
                : (spec is JsonValue v && v.TryGetValue<int>(out var num) ? num : 1);
            var moduleFile = Path.Combine(parityDir, $"{moduleName}.json");
            if (!File.Exists(moduleFile))
            {
                errors.Add($"Missing parity file: {moduleName}.json");
                continue;
            }

            JsonArray? casesArr = null;
            try
            {
                casesArr = JsonNode.Parse(File.ReadAllText(moduleFile)) as JsonArray;
            }
            catch (JsonException ex)
            {
                errors.Add($"Invalid JSON in {moduleName}.json: {ex.Message}");
                continue;
            }

            if (casesArr == null || casesArr.Count < minCases)
            {
                errors.Add($"Module {moduleName} has only {casesArr?.Count ?? 0} cases, requires at least {minCases}");
            }

            if (casesArr != null)
            {
                var fns = casesArr.Select(c => c?["fn"]?.GetValue<string>()).Where(f => !string.IsNullOrEmpty(f)).Distinct();
                foreach (var fn in fns)
                {
                    if (!wiredPorts.Contains($"{moduleName}/{fn}"))
                    {
                        errors.Add($"Function {moduleName}/{fn} is not wired in ParityTests.Ports");
                    }
                }
            }
        }
        return errors;
    }

    [Fact]
    public void Every_required_port_has_parity_cases_and_wiring()
    {
        var requiredFile = Path.Combine(Folder, "required.json");
        Assert.True(File.Exists(requiredFile), "required.json must exist in native/porting/parity");
        var required = JsonNode.Parse(File.ReadAllText(requiredFile))!.AsObject();
        var wired = new HashSet<string>(Ports.Keys, StringComparer.Ordinal);
        var errors = ValidateParityDirectory(required, Folder, wired);
        Assert.Empty(errors);
    }

    [Fact]
    public void Parity_guard_validates_module_requirements_and_wiring()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-parity-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Missing file
            var req = new JsonObject { ["missing-mod"] = new JsonObject { ["minCases"] = 5 } };
            var errors = ValidateParityDirectory(req, tempDir, new HashSet<string>());
            Assert.Contains(errors, e => e.Contains("Missing parity file: missing-mod.json"));

            // Too few cases
            File.WriteAllText(Path.Combine(tempDir, "short-mod.json"), "[{\"fn\":\"testFn\",\"args\":[]}]");
            req["short-mod"] = new JsonObject { ["minCases"] = 5 };
            errors = ValidateParityDirectory(req, tempDir, new HashSet<string> { "short-mod/testFn" });
            Assert.Contains(errors, e => e.Contains("Module short-mod has only 1 cases, requires at least 5"));

            // Invalid JSON
            File.WriteAllText(Path.Combine(tempDir, "corrupt-mod.json"), "{ invalid-json }");
            req["corrupt-mod"] = new JsonObject { ["minCases"] = 1 };
            errors = ValidateParityDirectory(req, tempDir, new HashSet<string>());
            Assert.Contains(errors, e => e.Contains("Invalid JSON in corrupt-mod.json"));

            // Non-array JSON
            File.WriteAllText(Path.Combine(tempDir, "obj-mod.json"), "{\"not\":\"an array\"}");
            req["obj-mod"] = new JsonObject { ["minCases"] = 1 };
            errors = ValidateParityDirectory(req, tempDir, new HashSet<string>());
            Assert.Contains(errors, e => e.Contains("Module obj-mod has only 0 cases, requires at least 1"));

            // Unwired function
            File.WriteAllText(Path.Combine(tempDir, "unwired-mod.json"), "[{\"fn\":\"unwiredFn\",\"args\":[]}]");
            req["unwired-mod"] = new JsonObject { ["minCases"] = 1 };
            errors = ValidateParityDirectory(req, tempDir, new HashSet<string>());
            Assert.Contains(errors, e => e.Contains("Function unwired-mod/unwiredFn is not wired"));

            // Scenario-only module does not require pure file or wired ports
            req["scenario-only-mod"] = new JsonObject { ["scenarioOnly"] = true, ["scenarioFloors"] = new JsonObject { ["scen1"] = 1 } };
            errors = ValidateParityDirectory(req, tempDir, new HashSet<string> { "unwired-mod/unwiredFn" });
            Assert.DoesNotContain(errors, e => e.Contains("scenario-only-mod"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    public static readonly Dictionary<string, int> ExpectedScenarioFloors = new()
    {
        ["provider-models"] = 19,
        ["provider-adapters"] = 11,
        ["api-agent"] = 27,
        ["worktrees"] = 16,
        ["pr-check"] = 18,
        ["phone-activation-check"] = 12,
        ["execution"] = 11,
        ["project-home"] = 2
    };

    public static Dictionary<string, int> LoadScenarioFloors(string parityDir)
    {
        var required = JsonNode.Parse(File.ReadAllText(Path.Combine(parityDir, "required.json"))) as JsonObject;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (required == null) return result;
        foreach (var (_, spec) in required)
        {
            if (spec is not JsonObject specObj) continue;
            if (specObj.TryGetPropertyValue("scenarioFloors", out var sf) && sf is JsonObject sfObj)
            {
                foreach (var (key, value) in sfObj)
                {
                    if (value is JsonValue v && v.TryGetValue<int>(out var count))
                    {
                        result[key] = count;
                    }
                }
            }
        }
        return result;
    }

    public static List<string> ValidateScenarioFloors(IReadOnlyDictionary<string, int> floors, string scenariosDir)
    {
        var errors = new List<string>();
        foreach (var (module, minCases) in floors)
        {
            var file = Path.Combine(scenariosDir, $"{module}.json");
            if (!File.Exists(file))
            {
                errors.Add($"Missing scenario fixture: {module}.json");
                continue;
            }
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var arr = node?["scenarios"] as JsonArray;
                var count = arr?.Count ?? 0;
                if (count < minCases)
                {
                    errors.Add($"Scenario module {module} has only {count} scenarios, requires at least {minCases}");
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"Invalid JSON in scenario fixture {module}.json: {ex.Message}");
            }
        }
        return errors;
    }

    [Fact]
    public void Scenario_floors_are_enforced_and_fail_on_missing_coverage()
    {
        var scenariosDir = Path.Combine(Folder, "scenarios");
        Assert.True(Directory.Exists(scenariosDir), "scenarios directory must exist");
        var floors = LoadScenarioFloors(Folder);
        Assert.Equal(ExpectedScenarioFloors, floors);
        var errors = ValidateScenarioFloors(floors, scenariosDir);
        Assert.Empty(errors);

        // Guard mutation checks: demonstrate that lowered scenario count or missing fixture causes failure
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-scen-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Missing scenario fixture fails
            var missingErrors = ValidateScenarioFloors(new Dictionary<string, int> { ["provider-models"] = 19 }, tempDir);
            Assert.Contains(missingErrors, e => e.Contains("Missing scenario fixture: provider-models.json"));

            // Missing execution scenario fixture fails
            var missingExec = ValidateScenarioFloors(new Dictionary<string, int> { ["execution"] = 11 }, tempDir);
            Assert.Contains(missingExec, e => e.Contains("Missing scenario fixture: execution.json"));

            // Missing project-home scenario fixture fails
            var missingHome = ValidateScenarioFloors(new Dictionary<string, int> { ["project-home"] = 2 }, tempDir);
            Assert.Contains(missingHome, e => e.Contains("Missing scenario fixture: project-home.json"));

            // Too few scenarios fails
            var fixture = new JsonObject
            {
                ["schema"] = 1,
                ["module"] = "provider-models",
                ["scenarios"] = new JsonArray { new JsonObject { ["name"] = "s1" } }
            };
            File.WriteAllText(Path.Combine(tempDir, "provider-models.json"), fixture.ToJsonString());
            var shortErrors = ValidateScenarioFloors(new Dictionary<string, int> { ["provider-models"] = 19 }, tempDir);
            Assert.Contains(shortErrors, e => e.Contains("has only 1 scenarios, requires at least 19"));

            // Too few project-home scenarios fails
            var homeFixture = new JsonObject
            {
                ["schema"] = 1,
                ["module"] = "project-home",
                ["scenarios"] = new JsonArray { new JsonObject { ["name"] = "h1" } }
            };
            File.WriteAllText(Path.Combine(tempDir, "project-home.json"), homeFixture.ToJsonString());
            var shortHome = ValidateScenarioFloors(new Dictionary<string, int> { ["project-home"] = 2 }, tempDir);
            Assert.Contains(shortHome, e => e.Contains("Scenario module project-home has only 1 scenarios, requires at least 2"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Parity_ports_and_comparisons_are_thread_safe_under_concurrency()
    {
        var sampleModules = new[] { "quota", "models", "time-left", "phone-voice", "step-outcomes", "plan-status" };
        Parallel.ForEach(sampleModules, new ParallelOptions { MaxDegreeOfParallelism = 4 }, mod =>
        {
            var file = Path.Combine(Folder, $"{mod}.json");
            var cases = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
            for (var i = 0; i < cases.Count; i++)
            {
                The_CSharp_port_gives_the_JavaScript_result(mod, i);
            }
        });
    }

    [Fact]
    public void Same_json_comparison_handles_edge_cases()
    {
        // Null comparisons
        Assert.True(Same(null, null));
        Assert.False(Same(null, new JsonObject()));
        Assert.False(Same(new JsonObject(), null));

        // Objects
        var o1 = new JsonObject { ["a"] = 1, ["b"] = "text" };
        var o2 = new JsonObject { ["b"] = "text", ["a"] = 1 };
        var o3 = new JsonObject { ["a"] = 1, ["b"] = "different" };
        var o4 = new JsonObject { ["a"] = 1 };
        Assert.True(Same(o1, o2));
        Assert.False(Same(o1, o3));
        Assert.False(Same(o1, o4));

        // Arrays
        var a1 = new JsonArray { 1, "two", true };
        var a2 = new JsonArray { 1, "two", true };
        var a3 = new JsonArray { 1, "two", false };
        var a4 = new JsonArray { 1, "two" };
        Assert.True(Same(a1, a2));
        Assert.False(Same(a1, a3));
        Assert.False(Same(a1, a4));

        // Numbers (integers and floats with identical value)
        Assert.True(Same(JsonValue.Create(42), JsonValue.Create(42.0)));
        Assert.False(Same(JsonValue.Create(42), JsonValue.Create(43)));

        // Mismatched value kinds
        Assert.False(Same(JsonValue.Create(1), JsonValue.Create("1")));
        Assert.False(Same(JsonValue.Create(true), JsonValue.Create(1)));
        Assert.False(Same(new JsonObject(), new JsonArray()));
    }

    [Fact]
    public void OrchestratorJobBuildingParity_SliceCount_replays_recorded_vectors_with_exact_parity()
    {
        var file = Path.Combine(Folder, "orchestrator-job-building.json");
        Assert.True(File.Exists(file), "orchestrator-job-building.json must exist in parity folder");
        var cases = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        var tested = 0;
        foreach (var entry in cases)
        {
            var obj = entry!.AsObject();
            if (obj["fn"]?.GetValue<string>() != "sliceCount") continue;
            var args = obj["args"]!.AsArray();
            var job = ParseJob(Obj(args, 0));
            var facts = ParseFacts(Obj(args, 1));
            var eligible = args.Count > 2 && args[2] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 1;
            var expected = obj["result"]!.GetValue<int>();
            var actual = OrchestratorJobBuilding.SliceCount(job, facts, eligible);
            Assert.Equal(expected, actual);
            tested++;
        }
        Assert.True(tested >= 48, $"Expected at least 48 sliceCount cases, got {tested}");
    }

    [Fact]
    public void OrchestratorJobBuildingParity_PlanLines_replays_recorded_vectors_with_exact_parity()
    {
        var file = Path.Combine(Folder, "orchestrator-job-building.json");
        Assert.True(File.Exists(file), "orchestrator-job-building.json must exist in parity folder");
        var cases = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        var tested = 0;
        foreach (var entry in cases)
        {
            var obj = entry!.AsObject();
            if (obj["fn"]?.GetValue<string>() != "planLines") continue;
            var args = obj["args"]!.AsArray();
            var item = ParseProjectItem(Obj(args, 0));
            var facts = ParseFacts(Obj(args, 1));
            var shown = ParseModelCandidate(Obj(args, 2));
            var active = args.Count > 3 && args[3] is JsonValue v && v.TryGetValue<bool>(out var act) && act;
            var extras = ParsePlanExtras(Obj(args, 4));
            var expectedLines = obj["result"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            var actualLines = OrchestratorJobBuilding.PlanLines(item, facts, shown, active, extras);
            Assert.Equal(expectedLines.Length, actualLines.Count);
            for (var i = 0; i < expectedLines.Length; i++)
            {
                Assert.Equal(expectedLines[i], actualLines[i]);
            }
            tested++;
        }
        Assert.True(tested >= 48, $"Expected at least 48 planLines cases, got {tested}");
    }

    static JobDescriptor? ParseJob(JsonObject? obj)
    {
        if (obj == null) return null;
        return new JobDescriptor(
            obj["id"]?.GetValue<string>(),
            obj["title"]?.GetValue<string>(),
            obj["detail"]?.GetValue<string>(),
            obj["kind"]?.GetValue<string>(),
            obj["timing"]?.GetValue<string>(),
            obj["deadlineAt"]?.GetValue<string>(),
            obj["revision_id"]?.GetValue<string>()
        )
        {
            Status = obj["status"]?.GetValue<string>(),
            Type = obj["type"]?.GetValue<string>()
        };
    }

    static InspectionFacts? ParseFacts(JsonObject? obj)
    {
        if (obj == null) return null;
        var git = obj["git"] is JsonValue gv && gv.TryGetValue<bool>(out var g) && g;
        var lastCommit = obj["lastCommit"]?.GetValue<string>();
        var lastSubject = obj["lastSubject"]?.GetValue<string>();
        var recent = (obj["recent"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToArray();
        var dirty = (obj["dirty"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToArray();
        var head = obj["head"]?.GetValue<string>();
        var branch = obj["branch"]?.GetValue<string>();
        var fileCount = obj["fileCount"] is JsonValue fv && fv.TryGetValue<int>(out var fc) ? fc : 0;
        var folder = obj["folder"]?.GetValue<string>();
        var error = obj["error"]?.GetValue<string>();
        return new InspectionFacts(git, lastCommit, lastSubject, recent, dirty, head, branch, fileCount, folder, error);
    }

    static ModelCandidate ParseModelCandidate(JsonObject? obj)
    {
        if (obj == null) return new ModelCandidate("");
        return new ModelCandidate(
            obj["id"]?.GetValue<string>() ?? "",
            obj["label"]?.GetValue<string>(),
            obj["why"]?.GetValue<string>(),
            obj["provider"]?.GetValue<string>()
        );
    }

    static ProjectItem ParseProjectItem(JsonObject? obj)
    {
        if (obj == null) return new ProjectItem(new ProjectInfo("", "", null), []);
        var projObj = obj["project"] as JsonObject;
        var proj = new ProjectInfo(
            projObj?["id"]?.GetValue<string>() ?? "",
            projObj?["name"]?.GetValue<string>() ?? "",
            projObj?["folder"]?.GetValue<string>()
        );
        var revs = (obj["revisions"] as JsonArray)?
            .Select(r => r is JsonObject ro ? new Revision(ro["id"]?.GetValue<string>(), ro["direction"]?.GetValue<string>(), (ro["open"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToArray()) : null)
            .Where(r => r != null)
            .Select(r => r!)
            .ToArray() ?? [];
        var tasks = (obj["tasks"] as JsonArray)?
            .Select(t => t is JsonObject to ? ParseJob(to) : null)
            .Where(t => t != null)
            .Select(t => t!)
            .ToArray();
        return new ProjectItem(proj, revs, tasks);
    }

    static PlanExtras? ParsePlanExtras(JsonObject? obj)
    {
        if (obj == null) return null;
        var dispatch = (obj["dispatch"] as JsonArray)?
            .Select(d => d is JsonObject dobj ? new DispatchPart(
                dobj["modelId"]?.GetValue<string>() ?? "",
                dobj["label"]?.GetValue<string>() ?? "",
                dobj["slice"]?.GetValue<string>() ?? "",
                dobj["why"]?.GetValue<string>() ?? ""
            ) : null)
            .Where(d => d != null)
            .Select(d => d!)
            .ToArray();
        var job = obj["job"] is JsonObject jobObj ? ParseJob(jobObj) : null;
        var cp = obj["checkpoint"] is JsonObject cpObj ? new CheckpointRecord(cpObj["path"]?.GetValue<string>()) : null;
        return new PlanExtras(dispatch, job, cp);
    }

    static JsonObject? Obj(JsonArray args, int i) => i < args.Count ? args[i]?.DeepClone() as JsonObject : null;
    static JsonArray? Arr(JsonArray args, int i) => i < args.Count ? args[i]?.DeepClone() as JsonArray : null;
    static JsonNode? Node(JsonArray args, int i) => i < args.Count ? args[i]?.DeepClone() : null;
    static string? Text(JsonArray args, int i) => i < args.Count && args[i] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static JsonNode? Num(double? value) => value is null ? null : JsonValue.Create(value.Value);

    // One entry per ported function: how to call the C# with the recorded JavaScript arguments.
    static readonly Dictionary<string, Func<JsonArray, JsonNode?>> Ports = new()
    {
        ["antigravity/poolOf"] = a => Antigravity.PoolOf(Node(a, 0), Node(a, 1)),
        ["antigravity/parseUsage"] = a => Antigravity.ParseUsage(Node(a, 0)),
        ["antigravity/parseModels"] = a => Antigravity.ParseModels(Node(a, 0)?.GetValue<string>()),
        ["runner/parseArgs"] = a =>
        {
            var arr = Arr(a, 0)!.Select(x => x?.GetValue<string>() ?? "").ToArray();
            var opts = Runner.ParseArgs(arr);
            var o = new JsonObject
            {
                ["folder"] = opts.Folder,
                ["result"] = opts.Result,
                ["title"] = opts.Title,
                ["detail"] = opts.Detail,
                ["worker"] = opts.Worker,
                ["attempt"] = opts.Attempt,
                ["slice"] = opts.Slice
            };
            if (!string.IsNullOrEmpty(opts.LinkedFolder)) o["linkedFolder"] = opts.LinkedFolder;
            if (opts.Model != null) o["model"] = opts.Model.DeepClone();
            if (!string.IsNullOrEmpty(opts.Kind)) o["kind"] = opts.Kind;
            return o;
        },
        ["runner/dropThinkingPrefix"] = a => (JsonNode)JsonValue.Create(Runner.DropThinkingPrefix(Text(a, 0), Text(a, 1)))!,
        ["runner/toolRequestRule"] = a => (JsonNode)JsonValue.Create(Runner.ToolRequestRule(Obj(a, 0)))!,
        ["runner/cliModelOf"] = a => (JsonNode?)JsonValue.Create(Runner.CliModelOf(Obj(a, 0))),
        ["runner/parseAskLine"] = a =>
        {
            var accObj = Obj(a, 0)!;
            var acc = new Runner.AskAccumulator
            {
                ExpectedModel = accObj["expectedModel"]?.GetValue<string>(),
                Answer = accObj["answer"]?.GetValue<string>() ?? "",
                Thinking = accObj["thinking"]?.GetValue<string>() ?? "",
                Ms = accObj["ms"] is JsonValue mv && mv.TryGetValue<long>(out var msv) ? msv : null
            };
            Runner.ParseAskLine(acc, Node(a, 1));
            var outObj = new JsonObject();
            foreach (var (k, v) in accObj) outObj[k] = v?.DeepClone();
            outObj["answer"] = acc.Answer;
            outObj["thinking"] = acc.Thinking;
            if (acc.Ms.HasValue) outObj["ms"] = acc.Ms.Value;
            if (acc.ModelSeen) outObj["modelSeen"] = true;
            if (acc.ResultSeen) outObj["resultSeen"] = true;
            if (acc.Failure != null) outObj["failure"] = acc.Failure;
            if (acc.Error != null) outObj["error"] = acc.Error;
            if (acc.Tokens != null) outObj["tokens"] = acc.Tokens.DeepClone();
            return outObj;
        },
        ["external-adapters/apiConfig"] = a => ExternalAdapters.ApiConfig(Obj(a, 0)),
        ["external-adapters/apiHeaders"] = a => ExternalAdapters.ApiHeaders(Obj(a, 0)!, Obj(a, 1)!.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? "")),
        ["provider-connections/connection"] = a => ProviderConnections.Connection(Node(a, 0)),
        ["model-policy/migratePolicy"] = a => ModelPolicy.MigratePolicy(Node(a, 0)),
        ["model-policy/validatePolicy"] = a => ModelPolicy.ValidatePolicy(Node(a, 0)),
        ["model-policy/usagePolicy"] = a => ModelPolicy.UsagePolicy(Obj(a, 0)!, Arr(a, 1)),
        ["model-policy/quotaLeft"] = a => Num(ModelPolicy.QuotaLeft(Obj(a, 0)!, Arr(a, 1))),
        ["model-policy/quotaLimit"] = a => ModelPolicy.QuotaLimit(Node(a, 0)),
        ["model-policy/quotaWindows"] = a => ModelPolicy.QuotaWindows(Obj(a, 0)!, Arr(a, 1)),
        ["model-policy/windowKind"] = a => ModelPolicy.WindowKind(Node(a, 0)),
        ["model-policy/lowQuota"] = a => ModelPolicy.LowQuota(Obj(a, 0)!, Arr(a, 1), a.Count > 2 ? Node(a, 2) : null),
        ["model-policy/quotaSwitches"] = a =>
        {
            var o = Obj(a, 0)!;
            var limit = o["thresholdPercent"]?.DeepClone();
            var enabled = o["enabled"] is not JsonValue flag || flag.GetValue<bool>();
            return ModelPolicy.QuotaSwitches(o["running"]?.DeepClone() as JsonArray, o["models"]?.DeepClone() as JsonArray, o["plans"]?.DeepClone() as JsonArray, limit, enabled);
        },
        ["model-policy/availableAgain"] = a => ModelPolicy.AvailableAgain(Obj(a, 0), a.Count > 1 ? (long)a[1]!.GetValue<double>() : null, a.Count > 2 ? (long)a[2]!.GetValue<double>() : null),
        ["model-policy/resolveUsageFallback"] = a => ModelPolicy.ResolveUsageFallback(Obj(a, 0)!, Arr(a, 1)!, Arr(a, 2), _ => true),
        ["model-roles/isVerifySlice"] = a => ModelRoles.IsVerifySlice(Node(a, 0)),
        ["model-roles/supportsSupervisor"] = a => ModelRoles.SupportsSupervisor(Obj(a, 0)!),
        ["model-roles/roleEligibility"] = a => new JsonObject(ModelRoles.RoleEligibility(Obj(a, 0)!).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
        ["model-roles/withModelRoles"] = a => ModelRoles.WithModelRoles(Obj(a, 0)!, Obj(a, 1)),
        ["model-roles/assignmentRole"] = a => ModelRoles.AssignmentRole(Obj(a, 0)),
        ["model-roles/permitsAssignment"] = a => ModelRoles.PermitsAssignment(Obj(a, 0)!, Obj(a, 1)),
        ["structured-final/supportsFinalResult"] = a => StructuredFinal.SupportsFinalResult(Node(a, 0)),
        ["structured-final/resultSchema"] = a => StructuredFinal.ResultSchema(Obj(a, 0)!),
        ["structured-final/readFinalResult"] = a => StructuredFinal.ReadFinalResult(Text(a, 0), Text(a, 1)),
        ["activity-events/sanitize"] = a => a.Count > 1 ? ActivityEvents.Sanitize(Text(a, 0), (int)a[1]!.GetValue<double>()) : ActivityEvents.Sanitize(Text(a, 0)),
        ["provider-accounts/accountType"] = a => ProviderAccounts.AccountType(Text(a, 0)),
        ["provider-accounts/accountLabel"] = a => ProviderAccounts.AccountLabel(Text(a, 0)!,
            Obj(a, 1) is { } account ? new ProviderAccounts.AccountMetadata(null, account["type"]?.GetValue<string>() ?? "unread", "", null) : null),
        ["provider-refusal/refusalReason"] = a => ProviderRefusal.RefusalReason(Text(a, 0)),
        ["provider-refusal/activeRefusal"] = a => ProviderRefusal.ActiveRefusal(Obj(a, 0), Text(a, 1)!, a[2]!.GetValue<double>()),
        ["provider-refusal/recordRefusal"] = a => a.Count > 4
            ? ProviderRefusal.RecordRefusal(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!, Text(a, 3)!, a[4]!.GetValue<double>())
            : ProviderRefusal.RecordRefusal(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!, Text(a, 3)!),
        ["provider-diagnostics/diagnoseProvider"] = a => ProviderDiagnostics.DiagnoseProvider(Obj(a, 0)!, Text(a, 1), Node(a, 2)),
        ["briefing/compactState"] = a => Briefing.CompactState(Obj(a, 0)!, Node(a, 1)),
        ["briefing/recentContext"] = a => Briefing.RecentContext(Arr(a, 0)!),
        ["project-history/summarizeTimeline"] = a => ProjectHistory.SummarizeTimeline(Arr(a, 0)!, Obj(a, 1)!),
        ["project-history/checkpointDiff"] = a => a.Count > 3
            ? ProjectHistory.CheckpointDiff(Obj(a, 0)!, Obj(a, 1)!, a[2]!.GetValue<int>(), Arr(a, 3))
            : ProjectHistory.CheckpointDiff(Obj(a, 0)!, Obj(a, 1)!, a[2]!.GetValue<int>()),
        ["activity-evidence/redact"] = a => ActivityEvidence.Redact(Text(a, 0)),
        ["activity-evidence/technicalEvidence"] = a => ActivityEvidence.TechnicalEvidence(Text(a, 0), Obj(a, 1)),
        ["autonomy/createSettings"] = a => Autonomy.CreateSettings(Text(a, 0)!, Text(a, 1)!),
        ["autonomy/pushGuardEnvironment"] = a => Autonomy.PushGuardEnvironment(Obj(a, 0)),
        ["autonomy/nextStep"] = a => Autonomy.NextStep(Obj(a, 0)!),
        ["autonomy/strictDecisionPrompt"] = a => Autonomy.StrictDecisionPrompt(Text(a, 0)!),
        ["autonomy/cleanFinish"] = a => Autonomy.CleanFinish(Arr(a, 0), Obj(a, 1)),
        ["autonomy/evaluationPrompt"] = a => Autonomy.EvaluationPrompt(Obj(a, 0)!),
        ["autonomy/nudgePrompt"] = a => Autonomy.NudgePrompt(Obj(a, 0)!),
        ["autonomy/assignmentPrompt"] = a => Autonomy.AssignmentPrompt(Obj(a, 0)!),
        ["autonomy/parseDecision"] = a => Autonomy.ParseDecision(Text(a, 0)),
        ["autonomy/parseAssignment"] = a => Autonomy.ParseAssignment(Text(a, 0), a[1]!.GetValue<int>(), Node(a, 2)),
        ["autonomy/splitPrompt"] = a => Autonomy.SplitPrompt(Obj(a, 0)!),
        ["autonomy/fitWeights"] = a => Autonomy.FitWeights(Arr(a, 0), Arr(a, 1)),
        ["autonomy/spreadParts"] = a => Autonomy.SpreadParts(Arr(a, 0), Arr(a, 1)),
        ["autonomy/parseSplit"] = a => Autonomy.ParseSplit(Text(a, 0), Obj(a, 1)!),
        ["autonomy/continuePrompt"] = a => Autonomy.ContinuePrompt(Obj(a, 0)!),
        ["autonomy/namedDocuments"] = a => new JsonArray(Autonomy.NamedDocuments(Text(a, 0), Arr(a, 1)!.Select(item => item!.GetValue<string>())).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        ["plan/example"] = _ => Plan.Example(),
        ["plan/isFolderPath"] = a => (JsonNode)JsonValue.Create(Plan.IsFolderPath(Text(a, 0)))!,
        ["plan/isGithubUrl"] = a => (JsonNode)JsonValue.Create(Plan.IsGithubUrl(Text(a, 0)))!,
        ["plan/taskTiming"] = a => Plan.TaskTiming(Obj(a, 0)),
        ["plan/validate"] = a => (JsonNode)JsonValue.Create(Plan.Validate(Node(a, 0)))!,
        ["plan/validateProject"] = a => (JsonNode)JsonValue.Create(Plan.ValidateProject(Node(a, 0)))!,
        ["plan/createProject"] = a => Plan.CreateProject(Node(a, 0), Obj(a, 1), Text(a, 2)!),
        ["plan/restoreProject"] = a => Plan.RestoreProject(Node(a, 0), Obj(a, 1), Obj(a, 2), Text(a, 3)!),
        ["plan/setPlace"] = a => Plan.SetPlace(Node(a, 0), Obj(a, 1)),
        ["plan/resetProject"] = a => Plan.ResetProject(Node(a, 0), Obj(a, 1), Text(a, 2)!),
        ["plan/removeProject"] = a => Plan.RemoveProject(Node(a, 0), Obj(a, 1)),
        ["plan/appendRevision"] = a => Plan.AppendRevision(Obj(a, 0)!, Obj(a, 1), Text(a, 2)!),
        ["plan/startGrill"] = a => a.Count > 2 ? Plan.StartGrill(Obj(a, 0)!, Text(a, 1)!, Obj(a, 2)) : Plan.StartGrill(Obj(a, 0)!, Text(a, 1)!),
        ["plan/startGrillDocs"] = a => a.Count > 3 ? Plan.StartGrillDocs(Obj(a, 0)!, Arr(a, 1), Text(a, 2)!, Obj(a, 3)) : Plan.StartGrillDocs(Obj(a, 0)!, Arr(a, 1), Text(a, 2)!),
        ["plan/grill"] = a => Plan.Grill(Obj(a, 0)!, Obj(a, 1), Text(a, 2)!),
        ["plan/importRevision"] = a => Plan.ImportRevision(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!),
        ["plan/createTask"] = a => Plan.CreateTask(Obj(a, 0)!, Obj(a, 1)),
        ["plan/removeTask"] = a => Plan.RemoveTask(Obj(a, 0)!, Obj(a, 1)),
        ["plan/diff"] = a => Plan.Diff(Obj(a, 0), Obj(a, 1)!),
        ["quota/describe"] = a => Quota.Describe(Node(a, 0)),
        ["quota/html"] = a => (JsonNode)JsonValue.Create(Quota.Html(Node(a, 0)))!,
        ["models/getEnabledModels"] = a => Models.GetEnabledModels(Node(a, 0)),
        ["models/setModelEnabled"] = a => Models.SetModelEnabled(Node(a, 0), Text(a, 1), a[2]!.GetValue<bool>()),
        ["models/validateWorkerClaim"] = a => Models.ValidateWorkerClaim(Node(a, 0), Text(a, 1)),
        ["project-export/sanitize"] = a => ProjectExport.Sanitize(Node(a, 0)),
        ["project-export/continueFrom"] = a => ProjectExport.ContinueFrom(Obj(a, 0)),
        ["execution-next/executionNext"] = a => (JsonNode)JsonValue.Create(ExecutionNext.GetExecutionNext(Obj(a, 0), Obj(a, 1)))!,
        ["execution-next/homeNext"] = a => (JsonNode)JsonValue.Create(ExecutionNext.GetHomeNext(Obj(a, 0)!, Obj(a, 1), Obj(a, 2)))!,
        ["provider-catalog/notInstalledReason"] = a => (JsonNode)JsonValue.Create(ProviderCatalog.NotInstalledReason(Obj(a, 0)!))!,
        ["provider-catalog/providerConfiguration"] = a => ProviderCatalog.ProviderConfiguration(Obj(a, 0)!),
        ["provider-catalog/loadCatalog"] = a =>
        {
            var cat = ProviderCatalog.LoadCatalog(Node(a, 0));
            return new JsonObject
            {
                ["providers"] = new JsonArray(cat.Providers.Select(p => (JsonNode)p.DeepClone()).ToArray()),
                ["models"] = new JsonArray(cat.Models.Select(m => (JsonNode)m.DeepClone()).ToArray())
            };
        },
        ["provider-catalog/providerViews"] = a => new JsonArray(ProviderCatalog.ProviderViews(
            Arr(a, 0)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>(),
            Arr(a, 1)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>(),
            Arr(a, 2)?.OfType<JsonObject>().ToList(),
            Obj(a, 3)).Select(v => (JsonNode)v.DeepClone()).ToArray()),
        ["provider-policy/validateProviderPolicy"] = a => ProviderPolicy.ValidateProviderPolicy(Node(a, 0)),
        ["provider-policy/providerPolicyFor"] = a => ProviderPolicy.ProviderPolicyFor(Obj(a, 0)!, Arr(a, 1), Obj(a, 2)),
        ["model-routing/modelRouteError"] = a => (JsonNode?)JsonValue.Create(ModelRouting.ModelRouteError(Obj(a, 0))),
        ["model-routing/resolveCliModel"] = a => (JsonNode?)JsonValue.Create(ModelRouting.ResolveCliModel(Obj(a, 0))),
        ["model-routing/savingModeError"] = a => (JsonNode?)JsonValue.Create(ModelRouting.SavingModeError(Obj(a, 0), Text(a, 1), a.Count > 2 ? a[2]?.GetValue<int>() : null)),
        ["claude/claudeToolArgs"] = a => new JsonArray(Claude.ClaudeToolArgs(Obj(a, 0)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["claude/parseModels"] = a =>
        {
            var (ok, reason, models) = Claude.ParseModels(a[0] as JsonArray);
            var obj = new JsonObject { ["ok"] = ok };
            if (ok) obj["models"] = new JsonArray(models!.Select(m => (JsonNode)m.DeepClone()).ToArray());
            else obj["reason"] = reason;
            return obj;
        },
        ["claude-usage/parseClaudeUsage"] = a => ClaudeUsage.ParseClaudeUsage(Obj(a, 0)),
        ["repository/githubRepo"] = a => (JsonNode?)JsonValue.Create(Repository.GithubRepo(Text(a, 0))),
        ["repository/repositoryAddress"] = a =>
        {
            var addr = Repository.GetRepositoryAddress(Text(a, 0));
            return addr == null ? null : new JsonObject
            {
                ["provider"] = addr.Provider,
                ["repository"] = addr.Repository,
                ["origin"] = addr.Origin,
                ["baseUrl"] = addr.BaseUrl,
                ["url"] = addr.Url
            };
        },
        ["repository/cleanError"] = a => (JsonNode?)JsonValue.Create(Repository.CleanError(Text(a, 0))),
        ["repository/relation"] = a => (JsonNode?)JsonValue.Create(Repository.Relation(a[0]?.GetValue<int>() ?? 0, a[1]?.GetValue<int>() ?? 0)),
        ["push-email/privateEmailRefused"] = a => (JsonNode?)JsonValue.Create(PushEmail.PrivateEmailRefused(Text(a, 0))),
        ["plan-status/statusFrom"] = a => (JsonNode?)JsonValue.Create(PlanStatus.StatusFrom(Text(a, 0), Obj(a, 1))),
        ["plan-status/withStatus"] = a => PlanStatus.WithStatus(Text(a, 0), Obj(a, 1) ?? new JsonObject()),
        ["plan-status/blockedStatus"] = a => (JsonNode?)JsonValue.Create(PlanStatus.BlockedStatus(Obj(a, 0))),
        ["plan-status/providerError"] = a => PlanStatus.ProviderError(Text(a, 0), a.Count > 1 ? (a[1] is JsonValue ? Text(a, 1) : a[1]?.ToJsonString()) : null, Text(a, 2) ?? ""),
        ["plan-status/notePlanRefusal"] = a =>
        {
            var (plans, status) = PlanStatus.NotePlanRefusal(a[0] as JsonArray, Obj(a, 1), Text(a, 2));
            return new JsonObject { ["plans"] = plans, ["status"] = status };
        },
        ["plan-status/mergePlans"] = a => PlanStatus.MergePlans(a[0] as JsonArray, a.Count > 1 ? a[1] as JsonArray : null),
        ["supervisor/label"] = a => (JsonNode?)JsonValue.Create(Supervisor.Label(Text(a, 0))),
        ["supervisor/advisoryError"] = a => (JsonNode?)JsonValue.Create(Supervisor.AdvisoryError(Obj(a, 0), a.Count > 1 && a[1] is JsonArray ar ? ar.OfType<JsonObject>().ToList() : null)),
        ["supervisor/projectFacts"] = a => Supervisor.ProjectFacts(Obj(a, 0)!, Text(a, 1)!, a.Count > 2 ? a[2]?.GetValue<long>() : null),
        ["activity/activityCapabilities"] = a => Activity.ActivityCapabilities(Obj(a, 0)),
        ["activity/activityMessages"] = a => new JsonArray(Activity.ActivityMessages(Text(a, 0)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["activity/interpretActivity"] = a => (JsonNode?)JsonValue.Create(Activity.InterpretActivity(Text(a, 0), Obj(a, 1), a.Count > 2 ? a[2]?.GetValue<bool>() : null)),
        ["activity/workerReport"] = a => (JsonNode?)JsonValue.Create(Activity.WorkerReport(Text(a, 0))),
        ["activity/activityEvents"] = a => new JsonArray(Activity.ActivityEventsList(Text(a, 0), Obj(a, 1)).Select(e => (JsonNode)e.DeepClone()).ToArray()),
        ["activity-sessions/assignmentSession"] = a => ActivitySessions.AssignmentSession(Obj(a, 0)!),
        ["provider-runtime/providerEnvironment"] = a =>
        {
            var dict = a.Count > 1 && a[1] is JsonObject o ? o.ToDictionary(k => k.Key, v => v.Value?.GetValue<string>() ?? "") : new Dictionary<string, string>();
            var res = ProviderRuntime.ProviderEnvironment(Text(a, 0)!, dict, Text(a, 2));
            var outObj = new JsonObject();
            foreach (var kvp in res) outObj[kvp.Key] = kvp.Value;
            return outObj;
        },
        ["provider-runtime/accountEnvironment"] = a =>
        {
            var dict = a.Count > 2 && a[2] is JsonObject o ? o.ToDictionary(k => k.Key, v => v.Value?.GetValue<string>() ?? "") : new Dictionary<string, string>();
            var res = ProviderRuntime.AccountEnvironment(Text(a, 0)!, Obj(a, 1), dict, Text(a, 3));
            var outObj = new JsonObject();
            foreach (var kvp in res) outObj[kvp.Key] = kvp.Value;
            return outObj;
        },
        ["provider-runtime/launchSpec"] = a =>
        {
            var spec = ProviderRuntime.LaunchSpec(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!, a.Count > 3 && a[3]?.GetValue<bool>() == true, a.Count > 4 && a[4]?.GetValue<bool>() == true);
            var obj = new JsonObject
            {
                ["command"] = spec.Command,
                ["args"] = new JsonArray(spec.Args.Select(arg => (JsonNode)JsonValue.Create(arg)!).ToArray()),
                ["cwd"] = spec.Cwd,
                ["provider"] = spec.Provider,
                ["adapter"] = spec.Adapter,
                ["model"] = spec.Model
            };
            if (spec.Stdin != null) obj["stdin"] = spec.Stdin;
            if (spec.PromptFile != null) obj["promptFile"] = spec.PromptFile;
            return obj;
        },
        ["time-left/roleOf"] = a => (JsonNode?)JsonValue.Create(TimeLeft.RoleOf(Obj(a, 0))),
        ["time-left/stepRecord"] = a => TimeLeft.StepRecord(Obj(a, 0)),
        ["time-left/parseHistory"] = a => new JsonArray(TimeLeft.ParseHistory(Text(a, 0)).Select(r => (JsonNode)r.DeepClone()).ToArray()),
        ["time-left/historyText"] = a => (JsonNode?)JsonValue.Create(TimeLeft.HistoryText(a.Count > 0 && a[0] is JsonArray ar ? ar.OfType<JsonObject>().ToList() : null)),
        ["time-left/estimate"] = a =>
        {
            var obj = a.Count > 0 && a[0] is JsonObject o ? o : new JsonObject();
            var history = obj["history"] as JsonArray;
            var steps = obj["steps"] as JsonArray;
            var now = obj["now"]?.GetValue<string>();
            var histList = history?.OfType<JsonObject>().ToList();
            var stepList = steps?.OfType<JsonObject>().ToList();
            var est = TimeLeft.Estimate(histList, stepList, now);
            if (est == null) return null;
            return new JsonObject
            {
                ["minutes"] = est.Minutes,
                ["endsAt"] = est.EndsAt,
                ["sure"] = est.Sure,
                ["longerThanUsual"] = est.LongerThanUsual
            };
        },
        ["phone-voice/normalizeLanguage"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.NormalizeLanguage(Text(a, 0))),
        ["phone-voice/languageOrDefault"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.LanguageOrDefault(Text(a, 0))),
        ["phone-voice/say"] = a =>
        {
            var vars = a.Count > 2 && a[2] is JsonObject o ? o.ToDictionary(k => k.Key, v => v.Value?.GetValue<string>() ?? "") : null;
            return (JsonNode?)JsonValue.Create(PhoneVoice.Say(Text(a, 0) ?? "", Text(a, 1) ?? "", vars));
        },
        ["phone-voice/telegramAlertLabel"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.TelegramAlertLabel(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["phone-voice/plainAlert"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.PlainAlert(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["phone-voice/stateLabel"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.StateLabel(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["phone-voice/isDetailsRequest"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.IsDetailsRequest(Text(a, 0) ?? "")),
        ["phone-voice/fitTelegram"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.FitTelegram(Text(a, 0) ?? "")),
        ["phone-voice/reasonLine"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.ReasonLine(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["step-outcomes/canonicalRole"] = a => (JsonNode?)JsonValue.Create(StepOutcomes.CanonicalRole(Text(a, 0))),
        ["step-outcomes/canonicalClass"] = a => (JsonNode?)JsonValue.Create(StepOutcomes.CanonicalClass(Text(a, 0))),
        ["step-outcomes/resolveModelMeta"] = a =>
        {
            var m = StepOutcomes.ResolveModelMeta(a.Count > 0 ? a[0] : null);
            return new JsonObject { ["company"] = m.Company, ["class"] = m.Class };
        },
        ["step-outcomes/stepOutcomeRecord"] = a => StepOutcomes.StepOutcomeRecord(Obj(a, 0)),
        ["step-outcomes/outcomesSummary"] = a => new JsonArray(StepOutcomes.OutcomesSummary(a.Count > 0 && a[0] is JsonArray ar ? ar.OfType<JsonObject>().ToList() : null).Select(r => (JsonNode)r.DeepClone()).ToArray()),
        ["step-outcomes/summaryText"] = a => (JsonNode?)JsonValue.Create(StepOutcomes.SummaryText(Obj(a, 0))),
        ["planUsage/unreadPlan"] = a => PlanUsage.UnreadPlan(Text(a, 0)!, Text(a, 1)),
        ["planUsage/emptyPlans"] = _ => PlanUsage.EmptyPlans(),
        ["planUsage/windowOf"] = a => PlanUsage.WindowOf(Node(a, 0), Text(a, 1)!),
        ["planUsage/planExhausted"] = a => (JsonNode)JsonValue.Create(PlanUsage.PlanExhausted(Node(a, 0), Text(a, 1)!))!,
        ["planUsage/parseGrokCredits"] = a => PlanUsage.ParseGrokCredits(Node(a, 0)),
        ["planUsage/parseChatGptUsage"] = a => PlanUsage.ParseChatGptUsage(Node(a, 0)),
        ["plan-file/isNoise"] = a => (JsonNode)JsonValue.Create(PlanFile.IsNoise(Text(a, 0) ?? ""))!,
        ["plan-file/dirtyMessage"] = a => (JsonNode)JsonValue.Create(PlanFile.DirtyMessage(Arr(a, 0)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? []))!,
        ["plan-file/namedBranch"] = a => (JsonNode?)JsonValue.Create(PlanFile.NamedBranch(Text(a, 0))),
        ["plan-file/branchName"] = a => (JsonNode)JsonValue.Create(PlanFile.BranchName(Text(a, 0), Text(a, 1)))!,
        ["plan-file/pullRequestTitle"] = a => (JsonNode)JsonValue.Create(PlanFile.PullRequestTitle(Text(a, 0), Arr(a, 1)?.Select(x => x?.GetValue<string>() ?? "").ToList()))!,
        ["plan-file/crewSection"] = a =>
        {
            var cr = Obj(a, 0);
            var row = cr is null ? null : new PlanFile.CrewRow(
                cr["leadLabel"]?.GetValue<string>() ?? "",
                (cr["crew"] as JsonArray)?.Select(m => new PlanFile.CrewMember(m?["role"]?.GetValue<string>() ?? "", m?["name"]?.GetValue<string>() ?? "")).ToList(),
                (cr["subTasks"] as JsonArray)?.Select(s => new PlanFile.CrewSubTask(s?["id"]?.GetValue<string>() ?? "", s?["title"]?.GetValue<string>() ?? "", s?["assignedModelId"]?.GetValue<string>() ?? "")).ToList(),
                (cr["results"] as JsonArray)?.Select(r => new PlanFile.CrewResult(r?["subTaskId"]?.GetValue<string>() ?? "", r?["modelId"]?.GetValue<string>() ?? "")).ToList());
            var models = Arr(a, 1)?.Select(m => new PlanFile.CrewModel(m?["id"]?.GetValue<string>() ?? "", m?["label"]?.GetValue<string>() ?? "")).ToList();
            return (JsonNode)JsonValue.Create(PlanFile.CrewSection(row, models))!;
        },
        ["plan-file/shortGoal"] = a => (JsonNode)JsonValue.Create(PlanFile.ShortGoal(Text(a, 0)))!,
        ["plan-file/parse"] = a =>
        {
            var p = PlanFile.Parse(Text(a, 0));
            return new JsonObject
            {
                ["goal"] = p.Goal,
                ["base"] = p.Base,
                ["branch"] = p.Branch,
                ["rules"] = p.Rules,
                ["done"] = p.Done,
                ["steps"] = new JsonArray(p.Steps.Select(s => (JsonNode)new JsonObject
                {
                    ["line"] = s.Label,
                    ["done"] = s.Done,
                    ["commit"] = s.Commit
                }).ToArray())
            };
        },
        ["phone/alertForStatus"] = a => (JsonNode?)JsonValue.Create(Phone.AlertForStatus(Text(a, 0), Text(a, 1))),
        ["phone/alertForStep"] = a => (JsonNode?)JsonValue.Create(Phone.AlertForStep(Node(a, 0), Node(a, 1))),
        ["phone/crewOfProject"] = a => Phone.CrewOfProject(Node(a, 0), Node(a, 1), Obj(a, 2)),
        ["phone/escapeHtml"] = a => (JsonNode)JsonValue.Create(Phone.EscapeHtml(Text(a, 0)))!,
        ["phone/formatProgressBar"] = a => (JsonNode)JsonValue.Create(Phone.FormatProgressBar(Node(a, 0), Node(a, 1)))!,
        ["phone/sanitizeText"] = a => (JsonNode)JsonValue.Create(Phone.SanitizeText(Text(a, 0)))!,
        ["phone/sanitizeGoalTitle"] = a => (JsonNode)JsonValue.Create(Phone.SanitizeGoalTitle(Text(a, 0)))!,
        ["phone/formatDuration"] = a => (JsonNode?)JsonValue.Create(Phone.FormatDuration(a.Count > 0 && a[0] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null)),
        ["phone/formatAlertMessage"] = a => (JsonNode)JsonValue.Create(Phone.FormatAlertMessage(Obj(a, 0)!))!,
        ["phone/formatTelegramAlertMessage"] = a => (JsonNode)JsonValue.Create(Phone.FormatTelegramAlertMessage(Obj(a, 0)!))!,
        ["phone/maskToken"] = a => (JsonNode?)JsonValue.Create(Phone.MaskToken(Text(a, 0))),
        ["phone/formatStatusMessage"] = a => (JsonNode)JsonValue.Create(Phone.FormatStatusMessage(Node(a, 0), Text(a, 1)))!,
        ["phone/matchProject"] = a => Phone.MatchProject(Node(a, 0), Text(a, 1)),
        ["phone-ollama/defaultOllamaSettings"] = _ =>
        {
            var s = PhoneOllama.DefaultSettings();
            return new JsonObject { ["enabled"] = s.Enabled, ["baseUrl"] = s.BaseUrl, ["model"] = s.Model };
        },
        ["phone-ollama/normalizeOllamaSettings"] = a =>
        {
            var s = PhoneOllama.NormalizeSettings(Node(a, 0));
            return new JsonObject { ["enabled"] = s.Enabled, ["baseUrl"] = s.BaseUrl, ["model"] = s.Model };
        },
        ["phone-ollama/escapeHtml"] = a => (JsonNode)JsonValue.Create(PhoneOllama.EscapeHtml(Text(a, 0)))!,
        ["phone-ollama/withCaptain"] = a => (JsonNode)JsonValue.Create(PhoneOllama.WithCaptain(Text(a, 0), Text(a, 1)))!,
        ["phone-ollama/isClearMemory"] = a => (JsonNode)JsonValue.Create(PhoneOllama.IsClearMemory(Text(a, 0)))!,
        ["phone-ollama/isBareConfirmation"] = a => (JsonNode)JsonValue.Create(PhoneOllama.IsBareConfirmation(Text(a, 0)))!,
        ["phone-ollama/projectFacts"] = a => PhoneOllama.ProjectFacts(Node(a, 0)),
        ["phone-ollama/buildReplyMessages"] = a =>
        {
            var opts = Obj(a, 0);
            return PhoneOllama.BuildReplyMessages(
                opts?["question"]?.GetValue<string>(),
                opts?["projects"],
                opts?["history"],
                null,
                opts?["model"]?.GetValue<string>(),
                opts?["language"]?.GetValue<string>()
            );
        },
        ["phone-ollama/unavailableReply"] = a => (JsonNode)JsonValue.Create(PhoneOllama.UnavailableReply(Text(a, 0), Text(a, 1)))!,
        ["phone-handover/handoverPlan"] = a => PhoneHandover.HandoverPlan(Obj(a, 0)).ToJsonObject(),
        ["phone-handover/exchangeEvidence"] = a => PhoneHandover.ExchangeEvidence(Obj(a, 0)),
        ["phone-handover-live/redact"] = a => (JsonNode)JsonValue.Create(PhoneHandoverLive.Redact(Text(a, 0)))!,
        ["phone-handover-live/projectsFromPlan"] = a => PhoneHandoverLive.ProjectsFromPlan(Node(a, 0)),
        ["execution/splitSummary"] = a =>
        {
            var subs = Arr(a, 0)!.Select(n => (JsonObject)n!).ToList();
            var why = a.Count > 1 && a[1] is JsonValue v && v.TryGetValue<string>(out var w) ? w : "";
            return (JsonNode)JsonValue.Create(Execution.SplitSummary(subs, id => id, why))!;
        },
        ["execution/noVerdictPlan"] = a =>
        {
            var tries = a.Count > 0 && a[0] is JsonValue v && v.TryGetValue<double>(out var t) ? t : 0;
            return (JsonNode)JsonValue.Create(Execution.NoVerdictPlan(tries))!;
        },
        ["execution/deliveredWithoutVerdict"] = a => Execution.DeliveredWithoutVerdict(Obj(a, 0)!),
        ["orchestrator-order-parsing/looksLikeQuestion"] = a => (JsonNode)JsonValue.Create(OrchestratorOrderParsing.LooksLikeQuestion(Text(a, 0)))!,
        ["orchestrator-order-parsing/looksLikeWork"] = a => (JsonNode)JsonValue.Create(OrchestratorOrderParsing.LooksLikeWork(Text(a, 0)))!,
        ["orchestrator-order-parsing/parseOrder"] = a =>
        {
            var snap = Obj(a, 1) is { } obj ? OrchestratorSnapshot.FromJson(obj) : null;
            var order = OrchestratorOrderParsing.ParseOrder(Text(a, 0), snap, Text(a, 2));
            if (order == null) return null;
            var json = order.ToJsonObject();
            if (order is ChatModeOrder or RejectOrder or SavingModeOrder or LifecycleOrder)
            {
                if (!json.ContainsKey("projectId")) json["projectId"] = null;
            }
            return json;
        },
        ["orchestrator-team-models/normalizeModels"] = a => OrchestratorTeamModels.NormalizeModels(Node(a, 0)),
        ["orchestrator-team-models/usageOf"] = a => OrchestratorTeamModels.UsageOf(Obj(a, 0)),
        ["orchestrator-team-models/leadTier"] = a => (JsonNode)JsonValue.Create(OrchestratorTeamModels.LeadTier(Obj(a, 0)))!,
        ["orchestrator-team-models/leadRanking"] = a => OrchestratorTeamModels.LeadRanking(Arr(a, 0), Arr(a, 1)),
        ["orchestrator-team-models/leadQuality"] = a => OrchestratorTeamModels.LeadQuality(Arr(a, 0)),
        ["orchestrator-team-models/pickWorker"] = a => OrchestratorTeamModels.PickWorker(Arr(a, 0), Obj(a, 1)),
        ["orchestrator-team-models/pickWorkers"] = a => OrchestratorTeamModels.PickWorkers(Arr(a, 0), Obj(a, 1)),
        ["orchestrator-team-models/dispatchPlan"] = a => OrchestratorTeamModels.DispatchPlan(Arr(a, 0), Obj(a, 1), Obj(a, 2)),
        ["orchestrator-team-models/allowedBySavingMode"] = a => (JsonNode)JsonValue.Create(OrchestratorTeamModels.AllowedBySavingMode(Text(a, 0), Obj(a, 1)))!,
        ["orchestrator-team-models/modelClass"] = a => (JsonNode)JsonValue.Create(ModelRouting.ModelClass(Obj(a, 0), a.Count > 1 && a[1] is JsonValue v && v.TryGetValue<int>(out var t) ? t : null))!,
        ["orchestrator-job-building/sliceCount"] = a =>
        {
            var job = ParseJob(Obj(a, 0));
            var facts = ParseFacts(Obj(a, 1));
            var eligible = a.Count > 2 && a[2] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 1;
            return (JsonNode)JsonValue.Create(OrchestratorJobBuilding.SliceCount(job, facts, eligible))!;
        },
        ["orchestrator-job-building/planLines"] = a =>
        {
            var item = ParseProjectItem(Obj(a, 0));
            var facts = ParseFacts(Obj(a, 1));
            var shown = ParseModelCandidate(Obj(a, 2));
            var active = a.Count > 3 && a[3] is JsonValue v && v.TryGetValue<bool>(out var act) && act;
            var extras = ParsePlanExtras(Obj(a, 4));
            var lines = OrchestratorJobBuilding.PlanLines(item, facts, shown, active, extras);
            var arr = new JsonArray();
            foreach (var line in lines) arr.Add(line);
            return arr;
        },
        ["orchestrator-lead-questions/parseGrillQuestions"] = a =>
            new JsonArray(OrchestratorLeadQuestions.ParseGrillQuestionsValue(Node(a, 0)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["orchestrator-lead-questions/grillPrompt"] = a => (JsonNode)JsonValue.Create(
            OrchestratorLeadQuestions.GrillPrompt(
                new OrchestratorLeadQuestions.Item(Obj(a, 0)!),
                Obj(a, 1) is { } f ? new OrchestratorLeadQuestions.Facts(f) : null,
                Arr(a, 2)?.Select(x => x?.GetValue<string>() ?? "").ToArray()))!,
        ["orchestrator-lead-questions/grillDone"] = a => (JsonNode)JsonValue.Create(
            OrchestratorLeadQuestions.GrillDone(Obj(a, 0) is { } o ? new OrchestratorLeadQuestions.Item(o) : null))!,
        ["orchestrator-lead-questions/collectDocs"] = a =>
            new JsonArray(OrchestratorLeadQuestions.CollectDocs(Obj(a, 0) is { } f ? new OrchestratorLeadQuestions.Facts(f) : null)
                .Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["orchestrator-lead-questions/isDocName"] = a => (JsonNode)JsonValue.Create(OrchestratorLeadQuestions.IsDocName(Text(a, 0)))!,
        ["orchestrator-lead-questions/listDocs"] = a =>
            new JsonArray(OrchestratorLeadQuestions.ListDocs(Text(a, 0)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["orchestrator-lead-questions/leadEnvelope"] = a =>
        {
            var env = OrchestratorLeadQuestions.LeadEnvelope(Text(a, 0), Arr(a, 1));
            var obj = new JsonObject
            {
                ["answer"] = env.Answer,
                ["actions"] = env.Actions.DeepClone()
            };
            if (env.Error != null) obj["error"] = env.Error;
            return obj;
        },
        ["orchestrator-lead-questions/answerQuestion"] = a =>
        {
            var res = OrchestratorLeadQuestions.AnswerQuestion(
                Text(a, 0),
                Obj(a, 1) is { } s ? new OrchestratorLeadQuestions.Snapshot(s) : null,
                Text(a, 2));
            var obj = new JsonObject
            {
                ["ok"] = res.Ok,
                ["answer"] = res.Answer,
                ["projectId"] = res.ProjectId
            };
            if (res.Reason != null) obj["reason"] = res.Reason;
            return obj;
        },
        ["orchestrator-lead-questions/briefRow"] = a => (JsonNode)JsonValue.Create(
            OrchestratorLeadQuestions.BriefRow(
                Obj(a, 0) is { } p ? new OrchestratorLeadQuestions.Project(p) : null,
                Text(a, 1)))!,
        ["orchestrator-lead-questions/packState"] = a => OrchestratorLeadQuestions.PackState(
            Obj(a, 0) is { } s ? new OrchestratorLeadQuestions.Snapshot(s) : null,
            Obj(a, 1) is { } p ? new OrchestratorLeadQuestions.Project(p) : null),
        ["orchestrator-lead-questions/evaluationPrompt"] = a => Autonomy.EvaluationPrompt(Obj(a, 0)!),
        ["orchestrator-lead-questions/nudgePrompt"] = a => Autonomy.NudgePrompt(Obj(a, 0)!),
        ["orchestrator-lead-questions/continuePrompt"] = a => Autonomy.ContinuePrompt(Obj(a, 0)!),
        ["orchestrator-lead-questions/splitPrompt"] = a => Autonomy.SplitPrompt(Obj(a, 0)!),
        ["orchestrator-lead-questions/assignmentPrompt"] = a => Autonomy.AssignmentPrompt(Obj(a, 0)!),
        ["orchestrator-lead-questions/strictDecisionPrompt"] = a => Autonomy.StrictDecisionPrompt(Text(a, 0)!),
    };

    // Same JSON: numbers by value (35 and 35.0 are the same), objects by keys, arrays in order.
    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Count == ob.Count && oa.All(pair => ob.TryGetPropertyValue(pair.Key, out var other) && Same(pair.Value, other));
        if (a is JsonArray aa && b is JsonArray ab) return aa.Count == ab.Count && aa.Zip(ab).All(pair => Same(pair.First, pair.Second));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                return double.Parse(va.ToJsonString(), CultureInfo.InvariantCulture) == double.Parse(vb.ToJsonString(), CultureInfo.InvariantCulture);
            return JsonNode.DeepEquals(va, vb);
        }
        return false;
    }
}
