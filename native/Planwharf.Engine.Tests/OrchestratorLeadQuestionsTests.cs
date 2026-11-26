using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Behavioral authority: engine/orchestrator.js and mapped Job 50 JS test suites.
// Preserves the initial part-67 unit cases and extends coverage across all sheet 46c contracts.
public class OrchestratorLeadQuestionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("No open decisions remain.")]
    [InlineData("[]")]
    [InlineData("[null, false, 12, {}, [], \" \" ]")]
    public void ParseGrillQuestions_EmptyOrNonQuestionInput_ReturnsNoQuestions(string? input)
    {
        Assert.Empty(OrchestratorLeadQuestions.ParseGrillQuestions(input));
    }

    [Theory]
    [InlineData("[\"Is this the calculator slice? Recommended: yes.\"]")]
    [InlineData("[\n  \"Is this the calculator slice? Recommended: yes.\"\n]")]
    [InlineData("```json\n[\"Is this the calculator slice? Recommended: yes.\"]\n```")]
    public void ParseGrillQuestions_SingleLineAndMultilineArrays_PreserveRecommendation(string input)
    {
        Assert.Equal(new[] { "Is this the calculator slice? Recommended: yes." },
            OrchestratorLeadQuestions.ParseGrillQuestions(input));
    }

    [Fact]
    public void ParseGrillQuestions_StructuredObjects_UseTitleBodyAndRecommendationAliases()
    {
        const string input = """
            [
              {"title":" Scope ","body":" Ship parser? ","recommend":" yes "},
              {"question":" Keep offline? ","recommended":" yes "},
              {"body":" Keep tests? "},
              {"title":"First?","question":"Ignored?","recommend":"A","recommended":"B"},
              {"recommended":" yes "}
            ]
            """;

        Assert.Equal(new[]
        {
            "Scope: Ship parser? Recommended: yes",
            "Keep offline? Recommended: yes",
            "Keep tests?",
            "First? Recommended: A",
            " Recommended: yes"
        }, OrchestratorLeadQuestions.ParseGrillQuestions(input));
    }

    [Fact]
    public void ParseGrillQuestions_JsonStringsNeedNoQuestionMark_AndInvalidElementsAreFiltered()
    {
        Assert.Equal(new[] { "Decision", "Proceed?" },
            OrchestratorLeadQuestions.ParseGrillQuestions("[null, 7, true, {}, \" Decision \", \" \", \"Proceed?\"]"));
    }

    [Fact]
    public void ParseGrillQuestions_MarkdownFallback_StripsPrefixesAndBold()
    {
        const string input = """
            Introductory statement.
            1. **Ship parser?** Recommended: yes
            2) Keep offline?
            - Keep tests? recommended: yes
            * Keep docs?
            ❓ **Q5** — Keep CLI?
            ❓ **Q6** – Keep API?
            ❓ **Q7** - Keep logs?
            **Recommended: keep defaults**
            Unrelated statement.
            """;

        Assert.Equal(new[]
        {
            "Ship parser? Recommended: yes", "Keep offline?",
            "Keep tests? recommended: yes", "Keep docs?", "Keep CLI?",
            "Keep API?", "Keep logs?", "*Recommended: keep defaults"
        }, OrchestratorLeadQuestions.ParseGrillQuestions(input));
    }

    [Fact]
    public void ParseGrillQuestions_MalformedJson_FallsBackToQuestionLines()
    {
        Assert.Equal(new[] { "Keep offline? Recommended: yes" },
            OrchestratorLeadQuestions.ParseGrillQuestions("[invalid]\r\n1. Keep offline? Recommended: yes\r\nNo decision."));
    }

    [Fact]
    public void ParseGrillQuestions_ValidJsonTakesPrecedenceOverSurroundingQuestions()
    {
        Assert.Equal(new[] { "Inside?" },
            OrchestratorLeadQuestions.ParseGrillQuestions("Outside?\n[\"Inside?\"]\nAfter?"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseGrillQuestions_CapsAtEightAfterFiltering(bool json)
    {
        var expected = Enumerable.Range(1, 8).Select(i => $"Decision {i}?").ToArray();
        var questions = Enumerable.Range(1, 11).Select(i => $"Decision {i}?");
        var input = json
            ? "[null, \" \", " + string.Join(",", questions.Select(q => $"\"{q}\"")) + "]"
            : "Intro\n" + string.Join("\r\n", questions.Select((q, i) => $"{i + 1}. {q}"));

        Assert.Equal(expected, OrchestratorLeadQuestions.ParseGrillQuestions(input));
    }

    [Fact]
    public void ParseGrillQuestions_UnicodeQuestions_PreserveText()
    {
        Assert.Equal(new[] { "日本語を使う? Recommended: sì, è utile 🚢" },
            OrchestratorLeadQuestions.ParseGrillQuestions("[\" 日本語を使う? Recommended: sì, è utile 🚢 \"]"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  Plain reply. \r\n", "Plain reply.")]
    [InlineData("[1,2]", "[1,2]")]
    [InlineData("```json\nPlain reply.\n```", "```json\nPlain reply.\n```")]
    public void LeadEnvelope_PlainText_IsTrimmedWithoutInventingActions(string? input, string expected)
    {
        var result = OrchestratorLeadQuestions.LeadEnvelope(input);

        Assert.Equal(expected, result.Answer);
        Assert.Empty(result.Actions);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("{\"answer\":\"日本語 è 🚢\",\"actions\":[]}")]
    [InlineData("```json\n{\"answer\":\"日本語 è 🚢\",\"actions\":[]}\n```")]
    [InlineData("```JSON\r\n{\"answer\":\"日本語 è 🚢\",\"actions\":[]}\r\n```")]
    public void LeadEnvelope_ValidJsonAndFences_PreserveAnswer(string input)
    {
        var result = OrchestratorLeadQuestions.LeadEnvelope(input);

        Assert.Equal("日本語 è 🚢", result.Answer);
        Assert.Empty(result.Actions);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"answer\":\"ok\"}")]
    [InlineData("{\"answer\":false,\"actions\":[]}")]
    [InlineData("{\"answer\":null,\"actions\":[]}")]
    [InlineData("{\"answer\":\"ok\",\"actions\":{}}")]
    [InlineData("{\"answer\":\"ok\",\"actions\":null}")]
    [InlineData("{\"answer\":\"ok\",\"actions\":[],\"extra\":true}")]
    [InlineData("{\"answer\":\"ok\",\"actions\":[{\"type\":\"dispatch_research\"}],\"extra\":true}")]
    public void LeadEnvelope_SchemaViolation_RejectsAllActionsAndPreservesEvidence(string input)
    {
        var result = OrchestratorLeadQuestions.LeadEnvelope(input);

        Assert.Equal(input, result.Answer);
        Assert.Empty(result.Actions);
        Assert.Equal("Invalid lead action envelope; no workers were dispatched.", result.Error);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"answer\":\"ok\",\"actions\":[}")]
    [InlineData("```json\n{broken\n```")]
    public void LeadEnvelope_MalformedJson_ReturnsUnreadableWithoutThrowing(string input)
    {
        var result = OrchestratorLeadQuestions.LeadEnvelope(input);

        Assert.Equal(input, result.Answer);
        Assert.Empty(result.Actions);
        Assert.Equal("Unreadable lead action envelope; no workers were dispatched.", result.Error);
    }

    [Fact]
    public void DocumentCollection_MissingInputs_ReturnEmpty()
    {
        Assert.Empty(OrchestratorLeadQuestions.ListDocs(null));
        Assert.Empty(OrchestratorLeadQuestions.ListDocs(""));
        Assert.Empty(OrchestratorLeadQuestions.CollectDocs(null));
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    private static JsonArray Arr(string json) => JsonNode.Parse(json)!.AsArray();

    private static OrchestratorLeadQuestions.Model MakeModel(
        string id,
        string? label = null,
        string provider = "codex",
        string cli = "gpt-6-astra",
        string kind = "premium",
        bool enabled = true,
        bool available = true,
        bool? supervisor = null,
        bool? worker = null,
        bool? orchestrator = null,
        string? plan = null,
        double? remaining = null,
        double? cost = null)
    {
        var roles = new JsonObject
        {
            ["orchestrator"] = orchestrator ?? (kind != "local"),
            ["worker"] = worker ?? true,
            ["supervisor"] = supervisor ?? false
        };
        var data = new JsonObject
        {
            ["id"] = id,
            ["label"] = label ?? id,
            ["provider"] = provider,
            ["cli"] = cli,
            ["kind"] = kind,
            ["enabled"] = enabled,
            ["available"] = available,
            ["providerEnabled"] = true,
            ["roles"] = roles
        };
        if (plan is not null) data["plan"] = plan;
        if (remaining is not null) data["remaining"] = remaining.Value;
        if (cost is not null) data["cost"] = cost.Value;
        return new OrchestratorLeadQuestions.Model(data);
    }

    private sealed class FakeFileSystem : OrchestratorLeadQuestions.IFileSystem
    {
        public Dictionary<string, IReadOnlyList<string>> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RealPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ReadTextPaths { get; } = [];
        public Func<string, IReadOnlyList<string>>? OnReadDirectory { get; set; }

        public IReadOnlyList<string> ReadDirectory(string path) =>
            OnReadDirectory is not null ? OnReadDirectory(path)
            : Directories.TryGetValue(path, out var entries) ? entries
            : throw new DirectoryNotFoundException(path);

        public string RealPath(string path) =>
            RealPaths.TryGetValue(path, out var real) ? real : path;

        public bool IsDirectory(string path) => ExistingDirectories.Contains(path);

        public string ReadText(string path)
        {
            ReadTextPaths.Add(path);
            return Files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);
        }
    }

    private sealed class FakeLeadAdapter : OrchestratorLeadQuestions.ILeadModelAdapter
    {
        public List<OrchestratorLeadQuestions.LeadAskInvocation> Invocations { get; } = [];
        public Func<OrchestratorLeadQuestions.LeadAskInvocation, CancellationToken, ValueTask<OrchestratorLeadQuestions.LeadSpokenResponse>>? Handler { get; set; }

        public ValueTask<OrchestratorLeadQuestions.LeadSpokenResponse> AskModelAsync(
            OrchestratorLeadQuestions.LeadAskInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            if (Handler is not null) return Handler(invocation, cancellationToken);
            return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Default reply."));
        }
    }

    [Fact]
    public void ListDocs_RespectsRootAndDocsBoundaryTraversalAndCapOfTwelve()
    {
        var root = Path.Combine("C:", "repo", "alpha");
        var docs = Path.Combine(root, "docs");
        var fs = new FakeFileSystem
        {
            Directories =
            {
                [root] = [".", "..", "README.md", "README", "AGENTS.md", "CLAUDE.md", "SKILL.md", "CONTRIBUTING.md", "src", "notes.txt"],
                [docs] = ["a.md", "b.md", "c.md", "d.md", "e.md", "f.md", "g.md", "h.md", "ignored.txt"]
            },
            ExistingDirectories = { root, docs },
            RealPaths = { [root] = root, [docs] = docs }
        };

        var result = OrchestratorLeadQuestions.ListDocs(root, fs);
        Assert.Equal(12, result.Length);
        Assert.Equal(new[]
        {
            "README.md", "README", "AGENTS.md", "CLAUDE.md", "SKILL.md", "CONTRIBUTING.md",
            "docs/a.md", "docs/b.md", "docs/c.md", "docs/d.md", "docs/e.md", "docs/f.md"
        }, result);

        // Symlink or junction escaping folder boundary must be rejected.
        fs.RealPaths[docs] = Path.Combine("C:", "outside", "docs");
        var bounded = OrchestratorLeadQuestions.ListDocs(root, fs);
        Assert.Equal(new[] { "README.md", "README", "AGENTS.md", "CLAUDE.md", "SKILL.md", "CONTRIBUTING.md" }, bounded);

        fs.OnReadDirectory = _ => throw new UnauthorizedAccessException("denied");
        Assert.Empty(OrchestratorLeadQuestions.ListDocs(root, fs));
    }

    [Fact]
    public void CollectDocs_PrefersExplicitDocs_FallsBackToNames_AndCapsAtTwelve()
    {
        var explicitFacts = new OrchestratorLeadQuestions.Facts(Obj("""
            {
              "docs": [" README.md ", " ", "docs/a.md", "d3.md", "d4.md", "d5.md", "d6.md", "d7.md", "d8.md", "d9.md", "d10.md", "d11.md", "d12.md", "d13.md"],
              "names": ["CLAUDE.md"]
            }
            """));
        var explicitDocs = OrchestratorLeadQuestions.CollectDocs(explicitFacts);
        Assert.Equal(12, explicitDocs.Length);
        Assert.Equal("README.md", explicitDocs[0]);
        Assert.Equal("d12.md", explicitDocs[^1]);

        var nameFacts = new OrchestratorLeadQuestions.Facts(Obj("""
            {
              "names": ["index.js", "package.json", "README.md", "CLAUDE.md", "docs\\spec.md", "docs/other.txt",
                        "docs/1.md", "docs/2.md", "docs/3.md", "docs/4.md", "docs/5.md", "docs/6.md", "docs/7.md",
                        "docs/8.md", "docs/9.md", "docs/10.md", "docs/11.md"]
            }
            """));
        var fromNames = OrchestratorLeadQuestions.CollectDocs(nameFacts);
        Assert.Equal(12, fromNames.Length);
        Assert.Equal("README.md", fromNames[0]);
        Assert.Equal("docs\\spec.md", fromNames[2]);
        Assert.Equal("docs/9.md", fromNames[^1]);
    }

    [Fact]
    public void LinkedFolder_RejectsRelativeMissingOrThrowingPaths()
    {
        var root = Path.Combine("C:", "repo", "alpha");
        var fs = new FakeFileSystem { RealPaths = { [root] = root }, ExistingDirectories = { root } };
        Assert.Null(OrchestratorLeadQuestions.LinkedFolder(null, fs));
        Assert.Null(OrchestratorLeadQuestions.LinkedFolder("relative/path", fs));
        Assert.Equal(root, OrchestratorLeadQuestions.LinkedFolder(root, fs));
        fs.ExistingDirectories.Remove(root);
        Assert.Null(OrchestratorLeadQuestions.LinkedFolder(root, fs));
    }

    [Fact]
    public void ParseGrillQuestionsValue_And_GrillDone_MatchJavaScriptRules()
    {
        Assert.Empty(OrchestratorLeadQuestions.ParseGrillQuestionsValue(JsonValue.Create(42)));
        Assert.Equal(new[] { "Ready?" }, OrchestratorLeadQuestions.ParseGrillQuestionsValue(JsonValue.Create("[\"Ready?\"]")));

        Assert.False(OrchestratorLeadQuestions.GrillDone(null));
        Assert.False(OrchestratorLeadQuestions.GrillDone(new OrchestratorLeadQuestions.Item(Obj("""{"revisions":[{"mode":"manual","title":"R1"}]}"""))));
        Assert.True(OrchestratorLeadQuestions.GrillDone(new OrchestratorLeadQuestions.Item(Obj("""{"revisions":[{"mode":"grill","title":"R1"}]}"""))));
        Assert.True(OrchestratorLeadQuestions.GrillDone(new OrchestratorLeadQuestions.Item(Obj("""{"revisions":[{"mode":"manual","title":"Grill me"}]}"""))));
        Assert.True(OrchestratorLeadQuestions.GrillDone(new OrchestratorLeadQuestions.Item(Obj("""{"revisions":[{"mode":"manual","title":"Grill with the docs"}]}"""))));
    }

    [Fact]
    public void GrillPrompt_BuildsExactFullAndMinimalSections()
    {
        var fullItem = new OrchestratorLeadQuestions.Item(Obj("""
            {
              "project": { "name": "Demo", "folder": "C:/repo/demo" },
              "revisions": [
                { "direction": "Old goal" },
                { "direction": "Ship the core." }
              ]
            }
            """));
        var files = Enumerable.Range(1, 14).Select(i => $"f{i}.cs").ToArray();
        var facts = new OrchestratorLeadQuestions.Facts(new JsonObject
        {
            ["lastCommit"] = "abc123 init",
            ["names"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray())
        });
        var fullPrompt = OrchestratorLeadQuestions.GrillPrompt(fullItem, facts, ["README.md", "docs/spec.md"]);
        var expectedFull = string.Join("\n\n", new[]
        {
            "You are interviewing the user about this Planwharf project before any planning.",
            "Ask only what blocks a good plan: the open decisions at the frontier, in one round. Word each question so yes accepts your recommended answer.",
            "Project: Demo",
            "Goal: Ship the core.",
            "Folder: C:/repo/demo",
            "Latest commit: abc123 init",
            "Top files: " + string.Join(", ", files.Take(12)),
            "Docs: README.md, docs/spec.md",
            "Return ONLY a JSON array of 3 to 6 strings.",
            "Each string is one frontier question about THIS project, with \"Recommended: ...\" in the same string.",
            "Do not ask for facts listed above. Do not implement."
        });
        Assert.Equal(expectedFull, fullPrompt);

        var minItem = new OrchestratorLeadQuestions.Item(Obj("""{"project":{"name":"Min"}}"""));
        var minPrompt = OrchestratorLeadQuestions.GrillPrompt(minItem, null, null);
        var expectedMin = string.Join("\n\n", new[]
        {
            "You are interviewing the user about this Planwharf project before any planning.",
            "Ask only what blocks a good plan: the open decisions at the frontier, in one round. Word each question so yes accepts your recommended answer.",
            "Project: Min",
            "Return ONLY a JSON array of 3 to 6 strings.",
            "Each string is one frontier question about THIS project, with \"Recommended: ...\" in the same string.",
            "Do not ask for facts listed above. Do not implement."
        });
        Assert.Equal(expectedMin, minPrompt);
    }

    [Fact]
    public void AnswerQuestion_CoversAllDeterministicClassificationsAndConstant()
    {
        Assert.Equal("Orchestrator", OrchestratorLeadQuestions.OrchName);

        var empty = OrchestratorLeadQuestions.AnswerQuestion("  \t ", new OrchestratorLeadQuestions.Snapshot(new JsonObject()));
        Assert.False(empty.Ok);
        Assert.Equal("empty question", empty.Reason);
        Assert.Equal("", empty.Answer);
        Assert.Null(empty.ProjectId);

        var noProjects = OrchestratorLeadQuestions.AnswerQuestion("Status?", new OrchestratorLeadQuestions.Snapshot(Obj("""{"orchestratorLabel":"Astra","projects":[]}""")));
        Assert.True(noProjects.Ok);
        Assert.Equal("Astra: no projects in the notebook.", noProjects.Answer);
        Assert.Null(noProjects.ProjectId);

        var snap = new OrchestratorLeadQuestions.Snapshot(Obj("""
            {
              "orchestratorLabel": "Astra",
              "plans": [
                {"id":"ChatGPT","label":"ChatGPT","readable":true,"exhausted":false,"pct":7,"leftPct":93,"windows":[{"name":"5-hour","pct":0,"leftPct":100},{"name":"weekly","pct":7,"leftPct":93}]},
                {"id":"Grok","label":"Grok","readable":true,"exhausted":true,"pct":100,"leftPct":0,"windows":[]},
                {"id":"Gemini","label":"Gemini","readable":false,"reason":"No Gemini login"}
              ],
              "projects": [
                {
                  "id": "demo",
                  "name": "Demo",
                  "label": "Watching",
                  "orchestratorDoing": "Monitoring 2 workers (1 alive).",
                  "jobTitle": "Implement parser",
                  "waitingFor": "worker completion",
                  "next": "Run verification.",
                  "monitoring": { "alive": 1, "workers": 2, "folderFiles": 4 },
                  "workerTail": "first line\nWriting parser.cs",
                  "dispatch": [
                    { "label": "GPT-5.6 Luna", "modelId": "luna", "slice": "Implement", "activity": "Reading docs.", "pid": 9, "alive": true },
                    { "label": "Grok Fast", "modelId": "grok-fast", "slice": "Verify", "pid": 10, "alive": false }
                  ]
                },
                { "id": "beta", "name": "Beta", "label": "Idle" }
              ]
            }
            """));

        var fleet = OrchestratorLeadQuestions.AnswerQuestion("Show all projects", snap);
        Assert.Null(fleet.ProjectId);
        Assert.Contains("Speaking as Astra. Demo is Watching.", fleet.Answer);
        Assert.Contains("Speaking as Astra. Beta is Idle.", fleet.Answer);

        var worker = OrchestratorLeadQuestions.AnswerQuestion("What is Luna doing?", snap, "demo");
        Assert.Equal("demo", worker.ProjectId);
        Assert.Equal("Astra on Demo: GPT-5.6 Luna has slice \"Implement\". Reading docs. Process 9 is alive.", worker.Answer);

        var split = OrchestratorLeadQuestions.AnswerQuestion("Why two workers?", snap, "demo");
        Assert.Equal("Astra split Demo into 2 slices: GPT-5.6 Luna — Implement; Grok Fast — Verify.", split.Answer);
        var single = OrchestratorLeadQuestions.AnswerQuestion("Perche quanti worker?", snap, "beta");
        Assert.Equal("Astra did not split Beta. One worker on the whole job.", single.Answer);

        var quota = OrchestratorLeadQuestions.AnswerQuestion("How much quota is left?", snap, "demo");
        Assert.Equal("Astra usage: ChatGPT: 5-hour 0% used (100% left), weekly 7% used (93% left); Grok: exhausted (100% used (0% left)); Gemini: No Gemini login.", quota.Answer);

        var modelQuotaSnap = new OrchestratorLeadQuestions.Snapshot(Obj("""
            {
              "models": [{"label":"Local","used":10,"limit":50}],
              "projects": [{"id":"demo","name":"Demo","label":"Idle"}]
            }
            """));
        Assert.Equal("Orchestrator usage: Local: 20% used (80% left).",
            OrchestratorLeadQuestions.AnswerQuestion("usage?", modelQuotaSnap, "demo").Answer);

        var monitor = OrchestratorLeadQuestions.AnswerQuestion("Please monitor alive workers", snap, "demo");
        Assert.Equal("Astra: Monitoring 2 workers (1 alive). 1/2 processes alive, 4 files. Waiting for: worker completion.", monitor.Answer);

        var next = OrchestratorLeadQuestions.AnswerQuestion("What is next?", snap, "demo");
        Assert.Equal("Astra on Demo: waiting for worker completion. Next: Run verification.", next.Answer);

        var log = OrchestratorLeadQuestions.AnswerQuestion("Show worker log output", snap, "demo");
        Assert.Equal("Astra last worker output on Demo: Writing parser.cs", log.Answer);

        var fallback = OrchestratorLeadQuestions.AnswerQuestion("Tell me about Demo", snap, "demo");
        Assert.Contains("Speaking as Astra. Demo is Watching.", fallback.Answer);
        Assert.Contains("Last worker output: Writing parser.cs", fallback.Answer);
    }

    [Fact]
    public void LeadEnvelope_ExplicitActions_ClonesWithoutReparsingText()
    {
        var actions = Arr("""[{"type":"dispatch_research","projectId":"demo"}]""");
        var envelope = OrchestratorLeadQuestions.LeadEnvelope("ignored {broken", actions);
        Assert.Equal("ignored {broken", envelope.Answer);
        Assert.Null(envelope.Error);
        Assert.Single(envelope.Actions);
        Assert.NotSame(actions, envelope.Actions);
    }

    [Fact]
    public void PackState_IsolatesFocusedProject_EnforcesCaps_AndNeverReadsStateFile()
    {
        var alphaDir = Path.Combine("C:", "repo", "alpha");
        var logPath = Path.Combine(alphaDir, "worker.log");
        var stateFile = Path.Combine(alphaDir, "orchestrator.json");
        var longLog = new string('x', 1700) + "\nFinal tail line";
        var fs = new FakeFileSystem
        {
            RealPaths = { [alphaDir] = alphaDir },
            ExistingDirectories = { alphaDir },
            Files =
            {
                [logPath] = longLog,
                [stateFile] = "SECRET-SHOULD-NOT-BE-READ"
            }
        };

        var open = new JsonArray(Enumerable.Range(1, 15).Select(i => (JsonNode?)JsonValue.Create($"open-{i}")).ToArray());
        var plan = new JsonArray(Enumerable.Range(1, 20).Select(i => (JsonNode?)JsonValue.Create($"step-{i}")).ToArray());
        var cpPlan = new JsonArray(Enumerable.Range(1, 15).Select(i => (JsonNode?)JsonValue.Create($"cp-{i}")).ToArray());
        var dirty = new JsonArray(Enumerable.Range(1, 15).Select(i => (JsonNode?)JsonValue.Create($"file-{i}.cs")).ToArray());

        var alpha = new OrchestratorLeadQuestions.Project(new JsonObject
        {
            ["id"] = "alpha",
            ["name"] = "Alpha",
            ["folder"] = alphaDir,
            ["status"] = "watching",
            ["label"] = "Watching",
            ["savingMode"] = "safe",
            ["open"] = open,
            ["plan"] = plan,
            ["notes"] = Arr("""["alpha-note"]"""),
            ["checkpoint"] = new JsonObject { ["reason"] = "pause", ["plan"] = cpPlan },
            ["facts"] = new JsonObject { ["git"] = true, ["lastCommit"] = "abc", ["fileCount"] = 7, ["dirty"] = dirty },
            ["dispatch"] = Arr($$"""[{"modelId":"m1","label":"M1","slice":"Build","pid":12,"alive":true,"logPath":"{{logPath.Replace("\\", "\\\\")}}"}]""")
        });

        var snap = new OrchestratorLeadQuestions.Snapshot(Obj("""
            {
              "orchestratorId": "astra",
              "orchestratorLabel": "Astra",
              "mode": "auto",
              "projects": [
                { "id": "alpha", "name": "Alpha", "status": "watching", "jobTitle": "Alpha job" },
                { "id": "beta", "name": "Beta", "status": "idle", "jobTitle": "beta-private-job", "notes": ["beta-private-note"] }
              ],
              "researches": [
                { "id": "r-alpha", "projectId": "alpha", "title": "Alpha research" },
                { "id": "r-beta", "projectId": "beta", "title": "beta-research-marker" }
              ],
              "execution": {
                "megaTasks": [
                  { "id": "mt-a", "projectId": "alpha" },
                  { "id": "mt-b", "projectId": "beta", "title": "beta-private-mega" }
                ],
                "subTasks": [
                  { "id": "st-a", "megaTaskId": "mt-a" },
                  { "id": "st-b", "megaTaskId": "mt-b" }
                ],
                "results": [
                  { "resultId": "res-a", "megaTaskId": "mt-a" },
                  { "resultId": "res-b", "megaTaskId": "mt-b" }
                ],
                "checkpoints": [
                  { "id": "cp-a", "projectId": "alpha" },
                  { "id": "cp-b", "projectId": "beta" }
                ]
              }
            }
            """));

        var packed = OrchestratorLeadQuestions.PackState(snap, alpha, stateFile, fs);
        var serialized = packed.ToJsonString();
        Assert.DoesNotContain("beta-private", serialized);
        Assert.DoesNotContain("beta-research-marker", serialized);
        Assert.DoesNotContain("mt-b", serialized);
        Assert.DoesNotContain("st-b", serialized);
        Assert.DoesNotContain("res-b", serialized);
        Assert.DoesNotContain("cp-b", serialized);
        Assert.DoesNotContain(stateFile, fs.ReadTextPaths);

        Assert.Empty(packed["others"]!.AsArray());
        Assert.Single(packed["researches"]!.AsArray());
        Assert.Equal("r-alpha", packed["researches"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(12, packed["focus"]!["open"]!.AsArray().Count);
        Assert.Equal(16, packed["focus"]!["plan"]!.AsArray().Count);
        Assert.Equal(12, packed["focus"]!["checkpoint"]!["plan"]!.AsArray().Count);
        Assert.Equal(12, packed["focus"]!["dirty"]!.AsArray().Count);
        Assert.Equal("safe", packed["focus"]!["savingMode"]!.GetValue<string>());
        Assert.Equal("Final tail line", packed["focus"]!["workers"]![0]!["log"]!.GetValue<string>());

        var unfocused = OrchestratorLeadQuestions.PackState(snap, null, stateFile, fs);
        Assert.Null(unfocused["focus"]);
        Assert.Equal(2, unfocused["others"]!.AsArray().Count);
        Assert.Equal("beta-private-job", unfocused["others"]![1]!["job"]!.GetValue<string>());
        Assert.DoesNotContain("beta-private-note", unfocused.ToJsonString());
    }

    [Fact]
    public async Task ComposeGrillRoundAsync_UsesLeadDocsAndWorkPlan_AndFallsBackDeterministically()
    {
        var root = Path.Combine("C:", "repo", "grill");
        var planPath = Path.Combine(root, ".planwharf", "plan.md");
        var fs = new FakeFileSystem
        {
            RealPaths = { [root] = root },
            ExistingDirectories = { root },
            Files = { [planPath] = "# Plan\nShip safely." }
        };
        var item = new OrchestratorLeadQuestions.Item(new JsonObject
        {
            ["project"] = new JsonObject { ["id"] = "p1", ["name"] = "Grill Demo", ["folder"] = root },
            ["revisions"] = Arr("""[{"direction":"Ship the parser"}]""")
        });
        var facts = new OrchestratorLeadQuestions.Facts(Obj("""{"names":["README.md","src/a.cs"],"docs":["README.md","docs/spec.md"]}"""));
        var lead = MakeModel("astra", "GPT-6 Astra");
        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var adapter = new FakeLeadAdapter
        {
            Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse(
                """{"answer":"[\"First frontier question? Recommended: yes.\"]","actions":[]}"""))
        };
        var services = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => lead,
            FileSystem = fs,
            OnEffect = effects.Add
        };

        var result = await OrchestratorLeadQuestions.ComposeGrillRoundAsync(item, facts, "docs", adapter, services);
        Assert.Equal(new[] { "First frontier question? Recommended: yes." }, result);
        var inv = Assert.Single(adapter.Invocations);
        Assert.False(inv.AllowActions);
        Assert.Equal("docs", inv.ClarificationKind);
        Assert.Contains("Docs: README.md, docs/spec.md", inv.Question);
        Assert.Equal("# Plan\nShip safely.", inv.State["workPlan"]!.GetValue<string>());
        Assert.Equal(new[] { "ledger_start", "ledger_finish" }, effects.Select(e => e.Kind).ToArray());
        Assert.Equal("COMPLETE", effects[1].Payload["status"]!.GetValue<string>());

        // Failure and missing-adapter cases fall back to Plan.GrillFrontier without throwing.
        effects.Clear();
        adapter.Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("", Failure: "provider"));
        var failedFallback = await OrchestratorLeadQuestions.ComposeGrillRoundAsync(item, facts, "me", adapter, services);
        Assert.NotEmpty(failedFallback);
        Assert.Equal("FAILED", effects.Last().Payload["status"]!.GetValue<string>());

        var noAdapterFallback = await OrchestratorLeadQuestions.ComposeGrillRoundAsync(item, facts, "me", null, services);
        Assert.NotEmpty(noAdapterFallback);

        // Cancellation after a token-ignoring adapter still cancels and records BLOCKED.
        effects.Clear();
        using var cts = new CancellationTokenSource();
        adapter.Handler = (_, _) =>
        {
            cts.Cancel();
            return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("[\"Ignored?\"]"));
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OrchestratorLeadQuestions.ComposeGrillRoundAsync(item, facts, "me", adapter, services, cts.Token));
        Assert.Equal("BLOCKED", effects.Last().Payload["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReviewSnapshotAsync_EnforcesSupervisorEligibilityWithoutSubstitution()
    {
        var sup = MakeModel("sup-api", "Supervisor API", provider: "api", cli: "api-model", supervisor: true, remaining: 10, cost: 2);
        var backupSup = MakeModel("sup-backup", "Backup Sup", provider: "api", cli: "api-backup", supervisor: true, remaining: 10, cost: 2);
        var ineligibleCodex = MakeModel("sup-codex", "Codex Sup", provider: "codex", cli: "gpt-6-astra", supervisor: true);
        var disabledSup = MakeModel("sup-off", "Disabled", provider: "api", cli: "api-off", enabled: false, supervisor: true);
        var tooExpensive = MakeModel("sup-broke", "Broke", provider: "api", cli: "api-broke", supervisor: true, remaining: 1, cost: 5);
        var roster = new[] { sup, backupSup, ineligibleCodex, disabledSup, tooExpensive };

        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var adapter = new FakeLeadAdapter
        {
            Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Looks healthy."))
        };
        var services = new OrchestratorLeadQuestions.LeadServices { OnEffect = effects.Add };

        var ok = await OrchestratorLeadQuestions.ReviewSnapshotAsync(
            new OrchestratorLeadQuestions.SupervisorReviewRequest("p1", "sup-api", "Review?", Obj("""{"megaTaskId":"mt-1"}"""), Arr("[]"), roster),
            adapter, services);

        Assert.Equal("Looks healthy.", ok.Answer);
        Assert.Equal("api", ok.Provider);
        Assert.Equal("api-model", ok.Model);
        var inv = Assert.Single(adapter.Invocations);
        Assert.False(inv.AllowActions);
        Assert.Equal("supervisor", inv.Purpose);
        Assert.Equal(new[] { "ledger_start", "usage", "ledger_finish" }, effects.Select(e => e.Kind).ToArray());
        Assert.Equal(2, effects[1].Payload["cost"]!.GetValue<double>());

        foreach (var badId in new[] { "missing", "sup-codex", "sup-off", "sup-broke" })
        {
            var err = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await OrchestratorLeadQuestions.ReviewSnapshotAsync(
                    new OrchestratorLeadQuestions.SupervisorReviewRequest("p1", badId, "Review?", new JsonObject(), new JsonArray(), roster),
                    adapter, services));
            Assert.Contains("No fallback was used.", err.Message);
        }
        Assert.Single(adapter.Invocations);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await OrchestratorLeadQuestions.ReviewSnapshotAsync(
                new OrchestratorLeadQuestions.SupervisorReviewRequest("p1", "sup-api", "Review?", new JsonObject(), new JsonArray(), roster),
                null, services));

        // Cancellation after a token-ignoring adapter still cancels and records BLOCKED.
        effects.Clear();
        using var cts = new CancellationTokenSource();
        adapter.Handler = (_, _) =>
        {
            cts.Cancel();
            return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Late answer"));
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OrchestratorLeadQuestions.ReviewSnapshotAsync(
                new OrchestratorLeadQuestions.SupervisorReviewRequest("p1", "sup-api", "Review?", new JsonObject(), new JsonArray(), roster),
                adapter, services, cts.Token));
        Assert.Equal("BLOCKED", effects.Last().Payload["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AskLeadDecisionAsync_StrictRetry_IsolatesInputSettings_CapsAssumptionsAt50_AndPreparesNext()
    {
        var lead = MakeModel("lead-1", "Lead One", provider: "codex", cli: "gpt-6-astra");
        var item = new OrchestratorLeadQuestions.Item(Obj("""{"project":{"id":"p1","name":"Demo"},"revisions":[{"direction":"Ship"}]}"""));
        var initialAssumptions = new JsonArray(Enumerable.Range(1, 49).Select(i => (JsonNode?)JsonValue.Create($"old-{i}")).ToArray());
        var inputSettings = new JsonObject
        {
            ["assumptions"] = initialAssumptions,
            ["approved"] = new JsonArray()
        };
        var inputSnapshot = inputSettings.ToJsonString();

        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var preparedRequests = new List<OrchestratorLeadQuestions.TaskPreparationRequest>();
        var adapter = new FakeLeadAdapter
        {
            Handler = (inv, _) =>
            {
                if (inv.Question == "Evaluate?")
                    return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Unreadable prose reply."));
                Assert.Equal(Autonomy.StrictDecisionPrompt("Evaluate?"), inv.Question);
                return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse(
                    """{"done":false,"next":"Add parity tests","assumptions":["new-1","new-2","new-3"],"summary":"First part done."}"""));
            }
        };
        var services = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => lead,
            OnEffect = effects.Add,
            PrepareTaskWithContext = (req, _) =>
            {
                preparedRequests.Add(req);
                return ValueTask.FromResult(Obj("""{"ok":true,"task":{"id":"mt-2","title":"Add parity tests"}}"""));
            }
        };

        var result = await OrchestratorLeadQuestions.AskLeadDecisionAsync(
            new OrchestratorLeadQuestions.LeadDecisionRequest("p1", item, inputSettings, "Evaluate?", [lead], EvalMegaTaskId: "mt-1"),
            adapter, services);

        Assert.True(result.Ok);
        Assert.Null(result.StopReason);
        Assert.Equal(2, adapter.Invocations.Count);
        Assert.Equal(inputSnapshot, inputSettings.ToJsonString());

        var assumptions = result.Settings!["assumptions"]!.AsArray();
        Assert.Equal(50, assumptions.Count);
        Assert.Equal("old-3", assumptions[0]!.GetValue<string>());
        Assert.Equal("new-3", assumptions[^1]!.GetValue<string>());

        var req = Assert.Single(preparedRequests);
        Assert.Equal("p1", req.ProjectId);
        Assert.Equal("Add parity tests", req.Question);
        Assert.Null(req.GoldModel);
        Assert.Contains(effects, e => e.Kind == "lead_summary" && e.Payload["megaTaskId"]!.GetValue<string>() == "mt-1");
        Assert.Contains(effects, e => e.Kind == "assumption");
        Assert.Contains(effects, e => e.Kind == "prepare_task");
    }

    [Fact]
    public async Task AskLeadDecisionAsync_RefusalAndCreditsHandover_SwitchesProvidersUpToThreeRounds()
    {
        var codexLead = MakeModel("lead-codex", "Codex Lead", provider: "codex", cli: "gpt-6-astra");
        var grokLead = MakeModel("lead-grok", "Grok Lead", provider: "grok", cli: "grok-4.6");
        var agyLead = MakeModel("lead-agy", "Agy Lead", provider: "antigravity", cli: "gemini-4");
        var roster = new[] { codexLead, grokLead, agyLead };
        var item = new OrchestratorLeadQuestions.Item(Obj("""{"project":{"id":"p1","name":"Demo"},"revisions":[]}"""));

        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var adapter = new FakeLeadAdapter
        {
            Handler = (inv, _) =>
            {
                var id = inv.Model.Data["id"]!.GetValue<string>();
                if (id == "lead-codex")
                    return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse(
                        "This account requires usage credits to continue.", Failure: "provider"));
                if (id == "lead-grok")
                    throw new InvalidOperationException("grok crashed");
                return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("""{"done":true,"next":null}"""));
            }
        };
        var services = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => codexLead,
            Now = () => "2026-11-26T12:00:00Z",
            NowMilliseconds = () => 1_795_694_400_000,
            OnEffect = effects.Add
        };

        var result = await OrchestratorLeadQuestions.AskLeadDecisionAsync(
            new OrchestratorLeadQuestions.LeadDecisionRequest("p1", item, new JsonObject(), "Continue?", roster),
            adapter, services);

        Assert.True(result.Ok);
        Assert.Equal("GOAL_REACHED", result.StopReason);
        Assert.Equal(new[] { "lead-codex", "lead-grok", "lead-agy" },
            adapter.Invocations.Select(i => i.Model.Data["id"]!.GetValue<string>()).ToArray());
        Assert.Contains(effects, e => e.Kind == "refusal" && e.Payload["modelId"]!.GetValue<string>() == "lead-codex");
        Assert.Contains(effects, e => e.Kind == "credits_needed" && e.Payload["modelId"]!.GetValue<string>() == "lead-codex");
        Assert.Contains(effects, e => e.Kind == "stop_autonomy" && e.Payload["reason"]!.GetValue<string>() == "GOAL_REACHED");
    }

    [Fact]
    public async Task AskLeadDecisionAsync_EnforcesMegaTaskLimit_MissingAdapter_AndCancellation()
    {
        var lead = MakeModel("lead-1", "Lead One", provider: "codex", cli: "gpt-6-astra");
        var item = new OrchestratorLeadQuestions.Item(Obj("""{"project":{"id":"p1","name":"Demo"},"revisions":[]}"""));
        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();

        var noAdapter = await OrchestratorLeadQuestions.AskLeadDecisionAsync(
            "p1", item, new JsonObject(), "Evaluate?", null, [lead],
            new OrchestratorLeadQuestions.LeadServices { OnEffect = effects.Add });
        Assert.False(noAdapter.Ok);
        Assert.Equal("NO_LEAD_AVAILABLE", noAdapter.StopReason);

        var approved = new JsonArray(Enumerable.Range(1, Autonomy.LIMITS.MaxMegaTasks).Select(i => (JsonNode?)JsonValue.Create($"m{i}")).ToArray());
        var prepareCalled = false;
        var adapter = new FakeLeadAdapter
        {
            Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("""{"done":false,"next":"Another task"}"""))
        };
        var services = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => lead,
            PrepareTaskWithContext = (_, _) => { prepareCalled = true; return ValueTask.FromResult(Obj("""{"ok":true}""")); }
        };
        var limited = await OrchestratorLeadQuestions.AskLeadDecisionAsync(
            "p1", item, new JsonObject { ["approved"] = approved }, "Evaluate?", adapter, [lead], services);
        Assert.True(limited.Ok);
        Assert.Equal("MEGATASK_LIMIT", limited.StopReason);
        Assert.False(prepareCalled);

        // Stale IsCurrent suppresses task preparation cleanly.
        var current = true;
        adapter.Handler = (_, _) =>
        {
            current = false;
            return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("""{"done":false,"next":"Stale task"}"""));
        };
        var stale = await OrchestratorLeadQuestions.AskLeadDecisionAsync(
            new OrchestratorLeadQuestions.LeadDecisionRequest("p1", item, new JsonObject(), "Evaluate?", [lead], IsCurrent: () => current),
            adapter, services);
        Assert.False(stale.Ok);
        Assert.Equal("cancelled", stale.FailureReason);
        Assert.False(prepareCalled);

        // Cancellation after token-ignoring adapter reply cancels and emits BLOCKED.
        effects.Clear();
        services = new OrchestratorLeadQuestions.LeadServices { LeadModel = () => lead, OnEffect = effects.Add };
        using var cts = new CancellationTokenSource();
        adapter.Handler = (_, _) =>
        {
            cts.Cancel();
            return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("""{"done":true}"""));
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OrchestratorLeadQuestions.AskLeadDecisionAsync(
                new OrchestratorLeadQuestions.LeadDecisionRequest("p1", item, new JsonObject(), "Evaluate?", [lead]),
                adapter, services, cts.Token));
        Assert.Equal("BLOCKED", effects.Last().Payload["status"]!.GetValue<string>());
    }

    private static OrchestratorLeadQuestions.Snapshot TwoProjectSnap(string? selectedProjectId = null, string? leadId = "astra") =>
        new(new JsonObject
        {
            ["orchestratorId"] = leadId,
            ["orchestratorLabel"] = "Astra",
            ["selectedProjectId"] = selectedProjectId,
            ["projects"] = Arr("""
                [
                  { "id": "alpha", "name": "Alpha" },
                  { "id": "beta", "name": "Beta" }
                ]
                """),
            ["researches"] = Arr("""
                [
                  { "id": "res-a", "projectId": "alpha", "title": "alpha-research-marker" },
                  { "id": "res-b", "projectId": "beta", "title": "beta-research-marker" }
                ]
                """)
        });

    [Fact]
    public async Task AskAsync_PreservesChronologicalUnicodeHistory_AndIsolatesProjectsAndUnresolvedTargets()
    {
        var lead = MakeModel("astra", "GPT-6 Astra");
        var history = new JsonArray();
        // Store passes asks newest-first; RecentContext returns the latest 6 chronologically.
        for (var i = 44; i >= 0; i--)
        {
            history.Add(new JsonObject
            {
                ["projectId"] = "alpha",
                ["chatSessionId"] = "s1",
                ["question"] = $"Retained {i}: è 日本語 🚢",
                ["answer"] = $"Answer {i}."
            });
        }
        history.Add(new JsonObject { ["projectId"] = "beta", ["chatSessionId"] = "s1", ["question"] = "beta-private-marker", ["answer"] = "beta-private-answer" });
        history.Add(new JsonObject { ["projectId"] = null, ["chatSessionId"] = "other-chat", ["question"] = "legacy-unscoped-marker", ["answer"] = "legacy-unscoped-answer" });

        var adapter = new FakeLeadAdapter
        {
            Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Unicode è 日本語 reply."))
        };
        var services = new OrchestratorLeadQuestions.LeadServices { LeadModel = () => lead };

        var ok = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Please explain alpha status?", "alpha", "s1", "default", null, null,
                TwoProjectSnap(), null, history, [lead], () => true, StateFile: "C:/private/orchestrator.json"),
            adapter, services);

        Assert.True(ok.Ok);
        Assert.Equal("Unicode è 日本語 reply.", ok.Answer);
        var state = adapter.Invocations[0].State;
        Assert.Null(state["contextArchive"]);
        Assert.Equal(39, state["earlierTurns"]!.GetValue<int>());
        var sentHistory = state["history"]!.AsArray();
        Assert.Equal(6, sentHistory.Count);
        Assert.Equal("Retained 39: è 日本語 🚢", sentHistory[0]!["user"]!.GetValue<string>());
        Assert.Equal("Retained 44: è 日本語 🚢", sentHistory[5]!["user"]!.GetValue<string>());
        Assert.DoesNotContain("beta-private", state.ToJsonString());
        Assert.DoesNotContain("legacy-unscoped", state.ToJsonString());

        // Unresolved target (no projectId, no project name mentioned) keeps scoped beta/alpha history out when chatSessionId is set.
        adapter.Invocations.Clear();
        var unresolved = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Please describe overall status?", null, "s1", "default", null, null,
                TwoProjectSnap(), null, history, [lead], () => true),
            adapter, services);
        Assert.True(unresolved.Ok);
        Assert.Empty(adapter.Invocations[0].State["history"]!.AsArray());
        Assert.DoesNotContain("beta-private", adapter.Invocations[0].State.ToJsonString());

        // Wrong or ambiguous recipient fails before invoking lead.
        adapter.Invocations.Clear();
        var wrongProj = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Tell me about Beta", "alpha", "s1", "default", null, null,
                TwoProjectSnap(), null, history, [lead], () => true),
            adapter, services);
        Assert.False(wrongProj.Ok);
        Assert.Contains("different project", wrongProj.Reason);
        Assert.Empty(adapter.Invocations);
    }

    [Fact]
    public async Task AskAsync_GrillChatSession_IsScoped_ReportsMissingLead_AndForwardsGrillContextOnStart()
    {
        var lead = MakeModel("astra", "GPT-6 Astra");
        var snap = TwoProjectSnap();
        var adapter = new FakeLeadAdapter
        {
            Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Q1: Keep minimal?"))
        };

        // Missing lead reports connection requirement instead of dispatching.
        var noLead = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Grill me", "alpha", "g1", "default", "me", null, snap, null, new JsonArray(), [], () => true),
            null, new OrchestratorLeadQuestions.LeadServices());
        Assert.True(noLead.Ok);
        Assert.Contains("orchestrator model connection is required for Grill me", noLead.Answer);

        var services = new OrchestratorLeadQuestions.LeadServices { LeadModel = () => lead };
        var turn1 = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Grill me", "alpha", "g1", "default", "me", null, snap, null, new JsonArray(), [lead], () => true),
            adapter, services);
        Assert.True(turn1.Ok);
        Assert.Equal("me", adapter.Invocations[0].GrillKind);
        Assert.False(adapter.Invocations[0].AllowActions);
        Assert.Equal("me", turn1.Metadata!["grillKind"]!.GetValue<string>());

        var grillHistory = Arr("""[{"projectId":"alpha","chatSessionId":"g1","question":"Grill me","answer":"Q1: Keep minimal?","grillKind":"me"}]""");
        var turn2 = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Keep a minimal API without database.", "alpha", "g1", "default", null, null, snap, null, grillHistory, [lead], () => true),
            adapter, services);
        Assert.Equal("me", adapter.Invocations[1].GrillKind);

        // Different chat session does not inherit grillKind.
        await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "What is the current goal?", "alpha", "other", "default", null, null, snap, null, grillHistory, [lead], () => true),
            adapter, services);
        Assert.Null(adapter.Invocations[2].GrillKind);

        // Explicit Start emits start order with grillContext without re-invoking lead.
        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var startServices = new OrchestratorLeadQuestions.LeadServices { LeadModel = () => lead, OnEffect = effects.Add };
        var startResult = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Start", "alpha", "g1", "default", null, null, snap, null, grillHistory, [lead], () => true),
            adapter, startServices);
        Assert.True(startResult.Ok);
        Assert.Equal(3, adapter.Invocations.Count);
        var orderEffect = Assert.Single(effects, e => e.Kind == "order");
        Assert.Equal("start", orderEffect.Payload["action"]!.GetValue<string>());
        Assert.Contains("Q1: Keep minimal?", orderEffect.Payload["grillContext"]!.GetValue<string>());
    }

    [Fact]
    public async Task AskAsync_ProcessFailureRetriesOnceThenSwitches_AndUsageLimitHandsOverImmediately()
    {
        var codexLead = MakeModel("lead-codex", "Codex Lead", provider: "codex", cli: "gpt-6-astra");
        var grokLead = MakeModel("lead-grok", "Grok Lead", provider: "grok", cli: "grok-4.6");
        var roster = new[] { codexLead, grokLead };
        var snap = TwoProjectSnap(leadId: "lead-codex");
        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();

        var codexCalls = 0;
        var adapter = new FakeLeadAdapter
        {
            Handler = (inv, _) =>
            {
                var id = inv.Model.Data["id"]!.GetValue<string>();
                if (id == "lead-codex")
                {
                    codexCalls++;
                    return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse(
                        "", Failure: "process", ProcessExit: 1));
                }
                return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Grok took over."));
            }
        };
        var services = new OrchestratorLeadQuestions.LeadServices { LeadModel = () => codexLead, OnEffect = effects.Add };

        var retryResult = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "What happened?", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);

        Assert.True(retryResult.Ok);
        Assert.Equal("Grok took over.", retryResult.Answer);
        Assert.Equal(2, codexCalls);
        Assert.Equal("lead-grok", retryResult.InvokedLead!.Data["id"]!.GetValue<string>());
        Assert.Contains(effects, e => e.Kind == "lead_switch" && e.Payload["toId"]!.GetValue<string>() == "lead-grok");

        // Immediate handover on PROVIDER_USAGE_LIMIT (no same-lead retry).
        adapter.Invocations.Clear();
        effects.Clear();
        adapter.Handler = (inv, _) =>
        {
            var id = inv.Model.Data["id"]!.GetValue<string>();
            if (id == "lead-codex")
                return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse(
                    "Quota reached", Failure: "provider", Diagnostic: Obj("""{"code":"PROVIDER_USAGE_LIMIT","message":"5h limit"}""")));
            return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Grok handled quota handover."));
        };

        var quotaResult = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Continue explanation?", "alpha", "s2", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);

        Assert.Equal(new[] { "lead-codex", "lead-grok" },
            adapter.Invocations.Select(i => i.Model.Data["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("Grok handled quota handover.", quotaResult.Answer);
        Assert.Contains(effects, e => e.Kind == "lead_switch");
    }

    [Fact]
    public async Task AskAsync_GoldConstraint_ForwardsExactModel_RejectsLegacyPreparation_AndBlocksSubstitution()
    {
        var lead = MakeModel("astra", "GPT-6 Astra", provider: "codex", cli: "gpt-6-astra");
        var worker = MakeModel("luna", "GPT-6 Luna", provider: "codex", cli: "gpt-6-luna", kind: "local");
        var badWorker = MakeModel("off", "Disabled Worker", provider: "codex", cli: "gpt-6-luna", enabled: false);
        var roster = new[] { lead, worker, badWorker };
        var snap = TwoProjectSnap();
        var goldSpec = Obj("""{"id":"luna","provider":"codex","cli":"gpt-6-luna"}""");

        var preparedRequests = new List<OrchestratorLeadQuestions.TaskPreparationRequest>();
        var adapter = new FakeLeadAdapter
        {
            Handler = (_, _) => ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Gold task ready."))
        };

        var legacyOnly = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => lead,
            PrepareTask = (_, _, _, _) => ValueTask.FromResult(Obj("""{"ok":true,"task":{"id":"legacy","title":"Legacy"}}"""))
        };
        var rejectedLegacy = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Implement the parser cleanly.", "alpha", "s1", "gold", null, goldSpec, snap, null, new JsonArray(), roster, () => true),
            adapter, legacyOnly);
        Assert.False(rejectedLegacy.Ok);
        Assert.Equal("task preparation is unavailable", rejectedLegacy.Reason);

        var services = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => lead,
            PrepareTaskWithContext = (req, _) =>
            {
                preparedRequests.Add(req);
                return ValueTask.FromResult(Obj("""{"ok":true,"task":{"id":"t-gold","title":"Implement the parser"}}"""));
            }
        };
        var ok = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Implement the parser cleanly.", "alpha", "s1", "gold", null, goldSpec, snap, null, new JsonArray(), roster, () => true),
            adapter, services);

        Assert.True(ok.Ok);
        Assert.Equal("t-gold", ok.NewTaskId);
        Assert.Contains("New task prepared from this message: Implement the parser.", ok.Answer);
        var prep = Assert.Single(preparedRequests);
        Assert.Equal("luna", prep.GoldModel!["id"]!.GetValue<string>());
        Assert.Equal("codex", prep.GoldModel!["provider"]!.GetValue<string>());
        Assert.Equal("gpt-6-luna", prep.GoldModel!["cli"]!.GetValue<string>());

        var mismatchGold = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Implement the parser.", "alpha", "s1", "gold", null,
                Obj("""{"id":"luna","provider":"codex","cli":"wrong-cli"}"""), snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.False(mismatchGold.Ok);
        Assert.Contains("No substitute will be used", mismatchGold.Reason);

        var disabledGold = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Implement the parser.", "alpha", "s1", "gold", null,
                Obj("""{"id":"off"}"""), snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.False(disabledGold.Ok);
        Assert.Contains("No substitute will be used", disabledGold.Reason);

        var approveWithGold = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Approve", "alpha", "s1", "gold", null, goldSpec, snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.False(approveWithGold.Ok);
        Assert.Contains("Gold applies to new work requests or Start", approveWithGold.Reason);
    }

    [Fact]
    public async Task AskAsync_ResearchDispatch_RequiresExplicitIntent_RejectsSubstitution_AndProtectsDraft()
    {
        var lead = MakeModel("astra", "GPT-6 Astra", provider: "codex", cli: "gpt-6-astra");
        var luna = MakeModel("luna", "GPT-6 Luna", provider: "codex", cli: "gpt-6-luna", kind: "local");
        var grok = MakeModel("grok", "Grok", provider: "grok", cli: "grok-4.6", kind: "default");
        var roster = new[] { lead, luna, grok };
        var snap = TwoProjectSnap();
        var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var progressEvents = new List<OrchestratorLeadQuestions.LeadAskProgress>();

        var actionModelId = "luna";
        var actionProjectId = "alpha";
        var adapter = new FakeLeadAdapter
        {
            Handler = (inv, _) =>
            {
                var raw = $$"""{"answer":"Research assignment proposed.","actions":[{"type":"dispatch_research","projectId":"{{actionProjectId}}","modelId":"{{actionModelId}}","title":"Telemetry","detail":"Compare sources."}]}""";
                inv.OnProgress?.Invoke(new OrchestratorLeadQuestions.LeadAskProgress("Selecting worker.", raw, 15));
                return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse(raw));
            }
        };
        var services = new OrchestratorLeadQuestions.LeadServices
        {
            LeadModel = () => lead,
            OnEffect = effects.Add,
            OnProgress = progressEvents.Add
        };

        // 1. Unsolicited research action is rejected when user only asked a question.
        var unsolicited = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "What is the telemetry format?", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.True(unsolicited.Ok);
        Assert.False(unsolicited.ActionResults[0].Ok);
        Assert.Contains("did not authorize research work", unsolicited.Answer);
        Assert.DoesNotContain(effects, e => e.Kind == "dispatch_research");
        // Structured JSON draft must not flash in progress updates.
        Assert.All(progressEvents, p => Assert.Equal("", p.Answer));

        // 2. Wrong target project is rejected.
        effects.Clear();
        actionProjectId = "beta";
        var wrongTarget = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Please research telemetry with GPT-6 Luna.", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.False(wrongTarget.ActionResults[0].Ok);
        Assert.Contains("different project", wrongTarget.Answer);

        // 3. Lead substituting another model when user named Luna is rejected.
        actionProjectId = "alpha";
        actionModelId = "astra";
        var substituted = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Please research telemetry using GPT-6 Luna.", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.False(substituted.ActionResults[0].Ok);
        Assert.Contains("No model was substituted", substituted.Answer);

        // 4. Adapter without SourceProtectedResearch is rejected.
        actionModelId = "grok";
        var unsupportedAdapter = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Please research telemetry with a worker.", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.False(unsupportedAdapter.ActionResults[0].Ok);
        Assert.Contains("Source-protected research is not supported", unsupportedAdapter.Answer);

        // 5. Valid explicit research request proposes dispatch_research effect without launching workers.
        actionModelId = "luna";
        var valid = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Please research telemetry with GPT-6 Luna.", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), roster, () => true),
            adapter, services);
        Assert.True(valid.ActionResults[0].Ok);
        Assert.Single(effects, e => e.Kind == "dispatch_research" && e.ProjectId == "alpha");
    }

    [Fact]
    public async Task AskAsync_StaleTurnsSuppressProgressAndFinalState_AndSimultaneousProjectsIsolateCancellation()
    {
        var lead = MakeModel("astra", "GPT-6 Astra");
        var snap = TwoProjectSnap();

        // Stale turn suppresses progress and returns cancelled without ask_turn or briefing_baseline.
        var isCurrent = true;
        var staleEffects = new List<OrchestratorLeadQuestions.LeadEffect>();
        var staleProgress = new List<OrchestratorLeadQuestions.LeadAskProgress>();
        var staleAdapter = new FakeLeadAdapter
        {
            Handler = (inv, _) =>
            {
                inv.OnProgress?.Invoke(new OrchestratorLeadQuestions.LeadAskProgress("t1", "live", 10));
                isCurrent = false;
                inv.OnProgress?.Invoke(new OrchestratorLeadQuestions.LeadAskProgress("t2", "stale", 20));
                return ValueTask.FromResult(new OrchestratorLeadQuestions.LeadSpokenResponse("Stale reply."));
            }
        };
        var staleResult = await OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Explain alpha", "alpha", "s1", "default", null, null, snap, null, new JsonArray(), [lead], () => isCurrent),
            staleAdapter,
            new OrchestratorLeadQuestions.LeadServices { LeadModel = () => lead, OnEffect = staleEffects.Add, OnProgress = staleProgress.Add });

        Assert.Equal("cancelled", staleResult.Reason);
        Assert.True(staleResult.Metadata!["cancelled"]!.GetValue<bool>());
        Assert.Single(staleProgress);
        Assert.Equal("live", staleProgress[0].Answer);
        Assert.DoesNotContain(staleEffects, e => e.Kind is "ask_turn" or "briefing_baseline");
        Assert.Contains(staleEffects, e => e.Kind == "cancelled");

        // Simultaneous projects with deterministic barriers: cancelling alpha leaves beta running cleanly.
        var alphaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlpha = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBeta = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var parallelAdapter = new FakeLeadAdapter
        {
            Handler = async (inv, ct) =>
            {
                if (inv.ProjectId == "alpha")
                {
                    alphaEntered.TrySetResult();
                    using var _ = ct.Register(() => releaseAlpha.TrySetCanceled(ct));
                    var text = await releaseAlpha.Task;
                    return new OrchestratorLeadQuestions.LeadSpokenResponse(text);
                }
                betaEntered.TrySetResult();
                var betaText = await releaseBeta.Task;
                return new OrchestratorLeadQuestions.LeadSpokenResponse(betaText);
            }
        };
        var parallelServices = new OrchestratorLeadQuestions.LeadServices { LeadModel = () => lead };
        using var alphaCts = new CancellationTokenSource();
        using var betaCts = new CancellationTokenSource();

        var alphaTask = OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Explain alpha", "alpha", "s-a", "default", null, null, snap, null, new JsonArray(), [lead], () => !alphaCts.IsCancellationRequested),
            parallelAdapter, parallelServices, alphaCts.Token).AsTask();
        var betaTask = OrchestratorLeadQuestions.AskAsync(
            new OrchestratorLeadQuestions.LeadAskTurnRequest(
                "Explain beta", "beta", "s-b", "default", null, null, snap, null, new JsonArray(), [lead], () => !betaCts.IsCancellationRequested),
            parallelAdapter, parallelServices, betaCts.Token).AsTask();

        await Task.WhenAll(alphaEntered.Task, betaEntered.Task);
        alphaCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await alphaTask);
        Assert.False(betaTask.IsCompleted);

        releaseBeta.SetResult("Answer for beta.");
        var betaResult = await betaTask;
        Assert.True(betaResult.Ok);
        Assert.Equal("Answer for beta.", betaResult.Answer);
        Assert.Equal("beta", betaResult.Metadata!["projectId"]!.GetValue<string>());
    }

    // Composition-root cases deferred to Job 52 (Orchestrator.cs / server integration):
    // - HTTP routes (/api/orchestrator, /api/orchestrator/ask, /api/orchestrator/ask/cancel, /api/orchestrator/lead, /api/orchestrator/research/*).
    // - Durable orchestrator.json persistence (store.asks mutations, briefingBaselines, atomic persist/renameWithRetry, restart recovery).
    // - Per-project askTokens/askEpoch cancellation registry, abortAsk() orchestration and Stopped. history recording.
    // - Live worker process spawning, push-guard env injection, stopWorker PID control, approve/startAutoTask state transitions, and autonomyTick/sentinel loops.
}
