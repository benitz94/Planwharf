// Scenario parity tests replaying recorded JavaScript fixtures against C# ports.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ScenarioParityTests
{
    private static readonly string ScenariosFolder = FindScenariosFolder();
    private const long Epoch = 1700000000000L;

    public static TheoryData<string> ExecutionScenarios => GetScenarios("execution");
    public static TheoryData<string> ProjectHomeScenarios => GetScenarios("project-home");
    public static TheoryData<string> OrchestratorTeamModelsScenarios => GetScenarios("orchestrator-team-models");
    public static TheoryData<string> OrchestratorJobBuildingScenarios => GetScenarios("orchestrator-job-building");
    public static TheoryData<string> OrchestratorLeadQuestionsScenarios => GetScenarios("orchestrator-lead-questions");

    [Theory]
    [MemberData(nameof(OrchestratorLeadQuestionsScenarios))]
    public async Task Replay_OrchestratorLeadQuestions_Scenarios(string name)
    {
        var root = Path.Combine(FindScenariosFolder(), ".verify-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var scenario = GetScenario("orchestrator-lead-questions", name);
            var requests = scenario["requests"]!.AsArray();
            var replies = scenario["replies"]!.AsArray();
            var exchange = new Exchange(requests, replies, root);
            var observedEvents = new List<JsonNode>();
            var effects = new List<OrchestratorLeadQuestions.LeadEffect>();
            var current = true;
            var adapter = new ReplayLeadModelAdapter(exchange, observedEvents, () => current, () => current = false);
            var roster = LeadQuestionRoster();
            var services = new OrchestratorLeadQuestions.LeadServices
            {
                Now = () => "2023-11-14T22:13:20.000Z",
                NowMilliseconds = () => Epoch,
                OnEffect = effect => effects.Add(effect),
                OnProgress = progress => observedEvents.Add(new JsonObject
                {
                    ["type"] = "progress",
                    ["thinking"] = progress.Thinking,
                    ["answer"] = progress.Answer,
                    ["ms"] = progress.Ms
                }),
                PrepareTask = (projectId, objective, isCurrent, _) => ValueTask.FromResult(
                    isCurrent()
                        ? new JsonObject
                        {
                            ["ok"] = true,
                            ["task"] = new JsonObject
                            {
                                ["id"] = "task-fixed",
                                ["projectId"] = projectId,
                                ["objective"] = objective
                            }
                        }
                        : new JsonObject { ["ok"] = false, ["reason"] = "cancelled" })
            };

            if (name == "unreadable-autonomy-decision-strict-retry")
            {
                var decisionServices = services with { LeadModel = () => roster[0], Roster = roster };
                var decisionRequest = CreateLeadDecisionRequest(scenario, roster, () => current);
                var decisionResult = await OrchestratorLeadQuestions.AskLeadDecisionAsync(decisionRequest, adapter, decisionServices, CancellationToken.None);

                var projectedNotes = new JsonArray(effects
                    .Where(e => e.Kind == "note")
                    .Select(e => (JsonNode?)e.Payload["text"]!.DeepClone())
                    .Reverse()
                    .ToArray());
                var actualDecisionOutput = new JsonObject
                {
                    ["calls"] = adapter.CallCount,
                    ["assumptions"] = decisionResult.Settings?["assumptions"]?.DeepClone() ?? new JsonArray(),
                    ["stopReason"] = decisionResult.StopReason,
                    ["notes"] = projectedNotes
                };

                AssertOutcome(scenario, Normalize(actualDecisionOutput, root), null, GetEffects(root), exchange, observedEvents);
                Assert.True(decisionResult.Ok);
                Assert.NotNull(decisionResult.Decision);
                Assert.Equal("Run the isolated checks", decisionResult.Decision!["next"]!.GetValue<string>());
                Assert.Equal("A strict reply was accepted.", decisionResult.Decision["summary"]!.GetValue<string>());
                Assert.Null(decisionResult.StopReason);
                Assert.Equal(["astra", "astra"], adapter.ObservedModelIds);
                Assert.Contains(effects, e => e.Kind == "assumption");
                Assert.Contains(effects, e => e.Kind == "prepare_task"
                    && e.Payload["next"]?.GetValue<string>() == "Run the isolated checks"
                    && e.Payload["result"]?["ok"]?.GetValue<bool>() == true);
                Assert.DoesNotContain(effects, e => e.Kind is "stop_autonomy" or "refusal" or "credits_needed" or "dispatch_research");
                AssertNoPrivateBetaLeak(adapter.ObservedStates, effects, decisionResult.Decision);
                return;
            }

            var askRequest = CreateLeadAskRequest(scenario, roster, () => current);
            var askResult = await OrchestratorLeadQuestions.AskAsync(askRequest, adapter, services, CancellationToken.None);

            if (name == "mid-turn-cancellation-preserves-only-stop-record")
            {
                exchange.AssertAllConsumed();
                Assert.False(current);
                Assert.True(Same(scenario["effects"]!, GetEffects(root)), "Cancelled turn must not write files to disk.");
                var actualEvents = new JsonArray(observedEvents.Select(e => e.DeepClone()).ToArray());
                Assert.True(Same(scenario["events"]!, actualEvents),
                    $"Events mismatch for {name}:\nExpected: {scenario["events"]!.ToJsonString()}\nActual:   {actualEvents.ToJsonString()}");

                // The durable "Stopped." ask belongs to the Job 52 composition root. This module proves
                // cancellation via LeadAskTurnResult metadata, captured progress effect, and no durable turn effects.
                Assert.Equal(scenario["result"]!["ok"]!.GetValue<bool>(), askResult.Ok);
                Assert.Equal(scenario["result"]!["cancelled"]!.GetValue<bool>(), askResult.Metadata?["cancelled"]?.GetValue<bool>());
                Assert.Equal("cancelled", askResult.Reason);
                Assert.Equal("", askResult.Answer);
                Assert.Null(askResult.Thinking);
                Assert.Null(askResult.InvokedLead);
                Assert.Empty(askResult.ActionResults);

                var progressEffects = effects.Where(e => e.Kind == "progress").ToList();
                var expectedAsk = scenario["result"]!["asks"]![0]!.AsObject();
                Assert.Single(progressEffects);
                Assert.Equal(expectedAsk["question"]!.GetValue<string>(), progressEffects[0].Payload["question"]!.GetValue<string>());
                Assert.Equal(expectedAsk["thinking"]!.GetValue<string>(), progressEffects[0].Payload["thinking"]!.GetValue<string>());
                Assert.Equal("Unsafe partial draft", progressEffects[0].Payload["draft"]!.GetValue<string>());
                Assert.Equal(7L, progressEffects[0].Payload["ms"]!.GetValue<long>());
                Assert.Contains(effects, e => e.Kind == "thinking_clear");
                Assert.Contains(effects, e => e.Kind == "cancelled" && e.Payload["chatKey"]?.GetValue<string>() == "alpha");
                Assert.DoesNotContain(effects, e => e.Kind is "ask_turn" or "briefing_baseline" or "dispatch_research" or "prepare_task" or "reassign_crew" or "tool_request");

                var resultText = JsonSerializer.Serialize(new JsonObject
                {
                    ["answer"] = askResult.Answer,
                    ["thinking"] = askResult.Thinking,
                    ["reason"] = askResult.Reason,
                    ["metadata"] = askResult.Metadata?.DeepClone()
                });
                Assert.DoesNotContain("must not be persisted", resultText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Too late", resultText, StringComparison.Ordinal);
                Assert.DoesNotContain(effects, e => e.Payload.ToJsonString().Contains("must not be persisted", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(effects, e => e.Payload.ToJsonString().Contains("Stale draft", StringComparison.Ordinal));
                AssertNoPrivateBetaLeak(adapter.ObservedStates, effects, null);
                return;
            }

            var projectedActions = new JsonArray();
            foreach (var action in askResult.ActionResults)
            {
                var item = new JsonObject { ["ok"] = action.Ok };
                if (action.Text is not null) item["text"] = action.Text;
                if (action.Reason is not null) item["reason"] = action.Reason;
                if (action.Payload is not null) item["payload"] = action.Payload.DeepClone();
                projectedActions.Add(item);
            }
            var turnJson = new JsonObject
            {
                ["ok"] = askResult.Ok,
                ["question"] = askRequest.Question,
                ["answer"] = askResult.Answer,
                ["thinking"] = askResult.Thinking,
                ["ms"] = askResult.Ms,
                ["projectId"] = askResult.Metadata?["projectId"]?.DeepClone(),
                ["orchestratorId"] = askResult.InvokedLead?.Data["id"]?.DeepClone(),
                ["actionResults"] = projectedActions
            };
            JsonObject actualResult = name == "streamed-envelope-project-isolation-and-unsolicited-research"
                ? new JsonObject
                {
                    ["turn"] = turnJson,
                    ["recordedAnswerContainsPrivateBeta"] =
                        askResult.Answer.Contains("PRIVATE_BETA_GOAL", StringComparison.Ordinal) ||
                        adapter.ObservedStates.Any(s => s.Contains("PRIVATE_BETA_GOAL", StringComparison.Ordinal)),
                    ["persistedAskCount"] = effects.Count(e => e.Kind == "ask_turn")
                }
                : new JsonObject
                {
                    ["turn"] = turnJson,
                    ["calls"] = adapter.CallCount
                };

            AssertOutcome(scenario, Normalize(actualResult, root), null, GetEffects(root), exchange, observedEvents);
            Assert.Equal(requests.Select(r => r!["modelId"]!.GetValue<string>()), adapter.ObservedModelIds);
            AssertNoPrivateBetaLeak(adapter.ObservedStates, effects, askResult.Metadata);

            if (name == "streamed-envelope-project-isolation-and-unsolicited-research")
            {
                Assert.Contains(effects, e => e.Kind == "ask_turn");
                Assert.Contains(effects, e => e.Kind == "briefing_baseline");
                Assert.DoesNotContain(effects, e => e.Kind is "dispatch_research" or "prepare_task" or "lead_switch");
            }
            else if (name == "process-failure-retry-then-alternate-provider")
            {
                Assert.Equal(["astra", "astra", "opus"], adapter.ObservedModelIds);
                Assert.Contains(effects, e => e.Kind == "note" && e.Payload["text"]?.GetValue<string>() == "Astra failed to answer (process). Retrying once.");
                Assert.Contains(effects, e => e.Kind == "lead_switch"
                    && e.Payload["fromId"]?.GetValue<string>() == "astra"
                    && e.Payload["toId"]?.GetValue<string>() == "opus");
                Assert.DoesNotContain(effects, e => e.Kind is "dispatch_research" or "credits_needed");
            }
            else if (name == "immediate-handover-provider-usage-limit")
            {
                Assert.Equal(["astra", "opus"], adapter.ObservedModelIds);
                Assert.Contains(effects, e => e.Kind == "lead_switch"
                    && e.Payload["fromId"]?.GetValue<string>() == "astra"
                    && e.Payload["toId"]?.GetValue<string>() == "opus"
                    && e.Payload["why"]?.GetValue<string>() == "Astra is out of quota.");
                Assert.DoesNotContain(effects, e => e.Kind is "dispatch_research" or "credits_needed");
            }
            else if (name == "immediate-handover-credits-needed")
            {
                Assert.Equal(["astra", "opus"], adapter.ObservedModelIds);
                Assert.Contains(effects, e => e.Kind == "credits_needed" && e.Payload["modelId"]?.GetValue<string>() == "astra");
                Assert.Contains(effects, e => e.Kind == "lead_switch"
                    && e.Payload["fromId"]?.GetValue<string>() == "astra"
                    && e.Payload["toId"]?.GetValue<string>() == "opus"
                    && e.Payload["why"]?.GetValue<string>() == "Astra needs usage credits.");
                Assert.DoesNotContain(effects, e => e.Kind == "dispatch_research");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(ProjectHomeScenarios))]
    public async Task Replay_ProjectHome_Scenarios(string name)
    {
        var root = Path.Combine(FindScenariosFolder(), ".verify-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var sc = GetScenario("project-home", name);
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            var folder = Path.Combine(root, "project é");
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
            File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(folder, ".git", "config"), "[core]\nrepositoryformatversion = 0\n");
            Task<string?> Git(string linked, IReadOnlyList<string> args)
            {
                var commandArgs = new JsonArray("--no-optional-locks", "-C", linked);
                foreach (var arg in args) commandArgs.Add(arg);
                var reply = exchange.Call(new JsonObject { ["kind"] = "git", ["command"] = "git", ["args"] = commandArgs,
                    ["cwd"] = null, ["timeout"] = 5000, ["maxBuffer"] = 8388608 });
                var stdout = reply["stdout"]?.ToString().Replace("<ROOT>", root);
                return Task.FromResult(reply["error"] is null ? args.Contains("-z") ? stdout : stdout?.Trim() : null);
            }
            var options = new ProjectHomeOptions { PlanFile = Path.Combine(folder, ".planwharf", "plan.md"), Git = Git,
                Now = () => "2023-11-14T22:13:20.000Z" };
            JsonObject result;
            if (name == "local-git-success-and-failure")
            {
                var success = await ProjectHome.LocalGitAsync(folder, options);
                var failure = await ProjectHome.LocalGitAsync(folder, new ProjectHomeOptions { Git = Git });
                result = new JsonObject { ["success"] = success, ["failure"] = failure };
            }
            else if (name == "durable-megatask-outranks-row")
            {
                var project = EObj("""{"project":{"id":"project"},"revisions":[{"id":3,"title":"Fixture plan","direction":"Ship fixture","state":"Recorded progress","open":["Need review"],"closed":[]}],"home":{"notes":[{"id":"N-1","text":"Keep exact identity."}]}}""");
                project["project"]!["folder"] = folder;
                var snapshot = EObj("""{"projects":[{"id":"project","megaTaskId":"MT-ready","status":"watching","canResume":true,"jobTitle":"Stale row title","dispatch":[]}],"researches":[],"execution":{"megaTasks":[{"id":"MT-running","projectId":"other","status":"RUNNING"},{"id":"MT-ready","projectId":"project","title":"Durable proposal","status":"READY_FOR_APPROVAL","blockers":[]}],"subTasks":[{"id":"ST-ready","megaTaskId":"MT-ready","title":"Build fixture","status":"PENDING","assignedModelId":"worker","provider":"fixture","cli":"fixture-cli","attemptIds":[]}],"results":[],"checkpoints":[],"timeline":[{"id":"EV-fixed","projectId":"project","type":"MEGATASK_PROPOSED","summary":"Durable proposal prepared."}]}}""");
                var home = await ProjectHome.GetProjectHomeAsync(project, snapshot, null, options);
                project["project"] = EObj("""{"id":"missing","folder":null}""");
                var relink = await ProjectHome.GetProjectHomeAsync(project, EObj("""{"projects":[{"id":"missing","status":"checkpointed","canResume":true}],"researches":[],"execution":{}}"""), null, new ProjectHomeOptions { Git = Git });
                result = new JsonObject { ["home"] = home, ["relink"] = new JsonObject { ["next"] = relink["next"]?.DeepClone(), ["action"] = relink["action"]?.DeepClone(), ["local"] = relink["local"]?.DeepClone() } };
            }
            else throw new InvalidOperationException("Missing project-home replay: " + name);
            AssertJsonAt(sc["result"], Normalize(result, root), "result");
            AssertOutcome(sc, Normalize(result, root), null, GetEffects(root), exchange);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [MemberData(nameof(OrchestratorTeamModelsScenarios))]
    public void Replay_OrchestratorTeamModels_Scenarios(string name)
    {
        var root = Path.Combine(FindScenariosFolder(), ".verify-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var sc = GetScenario("orchestrator-team-models", name);
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? result = null;
            string? error = null;
            try { result = RunOrchestratorTeamModelsScenario(name, root, exchange); }
            catch (InvalidOperationException ex) { error = ex.Message; }
            AssertOutcome(sc, Normalize(result, root), error, GetEffects(root), exchange);
        }
        finally { Directory.Delete(root, true); }
    }

    private static JsonObject RunOrchestratorTeamModelsScenario(string name, string root, Exchange exchange)
    {
        if (name == "project-team-role-isolation-and-account-scope")
        {
            var providers = JsonNode.Parse("""
                [
                  {"id":"codex-main","adapter":"codex","quotaPoolId":"chatgpt","enabled":true,"account":{"key":"acct-alpha","type":"normal"}},
                  {"id":"claude-main","adapter":"claude","quotaPoolId":"claude","enabled":true,"account":{"key":"acct-beta","type":"business"}}
                ]
                """)!.AsArray();
            var models = JsonNode.Parse("""
                [
                  {"id":"astra","label":"Astra","provider":"codex-main","providerId":"codex-main","adapter":"codex","cli":"gpt-6-astra","plan":"chatgpt","quotaPoolId":"chatgpt","kind":"premium","enabled":true,"available":true,"limit":200,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}},
                  {"id":"opus","label":"Opus","provider":"claude-main","providerId":"claude-main","adapter":"claude","cli":"claude-opus-5-5","plan":"claude","quotaPoolId":"claude","kind":"premium","enabled":true,"available":true,"limit":200,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}}
                ]
                """)!.AsArray();
            var store = new JsonObject();
            TeamRosterContext Ctx(string? projectId) => new()
            {
                Models = models,
                Store = store,
                ProviderDefinitions = providers,
                PlansLive = new JsonArray(),
                CurrentProjectId = projectId is null ? null : () => projectId,
                Now = () => "2023-11-14T22:13:20.000Z"
            };
            Assert.True(OrchestratorTeamModels.SetModelRoles(Ctx(null), "astra", EObj("""{"worker":true,"orchestrator":true}"""))["ok"]!.GetValue<bool>());
            Assert.True(OrchestratorTeamModels.SetModelRoles(Ctx(null), "opus", EObj("""{"worker":true,"orchestrator":true}"""))["ok"]!.GetValue<bool>());
            Assert.True(OrchestratorTeamModels.SetOrchestrator(Ctx(null), "astra")["ok"]!.GetValue<bool>());
            Assert.True(OrchestratorTeamModels.SetModelRoles(Ctx(null), "opus", EObj("""{"worker":false,"verifier":true}"""), "alpha")["ok"]!.GetValue<bool>());
            Assert.True(OrchestratorTeamModels.SetOrchestrator(Ctx(null), "opus", "alpha")["ok"]!.GetValue<bool>());
            Assert.True(OrchestratorTeamModels.SetModelRoles(Ctx(null), "opus", EObj("""{"worker":true,"verifier":false,"orchestrator":false}"""), "beta")["ok"]!.GetValue<bool>());
            Assert.True(OrchestratorTeamModels.SetModelRoles(Ctx(null), "astra", EObj("""{"worker":false,"reviewer":true}"""), "beta")["ok"]!.GetValue<bool>());

            JsonObject ViewFor(string? projectId)
            {
                var c = Ctx(projectId);
                var roster = OrchestratorTeamModels.BuildRoster(c);
                var lead = OrchestratorTeamModels.SelectLead(c);
                var selectedId = projectId is not null && store["projectTeams"]?[projectId] is JsonObject team
                    ? team["orchestratorId"]?.DeepClone()
                    : store["orchestratorId"]?.DeepClone();
                return new JsonObject
                {
                    ["selectedOrchestratorId"] = selectedId,
                    ["orchestratorId"] = lead?["id"]?.DeepClone(),
                    ["models"] = roster
                };
            }

            var alpha = ViewFor("alpha");
            var beta = ViewFor("beta");
            var shared = ViewFor(null);
            providers[0]!.AsObject()["account"] = EObj("""{"key":"acct-second","type":"normal"}""");
            var switchedAccount = OrchestratorTeamModels.BuildRoster(Ctx(null));
            return new JsonObject
            {
                ["alpha"] = alpha,
                ["beta"] = beta,
                ["shared"] = shared,
                ["switchedAccount"] = switchedAccount
            };
        }

        if (name == "lead-automatic-handover-on-quota-depletion")
        {
            var providers = JsonNode.Parse("""
                [
                  {"id":"claude","adapter":"claude","quotaPoolId":"claude","enabled":true},
                  {"id":"codex","adapter":"codex","quotaPoolId":"chatgpt","enabled":true}
                ]
                """)!.AsArray();
            var models = JsonNode.Parse("""
                [
                  {"id":"opus","label":"Opus 5.5","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-opus-5-5","plan":"claude","quotaPoolId":"claude","kind":"premium","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}},
                  {"id":"astra","label":"Astra","provider":"codex","providerId":"codex","adapter":"codex","cli":"gpt-6-astra","plan":"chatgpt","quotaPoolId":"chatgpt","kind":"premium","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}},
                  {"id":"sonnet","label":"Sonnet 5.5","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-sonnet-5-5","plan":"claude","quotaPoolId":"claude","kind":"default","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}}
                ]
                """)!.AsArray();
            var store = EObj("""{"projects":{"alpha":{}}}""");
            TeamRosterContext Ctx(JsonArray plans) => new()
            {
                Models = models,
                Store = store,
                ProviderDefinitions = providers,
                PlansLive = plans,
                Now = () => "2023-11-14T22:13:20.000Z"
            };
            Assert.True(OrchestratorTeamModels.SetOrchestrator(Ctx(new JsonArray()), "opus")["ok"]!.GetValue<bool>());
            var plans1 = exchange.Call(new JsonObject { ["kind"] = "fetchPlans", ["call"] = 1 }).AsArray();
            var beforeRoster = OrchestratorTeamModels.BuildRoster(Ctx(plans1));
            var beforeLead = OrchestratorTeamModels.SelectLead(Ctx(plans1));
            var beforeSelectedId = store["orchestratorId"]?.DeepClone();
            var plans2 = exchange.Call(new JsonObject { ["kind"] = "fetchPlans", ["call"] = 2 }).AsArray();
            var afterLead = OrchestratorTeamModels.SelectLead(Ctx(plans2));
            var afterRoster = OrchestratorTeamModels.BuildRoster(Ctx(plans2));
            var astra = afterRoster.OfType<JsonObject>().First(m => m["id"]!.GetValue<string>() == "astra");
            var opusBefore = beforeRoster.OfType<JsonObject>().First(m => m["id"]!.GetValue<string>() == "opus");
            return new JsonObject
            {
                ["before"] = new JsonObject
                {
                    ["orchestratorId"] = beforeLead?["id"]?.DeepClone(),
                    ["selectedOrchestratorId"] = beforeSelectedId,
                    ["opusLowQuota"] = opusBefore["lowQuota"]?.DeepClone()
                },
                ["after"] = new JsonObject
                {
                    ["orchestratorId"] = afterLead?["id"]?.DeepClone(),
                    ["selectedOrchestratorId"] = store["orchestratorId"]?.DeepClone(),
                    ["orchestratorLabel"] = afterLead?["label"]?.DeepClone(),
                    ["astraRoles"] = astra["roles"]?.DeepClone(),
                    ["log"] = store["projects"]!["alpha"]!["log"]?.DeepClone()
                }
            };
        }

        if (name == "twenty-four-hour-credit-lockout")
        {
            var providers = JsonNode.Parse("""
                [
                  {"id":"claude","adapter":"claude","quotaPoolId":"claude","enabled":true},
                  {"id":"codex","adapter":"codex","quotaPoolId":"chatgpt","enabled":true}
                ]
                """)!.AsArray();
            var models = JsonNode.Parse("""
                [
                  {"id":"opus","label":"Opus 5.5","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-opus-5-5","plan":"claude","quotaPoolId":"claude","kind":"premium","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}},
                  {"id":"astra","label":"Astra","provider":"codex","providerId":"codex","adapter":"codex","cli":"gpt-6-astra","plan":"chatgpt","quotaPoolId":"chatgpt","kind":"premium","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}}
                ]
                """)!.AsArray();
            var orchPath = Path.Combine(root, "orchestrator.json");
            File.WriteAllText(orchPath, "{\n  \"orchestratorId\": \"astra\",\n  \"creditsNeeded\": {\n    \"opus\": {\n      \"at\": \"2023-11-14T22:13:20.000Z\"\n    }\n  }\n}\n");
            var store = JsonNode.Parse(File.ReadAllText(orchPath))!.AsObject();
            var timestamps = new[]
            {
                "2023-11-14T22:13:20.000Z",
                "2023-11-15T10:13:20.000Z",
                "2023-11-15T22:13:19.000Z",
                "2023-11-15T22:13:20.000Z"
            };
            var evaluations = new JsonArray();
            var job = EObj("""{"job":{"title":"Build feature","detail":"Implement"}}""");
            foreach (var at in timestamps)
            {
                var ctx = new TeamRosterContext
                {
                    Models = models,
                    Store = store,
                    ProviderDefinitions = providers,
                    PlansLive = new JsonArray(),
                    Now = () => at
                };
                var roster = OrchestratorTeamModels.BuildRoster(ctx);
                var picked = OrchestratorTeamModels.PickWorker(roster, job);
                var opus = roster.OfType<JsonObject>().First(m => m["id"]!.GetValue<string>() == "opus");
                evaluations.Add((JsonNode)new JsonObject
                {
                    ["at"] = at,
                    ["opus"] = opus.DeepClone(),
                    ["pickedId"] = picked?["id"]?.DeepClone()
                });
            }
            return new JsonObject { ["evaluations"] = evaluations };
        }

        if (name == "saving-mode-candidate-filtering")
        {
            var providers = JsonNode.Parse("""
                [{"id":"claude","adapter":"claude","quotaPoolId":"claude","enabled":true}]
                """)!.AsArray();
            var models = JsonNode.Parse("""
                [
                  {"id":"opus","label":"Opus 5.5","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-opus-5-5","plan":"claude","quotaPoolId":"claude","kind":"premium","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}},
                  {"id":"sonnet","label":"Sonnet 5.5","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-sonnet-5-5","plan":"claude","quotaPoolId":"claude","kind":"default","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}},
                  {"id":"haiku","label":"Haiku 5.5","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-haiku-5-5","plan":"claude","quotaPoolId":"claude","kind":"local","enabled":true,"available":true,"limit":100,"capabilities":{"canUseTools":true,"canInspectFilesystem":true,"canCode":true}}
                ]
                """)!.AsArray();
            var store = new JsonObject();
            TeamRosterContext Ctx(string? projectId) => new()
            {
                Models = models,
                Store = store,
                ProviderDefinitions = providers,
                PlansLive = new JsonArray(),
                CurrentProjectId = projectId is null ? null : () => projectId,
                Now = () => "2023-11-14T22:13:20.000Z"
            };
            var job = EObj("""{"title":"Build feature","detail":"Implement saving mode filtering"}""");
            JsonObject Evaluate(string mode)
            {
                var roster = OrchestratorTeamModels.BuildRoster(Ctx("alpha"));
                var allowed = new JsonObject();
                foreach (var m in roster.OfType<JsonObject>())
                    allowed[m["id"]!.GetValue<string>()] = OrchestratorTeamModels.AllowedBySavingMode(mode, m);
                var options = new JsonObject { ["job"] = job.DeepClone(), ["savingMode"] = mode };
                var picked = OrchestratorTeamModels.PickWorker(roster, options);
                var crew = OrchestratorTeamModels.PickWorkers(roster, options);
                var crewIds = new JsonArray(crew.OfType<JsonObject>().Select(w => (JsonNode?)w["id"]?.DeepClone()).ToArray());
                return new JsonObject
                {
                    ["allowed"] = allowed,
                    ["picked"] = picked?.DeepClone(),
                    ["crewIds"] = crewIds
                };
            }

            store["savingModes"] = new JsonObject { ["alpha"] = "normal" };
            var normal = Evaluate("normal");
            store["savingModes"]!["alpha"] = "safe";
            var safe = Evaluate("safe");
            store["savingModes"]!["alpha"] = "ultra-safe";
            var ultraSafe = Evaluate("ultra-safe");
            store["savingModes"]!["alpha"] = "safe";
            store["modelWeights"] = new JsonObject { ["sonnet"] = "premium" };
            var teamWeightOverride = Evaluate("safe");
            var modelsAfterOverride = OrchestratorTeamModels.BuildRoster(Ctx(null));
            return new JsonObject
            {
                ["normal"] = normal,
                ["safe"] = safe,
                ["ultraSafe"] = ultraSafe,
                ["teamWeightOverride"] = teamWeightOverride,
                ["modelsAfterOverride"] = modelsAfterOverride
            };
        }

        throw new InvalidOperationException("Missing orchestrator-team-models replay: " + name);
    }

    [Theory]
    [MemberData(nameof(OrchestratorJobBuildingScenarios))]
    public async Task Replay_OrchestratorJobBuilding_Scenarios(string name)
    {
        var root = Path.Combine(FindScenariosFolder(), ".verify-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var sc = GetScenario("orchestrator-job-building", name);
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? result = null;
            string? error = null;
            try { result = await RunOrchestratorJobBuildingScenario(name, root, exchange); }
            catch (InvalidOperationException ex) { error = ex.Message; }
            AssertOutcome(sc, Normalize(result, root), error, GetEffects(root), exchange);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class MockLeadModelAdapter(Exchange exchange, ModelCandidate? leadModel = null) : ILeadModelAdapter
    {
        public ModelCandidate? LeadModel { get; set; } = leadModel;
        public List<string> Invocations { get; } = [];

        public Task<string?> AskAsync(string projectId, ModelCandidate lead, string question, string? folder, CancellationToken cancellationToken)
        {
            Invocations.Add($"{projectId}:{lead.Id}:{folder}");
            var reply = exchange.Call(new JsonObject
            {
                ["kind"] = "askModel",
                ["modelId"] = lead.Id,
                ["question"] = question
            });
            return Task.FromResult(reply?["answer"]?.GetValue<string>());
        }
    }

    private sealed class MockExecutionBridge : IExecutionBridge
    {
        public List<SubTaskProposal> Proposals { get; set; } = [];
        public List<JsonObject> Events { get; } = [];
        public List<string> MethodCalls { get; } = [];
        public string CurrentStatus { get; set; } = "needs_approval";

        public IReadOnlyList<SubTaskProposal> SubTasks(string projectId, string megaTaskId)
        {
            MethodCalls.Add($"SubTasks:{projectId}:{megaTaskId}");
            return Proposals;
        }

        public void Replan(string projectId, string megaTaskId, IReadOnlyList<SubTaskProposal> parts, string? why)
        {
            MethodCalls.Add($"Replan:{projectId}:{megaTaskId}:{why}");
            Proposals = parts.ToList();
        }

        public void Reassign(string projectId, string megaTaskId, IReadOnlyList<string> assignments, string? why)
        {
            MethodCalls.Add($"Reassign:{projectId}:{megaTaskId}:{why}");
            var updated = new List<SubTaskProposal>();
            for (var i = 0; i < Proposals.Count; i++)
            {
                var assigned = i < assignments.Count ? assignments[i] : (Proposals[i].AssignedModelId ?? Proposals[i].ModelId);
                var orig = Proposals[i];
                updated.Add(new SubTaskProposal(assigned, orig.Slice, orig.Detail, orig.DependsOn, orig.Size, orig.LeadLine, orig.Scope)
                {
                    Type = orig.Type,
                    Title = orig.Title,
                    AssignedModelId = assigned,
                    Label = orig.Label
                });
            }
            Proposals = updated;
        }

        public string Advance(ProjectItem item, JobDescriptor job, IReadOnlyList<DispatchPart> parts)
        {
            MethodCalls.Add($"Advance:{item.Project.Id}:{job.Id}:{parts.Count}");
            return CurrentStatus;
        }

        public string? FindStatus(string projectId, string jobId)
        {
            MethodCalls.Add($"FindStatus:{projectId}:{jobId}");
            return null;
        }

        public void Event(string projectId, string type, string summary)
        {
            MethodCalls.Add($"Event:{projectId}:{type}:{summary}");
            Events.Add(new JsonObject { ["projectId"] = projectId, ["type"] = type, ["summary"] = summary });
        }
    }

    private sealed class MockGitOperations(Exchange exchange, string root) : IGitOperations
    {
        public List<string> MethodCalls { get; } = [];

        public PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000)
        {
            var commandArgs = new JsonArray();
            foreach (var a in args) commandArgs.Add(a);
            MethodCalls.Add($"Run:{string.Join(' ', args)}");
            var reply = exchange.Call(new JsonObject
            {
                ["kind"] = "git",
                ["command"] = "git",
                ["args"] = commandArgs
            });
            var status = reply?["status"]?.GetValue<int>() ?? 0;
            if (args.Contains("--is-inside-work-tree"))
            {
                return new PlanGitResult(false, Stdout: "", Stderr: "");
            }
            return new PlanGitResult(status == 0, Stdout: status == 0 ? "true\n" : "", Stderr: "");
        }

        public PlanGitResult RunGh(string folder, IReadOnlyList<string> args, int timeoutMs = 20000) => new(false);

        public void SweepOrphans(string projectId)
        {
            MethodCalls.Add($"SweepOrphans:{projectId}");
            Worktrees.SweepOrphans(root, []);
        }
    }

    private sealed class LocalGitOperations : IGitOperations
    {
        public PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000)
        {
            if (args.Contains("--is-inside-work-tree")) return new(false, Stdout: "", Stderr: "");
            return new(true, Stdout: "", Stderr: "");
        }
        public PlanGitResult RunGh(string folder, IReadOnlyList<string> args, int timeoutMs = 20000) => new(false);
        public void SweepOrphans(string projectId) {}
    }

    private sealed class MockFolderInspector(Func<string, InspectionFacts>? inspector = null) : IFolderInspector
    {
        public List<string> InspectedFolders { get; } = [];

        public InspectionFacts Inspect(string folder)
        {
            InspectedFolders.Add(folder);
            if (inspector != null) return inspector(folder);
            var names = Directory.Exists(folder)
                ? Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().Order().ToList()
                : [];
            return new InspectionFacts(
                Git: false,
                LastCommit: "",
                LastSubject: "",
                Recent: [],
                Dirty: [],
                Head: "",
                Branch: "",
                FileCount: names.Count,
                Folder: folder,
                Error: null
            );
        }
    }

    private sealed class MockCrewSelector(IReadOnlyList<ModelCandidate>? crew = null) : ICrewSelector
    {
        public IReadOnlyList<ModelCandidate> PickWorkers(JobDescriptor job, InspectionFacts? facts) =>
            crew ?? [
                new ModelCandidate("astra", Label: "Astra", Provider: "codex", Why: "inspect job · Astra on chatgpt; user catalog order"),
                new ModelCandidate("opus", Label: "Opus 5.5", Provider: "claude", Why: "build job · Opus 5.5 on claude; user catalog order")
            ];
    }

    private sealed class MockProcessLiveness(bool isAlive = false) : IProcessLiveness
    {
        public bool IsAlive(int pid) => isAlive;
    }

    private sealed class MockProjectReader(ProjectItem item) : IProjectReader
    {
        public Task<ProjectItem?> ReadAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectItem?>(item);
    }

    private sealed class MockWorkPlanManager(IGitOperations? git = null, IExecutionBridge? bridge = null) : IWorkPlanManager
    {
        public WorkPlanResult Open(string projectId, ProjectItem item, IReadOnlyList<PlanStepEntry>? steps) =>
            OrchestratorJobBuilding.OpenWorkPlan(projectId, item, steps, git, bridge);
    }

    private sealed class MockGrillComposer(IReadOnlyList<string>? questions = null) : IGrillComposer
    {
        public Task<IReadOnlyList<string>?> ComposeAsync(ProjectItem item, InspectionFacts facts, CancellationToken cancellationToken) =>
            Task.FromResult(questions);
    }

    private sealed class MockAutonomyOperations(IReadOnlyList<ModelCandidate> roster) : IAutonomyOperations
    {
        public IReadOnlyList<ModelCandidate> Roster(string projectId) => roster;

        public string SplitPrompt(ProjectItem item, IReadOnlyList<ModelCandidate> builders, IReadOnlyList<ModelCandidate> verifiers)
        {
            var files = new JsonArray();
            var folder = item.Project.Folder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().Order())
                {
                    if (!file.StartsWith('.')) files.Add(file);
                }
            }
            static string TierOf(string id) => id == "opus" ? "premium" : id == "haiku" ? "light" : "default";
            var bArray = new JsonArray();
            foreach (var b in builders)
            {
                var tier = TierOf(b.Id);
                bArray.Add(new JsonObject { ["id"] = b.Id, ["label"] = b.Label ?? b.Id, ["provider"] = b.Provider, ["tier"] = tier });
            }
            var vArray = new JsonArray();
            foreach (var v in verifiers)
            {
                var tier = TierOf(v.Id);
                vArray.Add(new JsonObject { ["id"] = v.Id, ["label"] = v.Label ?? v.Id, ["provider"] = v.Provider, ["tier"] = tier });
            }
            var goal = item.Revisions?.LastOrDefault()?.Direction;
            return Autonomy.SplitPrompt(new JsonObject
            {
                ["name"] = item.Project.Name,
                ["goal"] = goal,
                ["builders"] = bArray,
                ["verifiers"] = vArray,
                ["files"] = files
            });
        }

        public IReadOnlyList<SubTaskProposal>? ParseSplit(string? response, IReadOnlyList<ModelCandidate> builders, IReadOnlyList<ModelCandidate> verifiers)
        {
            static string TierOf(string id) => id == "opus" ? "premium" : id == "haiku" ? "light" : "default";
            var builderList = new JsonArray();
            foreach (var b in builders)
            {
                var tier = TierOf(b.Id);
                builderList.Add(new JsonObject { ["id"] = b.Id, ["label"] = b.Label ?? b.Id, ["provider"] = b.Provider, ["tier"] = tier });
            }
            var verifierIds = new JsonArray();
            foreach (var v in verifiers) verifierIds.Add(v.Id);

            var options = new JsonObject
            {
                ["builders"] = new JsonArray(builders.Select(b => (JsonNode)b.Id).ToArray()),
                ["verifiers"] = verifierIds
            };
            var parsed = Autonomy.ParseSplit(response, options);
            if (parsed?["parts"] is not JsonArray rawParts) return null;

            var fitted = Autonomy.FitWeights(rawParts, builderList);
            var weighedParts = fitted["parts"] as JsonArray ?? rawParts;
            var spread = Autonomy.SpreadParts(weighedParts, builderList);
            var finalParts = spread["parts"] as JsonArray ?? weighedParts;

            var result = new List<SubTaskProposal>();
            for (var i = 0; i < finalParts.Count; i++)
            {
                if (finalParts[i] is JsonObject p)
                {
                    var title = p["title"]?.GetValue<string>() ?? "";
                    var detail = p["detail"]?.GetValue<string>() ?? "";
                    var model = p["model"]?.GetValue<string>() ?? "";
                    var size = p["size"]?.GetValue<string>();
                    var deps = (p["dependsOn"] as JsonArray)?.Select(d => d!.GetValue<int>()).ToList() ?? [];
                    var files = (p["files"] as JsonArray)?.Select(f => f!.GetValue<string>()).ToList() ?? [];
                    result.Add(new SubTaskProposal(model, title, detail, deps, Size: size, Scope: files)
                    {
                        Type = "IMPLEMENTATION",
                        Title = title,
                        AssignedModelId = model
                    });
                }
            }
            var verifyModel = parsed["verify"]?.GetValue<string>() ?? verifiers.FirstOrDefault()?.Id ?? "astra";
            var verifySlice = OrchestratorJobBuilding.VerifySliceTitle + ": read the requirements and run the tests, then go straight to what the tests do not cover (edge cases, error, recovery and concurrency paths), add tests for it and fix what fails. Do not rewrite code that works. If you find something you must not decide or fix yourself, write one line: \"Needs your decision: <plain words a non-programmer understands> | suggestion: <what you recommend> | breaks: yes\" only if the wrong choice could lose work or break the app, otherwise \"breaks: no\".";
            result.Add(new SubTaskProposal(verifyModel, verifySlice, verifySlice, Enumerable.Range(0, result.Count).ToArray())
            {
                Type = "IMPLEMENTATION",
                Title = verifySlice,
                AssignedModelId = verifyModel
            });
            return result;
        }

        public string AssignmentPrompt(ProjectItem item, IReadOnlyList<SubTaskProposal> subs, IReadOnlyList<ModelCandidate> roster)
        {
            var slices = new JsonArray();
            var candidates = new JsonArray();
            foreach (var sub in subs)
            {
                slices.Add(sub.Title ?? sub.Slice);
                var allowed = new JsonArray();
                foreach (var m in roster)
                {
                    allowed.Add(new JsonObject
                    {
                        ["id"] = m.Id,
                        ["label"] = m.Label ?? m.Id,
                        ["provider"] = m.Provider
                    });
                }
                candidates.Add(allowed);
            }
            var goal = item.Revisions?.LastOrDefault()?.Direction;
            return Autonomy.AssignmentPrompt(new JsonObject
            {
                ["name"] = item.Project.Name,
                ["goal"] = goal,
                ["slices"] = slices,
                ["candidates"] = candidates
            });
        }

        public IReadOnlyList<string>? ParseAssignment(string? response, IReadOnlyList<SubTaskProposal> subs, IReadOnlyList<ModelCandidate> roster)
        {
            var allowed = new JsonArray(roster.Select(m => (JsonNode)m.Id).ToArray());
            var parsed = Autonomy.ParseAssignment(response, subs.Count, allowed);
            if (parsed?["assign"] is JsonArray arr)
            {
                return arr.Select(x => x!.GetValue<string>()).ToList();
            }
            return null;
        }
    }

    private static async Task<JsonObject> RunOrchestratorJobBuildingScenario(string name, string root, Exchange exchange)
    {
        var astra = new ModelCandidate("astra", Label: "Astra", Provider: "codex", ProviderEnabled: true, Available: true, Enabled: true, CanCode: true, CanVerify: true);
        var opus = new ModelCandidate("opus", Label: "Opus 5.5", Provider: "claude", ProviderEnabled: true, Available: true, Enabled: true, CanCode: true, CanVerify: true);
        var haiku = new ModelCandidate("haiku", Label: "Haiku 5.5", Provider: "claude", ProviderEnabled: true, Available: true, Enabled: true, CanCode: true, CanVerify: false);
        var roster = new List<ModelCandidate> { astra, opus, haiku };

        if (name == "plan-by-lead")
        {
            var projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(projectFolder);
            File.WriteAllText(Path.Combine(projectFolder, "package.json"), "{\"name\":\"project\"}\n");

            var item = new ProjectItem(
                new ProjectInfo("alpha", "Alpha", projectFolder),
                [new Revision("1", "Implement service and tests")]
            );
            var job = new JobDescriptor("job-1", "Implement service and tests", "Build service");
            var megaId = "MT-00000000-0000-4000-8000-000000000001";

            var initialSub = new SubTaskProposal("astra", "Implement feature", "Build service", [])
            {
                Type = "IMPLEMENTATION",
                Title = "Implement feature",
                AssignedModelId = "astra"
            };

            var bridge = new MockExecutionBridge();
            bridge.Proposals = [initialSub];

            var leadAdapter = new MockLeadModelAdapter(exchange, astra);
            var autonomyOps = new MockAutonomyOperations(roster);

            var ok = await OrchestratorJobBuilding.PlanByLeadAsync("alpha", item, megaId, leadAdapter, bridge, autonomyOps);

            Assert.True(ok);
            Assert.Contains(bridge.MethodCalls, m => m.StartsWith($"Replan:alpha:{megaId}:", StringComparison.Ordinal));
            Assert.Equal(3, bridge.Proposals.Count);
            Assert.Equal("haiku", bridge.Proposals[0].AssignedModelId);
            Assert.Equal("astra", bridge.Proposals[1].AssignedModelId);
            Assert.Equal("astra", bridge.Proposals[2].AssignedModelId);

            var subTasksArr = new JsonArray();
            for (var i = 0; i < bridge.Proposals.Count; i++)
            {
                var sub = bridge.Proposals[i];
                var depArr = new JsonArray();
                foreach (var d in sub.DependsOn)
                {
                    depArr.Add($"MT-00000000-0000-4000-8000-000000000001-ST-{d + 1:D2}");
                }
                subTasksArr.Add(new JsonObject
                {
                    ["id"] = $"MT-00000000-0000-4000-8000-000000000001-ST-{i + 1:D2}",
                    ["title"] = sub.Title,
                    ["assignedModelId"] = sub.AssignedModelId ?? sub.ModelId,
                    ["dependencies"] = depArr
                });
            }

            return new JsonObject
            {
                ["ok"] = ok,
                ["replanned"] = ok,
                ["subTasks"] = subTasksArr
            };
        }

        if (name == "assign-by-lead")
        {
            var projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(projectFolder);
            File.WriteAllText(Path.Combine(projectFolder, "package.json"), "{\"name\":\"project\"}\n");

            var item = new ProjectItem(
                new ProjectInfo("alpha", "Alpha", projectFolder),
                [new Revision("1", "Build two components")]
            );
            var job = new JobDescriptor("job-2", "Build two components", "Dual component implementation");
            var megaId = "MT-00000000-0000-4000-8000-000000000001";

            var sub1 = new SubTaskProposal("astra", "Build service", "Dual component implementation", [])
            {
                Type = "IMPLEMENTATION",
                Title = "Build service",
                AssignedModelId = "astra"
            };
            var sub2 = new SubTaskProposal("opus", "Build tests", "Dual component implementation", [0])
            {
                Type = "IMPLEMENTATION",
                Title = "Build tests",
                AssignedModelId = "opus"
            };

            var bridge = new MockExecutionBridge();
            bridge.Proposals = [sub1, sub2];

            var leadAdapter = new MockLeadModelAdapter(exchange, astra);
            var autonomyOps = new MockAutonomyOperations(roster);

            await OrchestratorJobBuilding.AssignByLeadAsync("alpha", item, megaId, leadAdapter, bridge, autonomyOps);

            Assert.Contains(bridge.MethodCalls, m => m.StartsWith($"Reassign:alpha:{megaId}:", StringComparison.Ordinal));
            Assert.Equal("opus", bridge.Proposals[0].AssignedModelId);
            Assert.Equal("astra", bridge.Proposals[1].AssignedModelId);

            var subTasksArr = new JsonArray();
            for (var i = 0; i < bridge.Proposals.Count; i++)
            {
                var sub = bridge.Proposals[i];
                subTasksArr.Add(new JsonObject
                {
                    ["id"] = $"MT-00000000-0000-4000-8000-000000000001-ST-{i + 1:D2}",
                    ["assignedModelId"] = sub.AssignedModelId ?? sub.ModelId
                });
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["assigned"] = true,
                ["subTasks"] = subTasksArr
            };
        }

        if (name == "start-auto-task")
        {
            var projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(Path.Combine(projectFolder, ".git"));
            File.WriteAllText(Path.Combine(projectFolder, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(projectFolder, ".git", "config"), "[core]\nrepositoryformatversion = 0\n");
            File.WriteAllText(Path.Combine(projectFolder, "package.json"), "{\"name\":\"project\"}\n");

            var item = new ProjectItem(
                new ProjectInfo("alpha", "Alpha", projectFolder),
                [new Revision("rev-1", "Build web service and automated tests")],
                []
            );

            var questions = new List<string>
            {
                "Is this run the next slice of Alpha: Build web service and automated tests? Recommended: yes.",
                $"Stay inside {projectFolder.Replace('\\', '/')} and the files already there. No paid APIs. Recommended: yes.",
                "Is the slice done when the work is on disk with tests, and nothing extra ships? Recommended: yes.",
                "Is everything else out of scope until this slice is done? Recommended: yes."
            };

            var inspector = new MockFolderInspector(folder => new InspectionFacts(
                Git: false,
                LastCommit: "",
                LastSubject: "",
                Recent: [],
                Dirty: [],
                Head: "",
                Branch: "",
                FileCount: 2,
                Folder: folder,
                Error: null
            ));

            var projectReader = new MockProjectReader(item);
            var bridge = new MockExecutionBridge();
            var workPlanMgr = new MockWorkPlanManager(new LocalGitOperations(), bridge);
            var grillComposer = new MockGrillComposer(questions);

            var grill = await OrchestratorJobBuilding.StartAutoTaskAsync("alpha", new StartAutoTaskOptions(Timing: "discretion"), projectReader, inspector, workPlanMgr, bridge, grillComposer);
            var plan = await OrchestratorJobBuilding.StartAutoTaskAsync("alpha", new StartAutoTaskOptions(Timing: "deadline", DeadlineAt: "2023-11-15T00:00:00.000Z", GrillContext: "Autonomous mode: no interview"), projectReader, inspector, workPlanMgr, bridge, grillComposer);

            var autoPlanFile = Path.Combine(projectFolder, ".planwharf", "plan.md");
            if (File.Exists(autoPlanFile))
            {
                File.WriteAllText(autoPlanFile, File.ReadAllText(autoPlanFile).TrimEnd('\r', '\n') + "\n");
            }

            JsonObject FormatFacts(InspectionFacts? f, string folder)
            {
                return new JsonObject
                {
                    ["folder"] = folder,
                    ["fileCount"] = f?.FileCount ?? 2,
                    ["names"] = new JsonArray { ".git", "package.json" },
                    ["docs"] = new JsonArray(),
                    ["git"] = f?.Git ?? false,
                    ["head"] = f?.Head ?? "",
                    ["branch"] = f?.Branch ?? "",
                    ["lastCommit"] = f?.LastCommit ?? "",
                    ["commitId"] = null,
                    ["lastSubject"] = f?.LastSubject ?? "",
                    ["recent"] = new JsonArray(),
                    ["dirty"] = new JsonArray(),
                    ["diffStat"] = ""
                };
            }

            var qArray = new JsonArray();
            foreach (var q in grill.Questions ?? []) qArray.Add((JsonNode)q);
            var grillNode = new JsonObject
            {
                ["ok"] = grill.Ok,
                ["phase"] = grill.Phase,
                ["job"] = null,
                ["facts"] = FormatFacts(grill.Facts, projectFolder),
                ["questions"] = qArray,
                ["title"] = grill.Title,
                ["kind"] = grill.Kind,
                ["replace"] = grill.Replace,
                ["clearJob"] = grill.ClearJob
            };

            var planJob = new JsonObject
            {
                ["title"] = plan.Job!.Title,
                ["detail"] = plan.Job.Detail,
                ["source"] = plan.Job.Source,
                ["revision_id"] = plan.Job.RevisionId,
                ["timing"] = plan.Job.Timing,
                ["deadlineAt"] = plan.Job.DeadlineAt
            };
            var planNode = new JsonObject
            {
                ["ok"] = plan.Ok,
                ["phase"] = plan.Phase,
                ["job"] = planJob,
                ["facts"] = FormatFacts(plan.Facts, projectFolder),
                ["clearJob"] = plan.ClearJob
            };

            return new JsonObject
            {
                ["grill"] = grillNode,
                ["plan"] = planNode
            };
        }

        if (name == "open-work-plan")
        {
            var projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(Path.Combine(projectFolder, ".git"));
            File.WriteAllText(Path.Combine(projectFolder, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(projectFolder, ".git", "config"), "[core]\nrepositoryformatversion = 0\n");
            var worktreesDir = Path.Combine(root, "worktrees", "alpha", "MT-orphan");
            Directory.CreateDirectory(worktreesDir);

            var git = new MockGitOperations(exchange, root);
            var bridge = new MockExecutionBridge();
            var item = new ProjectItem(new ProjectInfo("alpha", "Alpha", projectFolder), [new Revision("1", "Implement service endpoints")]);
            var steps = new List<PlanStepEntry>
            {
                new("Build", "Astra", "Build endpoints"),
                new("Verify", "Haiku", "Verify service")
            };

            var res = OrchestratorJobBuilding.OpenWorkPlan("alpha", item, steps, git, bridge);
            var planFile = Path.Combine(projectFolder, ".planwharf", "plan.md");
            if (File.Exists(planFile))
            {
                File.WriteAllText(planFile, File.ReadAllText(planFile).TrimEnd('\r', '\n') + "\n");
            }

            Assert.True(res.Ok);
            Assert.Contains(git.MethodCalls, m => m.StartsWith("Run:worktree prune", StringComparison.Ordinal));
            Assert.Contains(git.MethodCalls, m => m == "SweepOrphans:alpha");

            return new JsonObject
            {
                ["ok"] = res.Ok,
                ["planExists"] = File.Exists(planFile),
                ["chat"] = res.Chat,
                ["orphanSwept"] = !Directory.Exists(worktreesDir)
            };
        }

        if (name == "decide")
        {
            var resultFile = Path.Combine(root, "result.txt");
            File.WriteAllText(resultFile, "STATUS: COMPLETE\nAll tests passed.\n");

            var job1 = new JobDescriptor("job-1", "Task 1", "Detail 1") { Status = "running" };
            var item = new ProjectItem(
                new ProjectInfo("alpha", "Alpha", root),
                [new Revision("1", "Goal")],
                [job1]
            );

            var row = new ProjectRow
            {
                JobId = "job-1",
                JobFingerprint = OrchestratorJobBuilding.JobFingerprint(job1),
                Dispatch = [new DispatchPart("astra", "Astra", "slice", ResultPath: resultFile)]
            };

            var bridge = new MockExecutionBridge();
            var crew = new MockCrewSelector();
            var inspector = new MockFolderInspector(folder => new InspectionFacts(
                Git: false,
                LastCommit: "",
                LastSubject: "",
                Recent: [],
                Dirty: [],
                Head: "",
                Branch: "",
                FileCount: 2,
                Folder: folder,
                Error: null
            ));
            var liveness = new MockProcessLiveness();
            var options = new DecideOptions();

            var row1 = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, options);
            var completion = new JsonObject
            {
                ["status"] = row.Status,
                ["doing"] = row.Doing,
                ["waitingFor"] = row.WaitingFor
            };

            // Mutation reset
            row.Approved = true;
            row.HoldApproval = true;
            var job2 = new JobDescriptor("job-2", "Task 2", "Detail 2") { Status = "running" };
            item = new ProjectItem(item.Project, item.Revisions, [job2]);
            var row2 = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, options);
            var mutation = new JsonObject
            {
                ["approved"] = row.Approved,
                ["holdApproval"] = row.HoldApproval,
                ["jobId"] = row.JobId
            };

            // Advance (executionVersion: 2)
            var jobV2 = new JobDescriptor("job-v2", "V2 Work", "Advance work", ExecutionVersion: 2,
                ProposedSubTasks: [new SubTaskProposal("astra", "Advance work", "Advance work", []) { Title = "Advance work", Type = "IMPLEMENTATION" }])
            {
                Status = "running"
            };
            item = new ProjectItem(item.Project, item.Revisions, [jobV2]);
            bridge.CurrentStatus = "needs_approval";
            var row3 = OrchestratorJobBuilding.Decide(item, row, bridge, crew, inspector, liveness, options);
            var advance = new JsonObject
            {
                ["status"] = row.Status,
                ["jobId"] = row.JobId
            };

            Assert.Equal("complete", completion["status"]?.GetValue<string>());
            Assert.False(mutation["approved"]?.GetValue<bool>());
            Assert.Equal("job-2", mutation["jobId"]?.GetValue<string>());
            Assert.Equal("needs_approval", advance["status"]?.GetValue<string>());
            Assert.Equal("job-v2", advance["jobId"]?.GetValue<string>());
            Assert.Contains(bridge.MethodCalls, m => m.StartsWith("Advance:alpha:job-v2:", StringComparison.Ordinal));

            return new JsonObject
            {
                ["completion"] = completion,
                ["mutation"] = mutation,
                ["advance"] = advance
            };
        }

        throw new InvalidOperationException("Missing orchestrator-job-building replay: " + name);
    }

    [Theory]
    [MemberData(nameof(ExecutionScenarios))]
    public void Replay_Execution_Scenarios(string name)
    {
        var root = Path.Combine(FindScenariosFolder(), ".verify-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var sc = GetScenario("execution", name);
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? result = null;
            string? error = null;
            var files = new ReplayExecutionFiles(root);
            try { result = RunExecutionScenario(name, root, files); }
            catch (InvalidOperationException ex) { error = ex.Message; }
            Assert.Equal(sc["error"]?.ToString(), error);
            AssertJsonAt(sc["fileEffects"], Normalize(files.Effects, root), "fileEffects");
            if (error is null) AssertJsonAt(sc["result"], Normalize(result, root), "result");
            AssertOutcome(sc, Normalize(result, root), error, GetEffects(root), exchange);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ReplayAttemptIo : IAttemptIo
    {
        private JsonObject state = JsonNode.Parse("""{"nextId":1,"attempts":[]}""")!.AsObject();
        public T WithLock<T>(Func<T> work) => work();
        public JsonObject Read() => state.DeepClone().AsObject();
        public void Write(JsonObject value) => state = value.DeepClone().AsObject();
    }

    private sealed class ReplayExecutionFiles(string root) : IExecutionFiles
    {
        private readonly ExecutionFiles inner = new();
        public JsonArray Effects { get; } = [];
        private void Guard(string path)
        {
            var full = Path.GetFullPath(path);
            Assert.True(full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Execution replay attempted filesystem access outside its isolated root.");
        }
        private void Mutate(string operation, string path, Action action, byte[]? bytes = null)
        {
            Guard(path);
            var effect = new JsonObject { ["operation"] = operation, ["path"] = path };
            if (bytes is not null) effect["content"] = Encoding.UTF8.GetString(bytes);
            action(); effect["ok"] = true; Effects.Add(effect);
        }
        public byte[] Read(string path) { Guard(path); return inner.Read(path); }
        public void Append(string path, byte[] bytes) => Mutate("appendFile", path, () => inner.Append(path, bytes), bytes);
        public void Write(string path, byte[] bytes) => Mutate("writeFile", path, () => inner.Write(path, bytes), bytes);
        public void CreateDirectory(string path) => Mutate("mkdir", path, () => inner.CreateDirectory(path));
        public string? LinkedWorkspace(string? path) { if (!string.IsNullOrEmpty(path)) Guard(path); return inner.LinkedWorkspace(path); }
        public string RealDirectory(string path) { Guard(path); return inner.RealDirectory(path); }
        public ExecutionFileStat Stat(string path) { Guard(path); return inner.Stat(path); }
    }

    private static JsonObject EObj(string json) => JsonNode.Parse(json)!.AsObject();

    private static void AssertJsonAt(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            Assert.True(eo.Select(p => p.Key).Order().SequenceEqual(ao.Select(p => p.Key).Order()),
                $"{path} keys: expected {string.Join(',', eo.Select(p => p.Key))}; actual {string.Join(',', ao.Select(p => p.Key))}");
            foreach (var (key, value) in eo) AssertJsonAt(value, ao[key], path + "." + key);
        }
        else if (expected is JsonArray ea && actual is JsonArray aa)
        {
            Assert.True(ea.Count == aa.Count, $"{path} count: {ea.Count} != {aa.Count}");
            for (var i = 0; i < ea.Count; i++) AssertJsonAt(ea[i], aa[i], path + "[" + i + "]");
        }
        else Assert.True(Same(Slashes(expected), Slashes(actual)), $"{path}: expected {expected}; actual {actual}");
    }

    private sealed class ReplayAttempts(JsonArray closed, JsonArray launches) : IExecutionAttempts
    {
        public JsonObject Claim(JsonObject input) => new() { ["ok"] = true,
            ["attempt"] = new JsonObject { ["id"] = "attempt-" + (closed.Count + launches.Count + 1) }, ["request"] = input.DeepClone() };
        public JsonObject AttachResult(JsonObject input)
        {
            closed.Add(new JsonObject { ["attached"] = input.DeepClone() });
            return new JsonObject { ["ok"] = true };
        }
        public JsonObject CloseAttempt(JsonObject input)
        {
            closed.Add(input.DeepClone());
            return new JsonObject { ["ok"] = true };
        }
    }

    private static JsonObject RunExecutionScenario(string name, string root, ReplayExecutionFiles files)
    {
        var store = EObj("""{"usage":{},"execution":{"megaTasks":[],"subTasks":[],"results":[],"checkpoints":[],"timeline":[],"decisions":[]}}""");
        var row = EObj("""{"dispatch":[]}""");
        var launches = new JsonArray();
        var closed = new JsonArray();
        var persisted = 0;
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = store, Directory = root, Attempts = AttemptEngine.Create(new ReplayAttemptIo()),
            AttemptAdapter = new ReplayAttempts(closed, launches),
            Files = files,
            RowFor = (_, _) => row, Inspect = _ => EObj("""{"git":false,"dirty":[],"names":[],"fileCount":0}"""),
            Models = () => [EObj("""{"id":"worker","label":"Fixture worker","provider":"fixture","cli":"fixture-cli","enabled":true,"available":true,"roles":{"worker":true}}""")],
            Now = () => "2023-11-14T22:13:20.000Z", LeadId = () => "lead-exact", SessionId = "session-fixed",
            NewId = () => "00000000-0000-4000-8000-000000000001", Persist = () => persisted++,
            IsProcessAlive = pid => name == "stop-kill-failure-preserves-process" ? pid == 88 :
                name == "addition-kill-failure-resets-verify" ? pid is 44 or 77 : name == "paused-addition-resumes" && pid == 77,
            KillWorker = _ => name == "addition-kill-failure-resets-verify" ? throw new IOException("Synthetic kill failure") : name != "stop-kill-failure-preserves-process",
            SpawnWorker = info => { launches.Add(info.DeepClone()); return new JsonObject { ["pid"] = 77 }; }
        });
        if (name == "helpers-and-no-verdict-matrix")
        {
            var subs = new List<JsonObject> { EObj("""{"id":"a","title":"Build","assignedModelId":"worker"}""") };
            var summaries = new JsonArray(Execution.SplitSummary(subs, id => id));
            subs.Add(EObj("""{"id":"b","title":"Verify","assignedModelId":"worker","dependencies":["a"]}"""));
            summaries.Add(Execution.SplitSummary(subs, id => id, "Fixture"));
            var input = new JsonObject { ["folder"] = root, ["before"] = new string('a', 40), ["after"] = new string('b', 40),
                ["exitCode"] = 0, ["scope"] = new JsonArray("src"), ["names"] = new JsonArray("src/a.js") };
            var delivered = new JsonArray(Execution.DeliveredWithoutVerdict(input));
            input["names"] = new JsonArray("outside.js"); delivered.Add(Execution.DeliveredWithoutVerdict(input));
            input.Remove("scope"); input["exitCode"] = 1; input["names"] = new JsonArray("src/a.js");
            delivered.Add(Execution.DeliveredWithoutVerdict(input));
            return new JsonObject { ["summaries"] = summaries, ["plans"] = new JsonArray(Execution.NoVerdictPlan(0), Execution.NoVerdictPlan(1), Execution.NoVerdictPlan(2)),
                ["delivered"] = delivered, ["repo"] = Execution.RepoState(root, EObj("""{"git":true,"commitId":"head-fixed","dirty":["?? missing.txt"]}""")) };
        }
        var item = EObj("""{"project":{"id":"project"},"revisions":[{"id":1,"direction":"Goal"}]}""");
        item["project"]!["folder"] = name == "proposal-guard-no-linked-folder" ? null : root;
        var job = EObj("""{"id":"job","title":"Fixture work","detail":"Goal"}""");
        if (name == "proposal-approval-and-recovery")
        {
            item["revisions"]![0]!["id"] = 4; item["revisions"]![0]!["direction"] = "Fixture goal";
            job = EObj("""{"id":"job","revision_id":4,"title":"Fixture work","detail":"Exercise proposal","timing":"deadline","deadlineAt":"2023-11-15T00:00:00.000Z"}""");
            var mega = bridge.Propose(item, job, [EObj("""{"title":"Build fixture","modelId":"worker","scope":["src/a.js"]}"""),
                EObj("""{"title":"Verify fixture","modelId":"worker","dependsOn":[0],"type":"AUDIT"}""")]);
            var approved = bridge.Approve("project", mega["id"]!.ToString());
            var recovered = bridge.Propose(item, job, []);
            return new JsonObject { ["mega"] = mega.DeepClone(), ["approved"] = approved.DeepClone(),
                ["recoveredSame"] = recovered["id"]!.ToString() == mega["id"]!.ToString(), ["persisted"] = persisted, ["store"] = store };
        }
        if (name.StartsWith("proposal-guard-", StringComparison.Ordinal))
        {
            var parts = new List<JsonObject>();
            if (name != "proposal-guard-no-crew") parts.Add(EObj("""{"title":"Build","modelId":"worker"}"""));
            if (name == "proposal-guard-cyclic-dependency") parts[0]["dependsOn"] = new JsonArray(0);
            try { return new JsonObject { ["proposed"] = bridge.Propose(item, job, parts).DeepClone(), ["store"] = store, ["launches"] = launches }; }
            catch (InvalidOperationException error) { return new JsonObject { ["error"] = error.Message, ["store"] = store, ["launches"] = launches }; }
        }
        item["project"]!["folder"] = null;
        if (name is "addition-kill-failure-resets-verify" or "paused-addition-resumes" or "first-answer-after-start" or "delegation-replaces-after-start")
        {
            var paused = name == "paused-addition-resumes";
            var killFailure = name == "addition-kill-failure-resets-verify";
            var mega = EObj("""{"id":"MT-fixed","projectId":"project","taskId":"job","title":"Fixture work","goal":"Goal","status":"RUNNING","approvedAt":"yes","authorizedSessionId":"session-fixed","pauseRequested":false,"jobFingerprint":"7c635f0996c3e96cba77d8dbe78c05153906baf54e5d3033fd3abc70bd4752db","repoBefore":{"repoHead":null,"dirtyFiles":[],"comparable":true,"gitReadable":false,"fingerprint":null,"disposition":"NOT APPLICABLE"}}""");
            mega["status"] = paused ? "PAUSED" : "RUNNING"; mega["pauseRequested"] = paused;
            var verify = EObj("""{"id":"MT-fixed-ST-02","megaTaskId":"MT-fixed","title":"Verify the work independently","status":"PENDING","dependencies":["MT-fixed-ST-01"],"assignedModelId":"worker","proposedProvider":"fixture","proposedCli":"fixture-cli","attemptIds":[],"resultId":null}""");
            var build = EObj("""{"id":"MT-fixed-ST-01","megaTaskId":"MT-fixed","title":"Build fixture","type":"IMPLEMENTATION","status":"COMPLETE","dependencies":[],"assignedModelId":"worker","proposedProvider":"fixture","proposedCli":"fixture-cli","attemptIds":[],"resultId":null}""");
            build["status"] = paused ? "PENDING" : "COMPLETE";
            if (killFailure)
            {
                verify["status"] = "RUNNING"; verify["attemptIds"] = new JsonArray("attempt-fixed");
                row["dispatch"]!.AsArray().Add(EObj("""{"subTaskId":"MT-fixed-ST-02","pid":44,"alive":true,"observedAlive":true,"attemptId":"attempt-fixed"}"""));
            }
            var decision = EObj("""{"id":"D-fixed","projectId":"project","megaTaskId":"MT-fixed","answer":"","suggestion":"suggested answer"}""");
            if (name == "delegation-replaces-after-start") decision["answer"] = "old answer";
            bridge.Data.MegaTasks.Add(mega); bridge.Data.SubTasks.Add(build); bridge.Data.SubTasks.Add(verify); bridge.Data.Decisions.Add(decision);
            if (paused) bridge.Data.Checkpoints.Add(EObj("""{"id":"CP-fixed","megaTaskId":"MT-fixed"}"""));
            if (name == "first-answer-after-start")
            {
                var first = bridge.AnswerDecision("project", "D-fixed", "owner answer");
                return new JsonObject { ["first"] = first.DeepClone(), ["decision"] = decision.DeepClone(), ["persisted"] = persisted };
            }
            if (name == "delegation-replaces-after-start")
            {
                var delegated = bridge.DelegateDecision("project", "D-fixed", "Lead fixture");
                return new JsonObject { ["delegated"] = delegated.DeepClone(), ["decision"] = decision.DeepClone(), ["persisted"] = persisted };
            }
            var appended = bridge.AppendAddition(item, EObj("""{"id":"job","detail":"Goal"}"""), "MT-fixed", EObj("""{"addition":"Add `src/new.js`."}"""));
            return new JsonObject { ["appended"] = appended.DeepClone(), ["closed"] = closed, ["launches"] = launches, ["persisted"] = persisted, ["row"] = row, ["store"] = store };
        }
        if (name is "dead-worker-retries-once" or "stop-kill-failure-preserves-process")
        {
            var stopping = name == "stop-kill-failure-preserves-process";
            var id = stopping ? "MT-stop" : "MT-retry";
            var mega = EObj("""{"projectId":"project","taskId":"job","status":"RUNNING","approvedAt":"yes","authorizedSessionId":"session-fixed","repoBefore":{"repoHead":null}}""");
            mega["id"] = id; mega["title"] = stopping ? "Stop fixture" : "Retry fixture";
            var sub = EObj("""{"title":"Build fixture","status":"RUNNING","assignedModelId":"worker","proposedProvider":"fixture","proposedCli":"fixture-cli","provider":"fixture","cli":"fixture-cli","resultId":null,"dependencies":[]}""");
            sub["id"] = id + "-ST-01"; sub["megaTaskId"] = id; sub["attemptIds"] = new JsonArray(stopping ? "attempt-live" : "attempt-dead");
            if (!stopping) sub["type"] = "IMPLEMENTATION";
            var part = new JsonObject { ["subTaskId"] = id + "-ST-01", ["attemptId"] = stopping ? "attempt-live" : "attempt-dead",
                ["pid"] = stopping ? 88 : 66, ["alive"] = true, ["structuredResultPath"] = Path.Combine(root, stopping ? "live.json" : "dead.json") };
            if (!stopping)
            {
                part["folder"] = null; part["projectFolder"] = null;
                part["logPath"] = Path.Combine(root, "dead.log"); part["exitPath"] = Path.Combine(root, "dead.exit.json");
            }
            bridge.Data.MegaTasks.Add(mega); bridge.Data.SubTasks.Add(sub); row["dispatch"]!.AsArray().Add(part);
            if (stopping)
            {
                item["revisions"]![0]!.AsObject().Remove("direction");
                var error = Assert.Throws<InvalidOperationException>(() => bridge.Stop(item, EObj("""{"id":"job"}"""), id));
                return new JsonObject { ["error"] = error.Message, ["row"] = row, ["store"] = store, ["persisted"] = persisted };
            }
            var result = bridge.Advance(item, EObj("""{"id":"job","detail":"Goal"}"""), [], false);
            return new JsonObject { ["result"] = result.DeepClone(), ["closed"] = closed, ["persisted"] = persisted, ["store"] = store, ["row"] = row };
        }
        throw new InvalidOperationException("Missing execution replay: " + name);
    }

    private sealed class ReplayLeadModelAdapter(
        Exchange exchange,
        List<JsonNode> events,
        Func<bool> isCurrent,
        Action cancel) : OrchestratorLeadQuestions.ILeadModelAdapter
    {
        public int CallCount { get; private set; }
        public List<string> ObservedModelIds { get; } = [];
        public List<string> ObservedStates { get; } = [];

        public ValueTask<OrchestratorLeadQuestions.LeadSpokenResponse> AskModelAsync(
            OrchestratorLeadQuestions.LeadAskInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(isCurrent());

            var call = ++CallCount;
            var modelId = invocation.Model.Data["id"]!.GetValue<string>();
            ObservedModelIds.Add(modelId);
            ObservedStates.Add(invocation.State.ToJsonString());

            if (call == 1 && invocation.Question == "What is the current status?")
            {
                Assert.NotNull(invocation.OnProgress);
                invocation.OnProgress!(new OrchestratorLeadQuestions.LeadAskProgress("Inspecting Alpha", "", 4));
                invocation.OnProgress!(new OrchestratorLeadQuestions.LeadAskProgress("Inspecting Alpha", "Draft answer", 8));
            }
            else if (call == 1 && invocation.Question == "Cancel this Alpha turn.")
            {
                Assert.NotNull(invocation.OnProgress);
                invocation.OnProgress!(new OrchestratorLeadQuestions.LeadAskProgress("Halfway", "Unsafe partial draft", 7));
                cancel();
                invocation.OnProgress!(new OrchestratorLeadQuestions.LeadAskProgress("Stale thinking", "Stale draft", 9));
                events.Add(new JsonObject { ["type"] = "abort", ["aborted"] = true, ["asks"] = 1 });
            }
            else
            {
                var invoked = new JsonObject { ["type"] = "invoke", ["call"] = call, ["modelId"] = modelId };
                if (invocation.Question.StartsWith("Decide whether Alpha", StringComparison.Ordinal))
                    invoked["question"] = invocation.Question;
                events.Add(invoked);
            }

            var actualRequest = new JsonObject
            {
                ["kind"] = "askModel",
                ["modelId"] = modelId,
                ["question"] = invocation.Question,
                ["projectId"] = invocation.ProjectId,
                ["allowActions"] = invocation.AllowActions is bool allow ? JsonValue.Create(allow) : null,
                ["state"] = invocation.State.DeepClone()
            };
            var reply = exchange.Call(actualRequest).AsObject();
            var response = new OrchestratorLeadQuestions.LeadSpokenResponse(
                Answer: reply["answer"]?.GetValue<string>(),
                Thinking: reply["thinking"]?.GetValue<string>(),
                Ms: reply["ms"]?.GetValue<long>(),
                Actions: reply["actions"] is JsonArray actions ? (JsonArray)actions.DeepClone() : null,
                Failure: reply["failure"]?.GetValue<string>(),
                Diagnostic: reply["diagnostic"] is JsonObject diagnostic ? (JsonObject)diagnostic.DeepClone() : null,
                ProcessExit: reply["processExit"]?.GetValue<int>(),
                Tokens: reply["tokens"] is JsonObject tokens ? (JsonObject)tokens.DeepClone() : null,
                Error: reply["error"]?.GetValue<string>(),
                Metadata: reply["metadata"] is JsonObject metadata ? (JsonObject)metadata.DeepClone() : null);
            return ValueTask.FromResult(response);
        }
    }

    private static IReadOnlyList<OrchestratorLeadQuestions.Model> LeadQuestionRoster() =>
    [
        new(EObj("""{"id":"astra","label":"Astra","provider":"codex","providerId":"codex","adapter":"codex","cli":"gpt-6-astra","plan":"chatgpt","quotaPoolId":"chatgpt","kind":"premium","enabled":true,"available":true,"limit":100,"roles":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false},"roleEligibility":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false}}""")),
        new(EObj("""{"id":"opus","label":"Opus","provider":"claude","providerId":"claude","adapter":"claude","cli":"claude-opus-5-5","plan":"claude","quotaPoolId":"claude","kind":"premium","enabled":true,"available":true,"limit":100,"roles":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false},"roleEligibility":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false}}"""))
    ];

    private static JsonObject LeadQuestionSnapshotData() => EObj("""
        {
          "orchestratorId":"astra","orchestratorLabel":"Astra","mode":"manual","grillPropose":false,"grillKind":"off",
          "projects":[
            {
              "id":"alpha","name":"Alpha","folder":null,"github":null,
              "repository":{
                "checkedAt":"2023-11-14T22:13:20.000Z","folder":null,"github":null,
                "local":{"exists":false,"readable":false,"writable":false,"git":false},
                "remote":{"accessible":false,"error":"No repository linked."},
                "comparison":{"status":"unknown","ahead":null,"behind":null}
              },
              "status":"idle","label":"Idle","goal":"Ship Alpha safely","stateArt":"","open":[],
              "analysis":"Reading the notebook. No job to split. Waiting for: Auto start.",
              "jobTitle":null,"plan":[],"checkpoint":null,
              "orchestratorDoing":"Reading the notebook. No job to split.","waitingFor":"Auto start",
              "next":"Relink the project folder on this PC.","facts":null,"dispatch":[],"monitoring":null,"workerTail":""
            },
            {
              "id":"beta","name":"Beta","folder":null,"status":"idle","label":"Idle",
              "goal":"PRIVATE_BETA_GOAL",
              "notes":[{"id":"N-beta","text":"PRIVATE_BETA_NOTE","aiRelevant":true}],
              "dispatch":[]
            }
          ],
          "execution":{"megaTasks":[],"subTasks":[],"results":[],"checkpoints":[]},
          "researches":[{"id":"res-beta","projectId":"beta","title":"PRIVATE_BETA_RESEARCH","status":"COMPLETE","reportPath":"beta-report.md"}],
          "fleet":[
            {
              "id":"astra","label":"Astra","role":"Lead","plan":"chatgpt","exhausted":false,"enabled":true,
              "roles":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false},
              "roleEligibility":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false},
              "lead":true
            },
            {
              "id":"opus","label":"Opus","role":"Orchestrator / Worker / Verifier / Reviewer","plan":"claude","exhausted":false,"enabled":true,
              "roles":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false},
              "roleEligibility":{"orchestrator":true,"worker":true,"verifier":true,"reviewer":true,"supervisor":false},
              "lead":false
            }
          ],
          "plans":[
            {"id":"ChatGPT","label":"ChatGPT","readable":false,"exhausted":false,"pct":null,"leftPct":null,"windows":[]},
            {"id":"Grok","label":"Grok","readable":false,"exhausted":false,"pct":null,"leftPct":null,"windows":[]},
            {"id":"Gemini","label":"Gemini","readable":false,"exhausted":false,"pct":null,"leftPct":null,"windows":[]}
          ]
        }
        """);

    private static OrchestratorLeadQuestions.LeadAskTurnRequest CreateLeadAskRequest(
        JsonObject scenario,
        IReadOnlyList<OrchestratorLeadQuestions.Model> roster,
        Func<bool> isCurrent)
    {
        var expected = scenario["requests"]![0]!.AsObject();
        var snapData = LeadQuestionSnapshotData();
        var alphaData = snapData["projects"]![0]!.DeepClone().AsObject();
        return new OrchestratorLeadQuestions.LeadAskTurnRequest(
            Question: expected["question"]!.GetValue<string>(),
            ProjectId: "alpha",
            ChatSessionId: "chat-fixed",
            RequestMode: "default",
            RequestedGrillKind: null,
            GoldModel: null,
            Snapshot: new OrchestratorLeadQuestions.Snapshot(snapData),
            TargetProject: new OrchestratorLeadQuestions.Project(alphaData),
            ConversationHistory: [],
            Roster: roster,
            IsCurrent: isCurrent,
            StateFile: null);
    }

    private static OrchestratorLeadQuestions.LeadDecisionRequest CreateLeadDecisionRequest(
        JsonObject scenario,
        IReadOnlyList<OrchestratorLeadQuestions.Model> roster,
        Func<bool> isCurrent)
    {
        var expected = scenario["requests"]![0]!.AsObject();
        var item = new OrchestratorLeadQuestions.Item(
            EObj("""{"project":{"id":"alpha","name":"Alpha","folder":null},"revisions":[{"id":"rev-a","direction":"Ship Alpha safely"}]}"""));
        return new OrchestratorLeadQuestions.LeadDecisionRequest(
            ProjectId: "alpha",
            Item: item,
            Settings: EObj("""{"enabled":true,"assumptions":[],"approved":[],"continued":[],"evaluated":[]}"""),
            Question: expected["question"]!.GetValue<string>(),
            Roster: roster,
            EvalMegaTaskId: null,
            IsCurrent: isCurrent);
    }

    private static void AssertNoPrivateBetaLeak(
        IReadOnlyList<string> observedStates,
        IReadOnlyList<OrchestratorLeadQuestions.LeadEffect> effects,
        JsonNode? outputNode)
    {
        foreach (var marker in new[] { "PRIVATE_BETA_GOAL", "PRIVATE_BETA_NOTE", "PRIVATE_BETA_RESEARCH" })
        {
            Assert.All(observedStates, state => Assert.DoesNotContain(marker, state, StringComparison.Ordinal));
            Assert.All(effects, effect => Assert.DoesNotContain(marker, effect.Payload.ToJsonString(), StringComparison.Ordinal));
            if (outputNode is not null)
                Assert.DoesNotContain(marker, outputNode.ToJsonString(), StringComparison.Ordinal);
        }
    }

    private static string FindScenariosFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "porting", "parity", "scenarios");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("native/porting/parity/scenarios not found above " + AppContext.BaseDirectory);
    }

    private static JsonObject LoadFixture(string module)
    {
        var file = Path.Combine(ScenariosFolder, $"{module}.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    public static TheoryData<string> GetScenarios(string module)
    {
        var data = new TheoryData<string>();
        var fixture = LoadFixture(module);
        var scenarios = fixture["scenarios"]!.AsArray();
        foreach (var sc in scenarios)
        {
            data.Add(sc!["name"]!.GetValue<string>());
        }
        return data;
    }

    private static JsonObject GetScenario(string module, string name)
    {
        var fixture = LoadFixture(module);
        foreach (var sc in fixture["scenarios"]!.AsArray())
        {
            if (sc!["name"]!.GetValue<string>() == name) return sc.AsObject();
        }
        throw new KeyNotFoundException($"Scenario '{name}' not found in {module}.json");
    }

    public sealed class Exchange
    {
        private readonly JsonArray requests;
        private readonly JsonArray replies;
        private readonly string root;
        private int cursor;

        public Exchange(JsonArray requests, JsonArray replies, string root)
        {
            this.requests = requests;
            this.replies = replies;
            this.root = root;
        }

        public JsonNode Call(JsonNode? actualRequest)
        {
            var normalized = Normalize(actualRequest, root);
            if (cursor >= requests.Count)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected request #{cursor + 1}: {normalized?.ToJsonString()}");
            }
            var expectedReq = requests[cursor];
            if (!Same(expectedReq, normalized))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Request #{cursor + 1} mismatch:\nExpected: {expectedReq?.ToJsonString()}\nActual:   {normalized?.ToJsonString()}");
            }
            return replies[cursor++]!;
        }

        public void AssertAllConsumed()
        {
            if (cursor != requests.Count)
            {
                throw new Xunit.Sdk.XunitException($"Unconsumed replies: used {cursor} of {requests.Count}");
            }
        }
    }

    // The root as the engine sees it once links are followed: on macOS the temporary folder /var/... is a
    // link to /private/var/..., and the engine resolves paths the way fs.realpathSync does.
    public static string RealRootForTests(string path) => RealRoot(path);

    static string RealRoot(string root)
    {
        var current = Path.GetPathRoot(root) ?? "";
        foreach (var part in root[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target) current = target.FullName;
        }
        return current;
    }

    public static JsonNode? Normalize(JsonNode? value, string root)
    {
        if (value == null) return null;
        var forwardRoot = root.Replace('\\', '/');
        if (value is JsonValue linked && linked.TryGetValue<string>(out var text) && RealRoot(root) is var real && real != root && (text.Contains(real) || text.Contains(real.Replace('\\', '/'))))
            value = JsonValue.Create(text.Replace(real, root).Replace(real.Replace('\\', '/'), forwardRoot));
        if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            if (s.Contains(root) || s.Contains(forwardRoot))
            {
                try
                {
                    var parsed = JsonNode.Parse(s);
                    if (parsed is JsonObject or JsonArray)
                    {
                        var normParsed = Normalize(parsed, root);
                        var formatted = normParsed?.ToJsonString() ?? "";
                        if (s.EndsWith('\n')) formatted += "\n";
                        return JsonValue.Create(formatted);
                    }
                }
                catch { }
                return JsonValue.Create(s.Replace(root, "<ROOT>").Replace(forwardRoot, "<ROOT>").Replace('\\', '/'));
            }
            return JsonValue.Create(s.Replace("synthetic-token", "<REDACTED>"));
        }
        if (value is JsonArray arr)
        {
            var res = new JsonArray();
            foreach (var item in arr) res.Add(Normalize(item?.DeepClone(), root));
            return res;
        }
        if (value is JsonObject obj)
        {
            var res = new JsonObject();
            foreach (var (k, v) in obj) res[k] = Normalize(v?.DeepClone(), root);
            return res;
        }
        return value.DeepClone();
    }

    public static JsonObject GetEffects(string root)
    {
        var output = new JsonObject();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
        var rootSlash = root.Replace('\\', '/');
        var rootEscaped = root.Replace("\\", "\\\\");
        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = File.ReadAllText(file);
            content = content
                .Replace(rootEscaped, "<ROOT>")
                .Replace(rootSlash, "<ROOT>")
                .Replace(root, "<ROOT>")
                .Replace("<ROOT>\\\\", "<ROOT>/")
                .Replace("<ROOT>\\", "<ROOT>/")
                .Replace("synthetic-token", "<REDACTED>")
                .Replace("\r\n", "\n");
            output[relPath] = content;
        }
        return output;
    }

    public static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Count == ob.Count && oa.All(pair => ob.TryGetPropertyValue(pair.Key, out var other) && Same(pair.Value, other));
        if (a is JsonArray aa && b is JsonArray ab)
            return aa.Count == ab.Count && aa.Zip(ab).All(pair => Same(pair.First, pair.Second));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                return double.Parse(va.ToJsonString(), CultureInfo.InvariantCulture) == double.Parse(vb.ToJsonString(), CultureInfo.InvariantCulture);
            if (va.TryGetValue<string>(out var sa) && vb.TryGetValue<string>(out var sb))
            {
                sa = sa.Replace("\r\n", "\n");
                sb = sb.Replace("\r\n", "\n");
                if (sa == sb) return true;
                try
                {
                    var ja = JsonNode.Parse(sa);
                    var jb = JsonNode.Parse(sb);
                    if (ja != null && jb != null) return Same(ja, jb);
                }
                catch { }
                return false;
            }
            return JsonNode.DeepEquals(va, vb);
        }
        return false;
    }

    // The scenarios were recorded on Windows: a path there uses "\\". The separator is the computer's, not a
    // difference between the JavaScript and the C#, so both sides are compared with "/".
    private static JsonNode? Slashes(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(text.Replace('\\', '/')),
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Slashes(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Slashes).ToArray()),
        _ => node?.DeepClone()
    };

    private static void AssertOutcome(JsonObject sc, JsonNode? actualResult, string? actualError, JsonObject actualEffects, Exchange exchange, List<JsonNode>? events = null)
    {
        if (actualError != null && !sc.ContainsKey("error"))
        {
            throw new Xunit.Sdk.XunitException($"Unexpected error for {sc["name"]}: '{actualError}'");
        }
        exchange.AssertAllConsumed();

        if (sc.TryGetPropertyValue("error", out var expErrNode) && expErrNode is JsonValue ev && ev.TryGetValue<string>(out var expErr))
        {
            Assert.True(actualError != null, $"Expected error '{expErr}' but got result: {actualResult?.ToJsonString()}");
            Assert.Contains(expErr, actualError);
        }
        else if (sc.TryGetPropertyValue("result", out var expResNode))
        {
            Assert.True(actualError == null, $"Expected result but got error: '{actualError}'");
            Assert.True(Same(Slashes(expResNode), Slashes(actualResult)),
                $"Result mismatch for {sc["name"]}:\nExpected: {expResNode?.ToJsonString()}\nActual:   {actualResult?.ToJsonString()}");
        }

        if (sc.TryGetPropertyValue("effects", out var expEffNode) && expEffNode is JsonObject expEff)
        {
            Assert.True(Same(Slashes(expEff), Slashes(actualEffects)),
                $"Effects mismatch for {sc["name"]}:\nExpected: {expEff.ToJsonString()}\nActual:   {actualEffects.ToJsonString()}");
        }

        if (events != null && sc.TryGetPropertyValue("events", out var expEventsNode) && expEventsNode is JsonArray expEvents)
        {
            var actualEventsArray = new JsonArray(events.Select(e => e.DeepClone()).ToArray());
            if (sc["name"]?.GetValue<string>() == "bad-listeners" && expEvents.Count == 1 && actualEventsArray.Count == 1)
            {
                var expStderr = expEvents[0]?["stderr"]?.GetValue<string>();
                var actStderr = actualEventsArray[0]?["stderr"]?.GetValue<string>();
                Assert.True(expStderr != null && actStderr != null && actStderr.Length > 0);
            }
            else
            {
                Assert.True(Same(expEvents, actualEventsArray),
                    $"Events mismatch for {sc["name"]}:\nExpected: {expEvents.ToJsonString()}\nActual:   {actualEventsArray.ToJsonString()}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "provider-models")]
    public async Task Replay_ProviderModels_Scenarios(string scenarioName)
    {
        var sc = GetScenario("provider-models", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pm-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunProviderModelsAsync(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "provider-adapters")]
    public async Task Replay_ProviderAdapters_Scenarios(string scenarioName)
    {
        var sc = GetScenario("provider-adapters", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pa-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunProviderAdaptersAsync(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "api-agent")]
    public async Task Replay_ApiAgent_Scenarios(string scenarioName)
    {
        var sc = GetScenario("api-agent", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-aa-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            var events = new List<JsonNode>();
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunApiAgentAsync(sc, root, exchange, events);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange, events);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "worktrees")]
    public void Replay_Worktrees_Scenarios(string scenarioName)
    {
        var sc = GetScenario("worktrees", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-wt-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = RunWorktrees(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            if (actualError != null)
                actualError = actualError.Replace(RealRoot(root), root).Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "pr-check")]
    public async Task Replay_PrCheck_Scenarios(string scenarioName)
    {
        var sc = GetScenario("pr-check", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pr-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunPrCheckAsync(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            if (actualError != null)
                actualError = actualError.Replace(RealRoot(root), root).Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "phone-activation-check")]
    public async Task Replay_PhoneActivationCheck_Scenarios(string scenarioName)
    {
        var sc = GetScenario("phone-activation-check", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pac-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            var events = new List<JsonNode>();
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunPhoneActivationCheckAsync(sc, root, exchange, events);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            if (actualError != null)
                actualError = actualError.Replace(RealRoot(root), root).Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange, events);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<JsonNode?> RunProviderModelsAsync(JsonObject sc, string root, Exchange exchange)
    {
        var name = sc["name"]!.GetValue<string>();
        var mode = sc["input"]?["mode"]?.GetValue<string>() ?? "";

        if (name.StartsWith("legacy-"))
        {
            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["provider"] = "grok", ["cli"] = "grok-exact", ["available"] = true, ["enabled"] = true }
            };
            int calls = 0;
            TaskCompletionSource? release = null;
            if (mode == "single-flight") release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var api = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Now = () => Epoch,
                LegacyDiscover = async () =>
                {
                    calls++;
                    if (mode == "single-flight" && release != null) await release.Task;
                    var req = new JsonObject { ["kind"] = "discover", ["call"] = calls };
                    var rep = exchange.Call(req);
                    return rep.AsObject();
                }
            });

            var first = api.RefreshAsync(true);
            if (mode == "single-flight")
            {
                var second = api.RefreshAsync(true);
                release!.SetResult();
                await Task.WhenAll(first, second);
            }
            else
            {
                await first;
            }
            if (mode == "ttl") await api.RefreshAsync();
            if (mode == "failure-recovery") await api.RefreshAsync(true);

            return new JsonObject
            {
                ["snapshot"] = api.Snapshot(),
                ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["calls"] = calls
            };
        }

        if (name.StartsWith("catalog-"))
        {
            var provider = new JsonObject
            {
                ["id"] = "fixture", ["adapter"] = "codex", ["quotaPoolId"] = "fixture",
                ["account"] = new JsonObject { ["key"] = "fake-account" },
                ["enabled"] = mode != "disabled"
            };
            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["providerId"] = "fixture", ["cli"] = "gpt-exact", ["available"] = true, ["enabled"] = true }
            };
            var cacheFile = Path.Combine(root, "catalog.json");
            if (mode == "rename-failure") Directory.CreateDirectory(cacheFile);
            int calls = 0;
            TaskCompletionSource? release = null;
            TaskCompletionSource? firstDiscoverStarted = null;
            if (mode == "stale-generation")
            {
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                firstDiscoverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Func<JsonObject?, Task<JsonObject>> discover = async p =>
            {
                calls++;
                var req = new JsonObject
                {
                    ["kind"] = "discover",
                    ["provider"] = p != null ? (JsonNode)p.DeepClone() : null,
                    ["call"] = calls
                };
                var rep = exchange.Call(req);
                if (mode == "stale-generation" && calls == 1)
                {
                    firstDiscoverStarted?.TrySetResult();
                    if (release != null) await release.Task;
                }
                return rep.AsObject();
            };

            Func<ProviderModels.IProviderModelsInstance> create = () => ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Providers = [provider],
                Now = () => Epoch,
                CacheFile = cacheFile,
                Discover = discover,
                FindExecutable = _ => mode != "not-installed"
            });

            var api = create();
            if (mode == "stale-generation")
            {
                var pending = api.RefreshAsync(true);
                await firstDiscoverStarted!.Task;
                await api.InvalidateAsync("fixture");
                var queued = api.RefreshAsync(true);
                release!.SetResult();
                await Task.WhenAll(pending, queued);
            }
            else
            {
                await api.RefreshAsync(true);
                if (mode == "restart")
                {
                    api = create();
                    await api.RefreshAsync(true);
                }
                if (mode == "invalidate-recovery")
                {
                    await api.InvalidateAsync("fixture");
                    await api.RefreshAsync(true);
                }
                if (mode == "rename-failure")
                {
                    try
                    {
                        await api.InvalidateAsync("fixture");
                    }
                    catch (Exception ex)
                    {
                        return new JsonObject
                        {
                            ["error"] = ex.Message,
                            ["snapshot"] = api.Snapshot(),
                            ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray())
                        };
                    }
                }
            }

            return new JsonObject
            {
                ["snapshot"] = api.Snapshot(),
                ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["calls"] = calls
            };
        }

        if (name.StartsWith("grok-list-"))
        {
            var fakeGrok = Path.Combine(root, "fake-grok");
            File.WriteAllText(fakeGrok, "");
            var savedCwd = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(root);
            Environment.SetEnvironmentVariable("PLANWHARF_GROK", "fake-grok");

            ProviderModels.GrokExecutor = cmd =>
            {
                var req = new JsonObject
                {
                    ["kind"] = "process",
                    ["command"] = cmd,
                    ["args"] = new JsonArray { (JsonNode)"models" },
                    ["options"] = new JsonObject { ["windowsHide"] = true, ["timeout"] = 15000, ["maxBuffer"] = 1048576 }
                };
                var rep = exchange.Call(req).AsObject();
                int code = rep.ContainsKey("error") && rep["error"] is JsonObject eo && eo.ContainsKey("code")
                    ? eo["code"]!.GetValue<int>()
                    : 0;
                string stdout = rep["stdout"]?.GetValue<string>() ?? "";
                string stderr = rep["stderr"]?.GetValue<string>() ?? "";
                return Task.FromResult((code, stdout, stderr));
            };
            try
            {
                var res = await ProviderModels.ListGrokAsync();
                try { File.Delete(fakeGrok); } catch { }
                return res;
            }
            finally
            {
                ProviderModels.GrokExecutor = null;
                Directory.SetCurrentDirectory(savedCwd);
                Environment.SetEnvironmentVariable("PLANWHARF_GROK", null);
                try { File.Delete(fakeGrok); } catch { }
            }
        }

        if (name == "switch-away-and-back-rejects-old-generation")
        {
            var provider = new JsonObject
            {
                ["id"] = "fixture", ["adapter"] = "codex",
                ["account"] = new JsonObject { ["key"] = "fake-A" },
                ["quotaPoolId"] = "fixture"
            };
            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["providerId"] = "fixture", ["cli"] = "gpt-exact", ["available"] = false }
            };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The account changes while the first discovery is running, as in the JavaScript, where it starts at
            // once. Without waiting, a busy test run could switch the account before it started.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var api = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Providers = [provider],
                Now = () => Epoch,
                Discover = async p =>
                {
                    calls++;
                    var req = new JsonObject
                    {
                        ["kind"] = "discover",
                        ["provider"] = p != null ? (JsonNode)p.DeepClone() : null,
                        ["call"] = calls
                    };
                    var rep = exchange.Call(req);
                    if (calls == 1) { started.TrySetResult(); await release.Task; }
                    return rep.AsObject();
                }
            });

            var pending = api.RefreshAsync(true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            provider["account"]!["key"] = "fake-B"; await api.InvalidateAsync("fixture"); api.ObserveScope("fixture");
            provider["account"]!["key"] = "fake-A"; await api.InvalidateAsync("fixture"); api.ObserveScope("fixture");
            var next = api.RefreshAsync(true);
            release.SetResult();
            await Task.WhenAll(pending, next);

            return new JsonObject
            {
                ["snapshot"] = api.Snapshot(),
                ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["calls"] = calls
            };
        }

        throw new NotSupportedException($"Scenario '{name}' not supported");
    }

    private static async Task<JsonNode?> RunProviderAdaptersAsync(JsonObject sc, string root, Exchange exchange)
    {
        var mode = sc["name"]!.GetValue<string>();
        ProviderAdapters.ResetBuiltIns();

        if (mode is "success" or "missing-auth" or "http-error" or "malformed" or "identity" or "registration")
        {
            if (mode != "missing-auth")
            {
                File.WriteAllText(Path.Combine(root, "auth.json"),
                    "{\"tokens\":{\"access_token\":\"synthetic-token\",\"account_id\":\"fake-account\"}}");
            }

            ProviderAdapters.FetchImpl = (url, reqMsg) =>
            {
                var headersObj = new JsonObject();
                foreach (var h in reqMsg.Headers) headersObj[h.Key] = string.Join(", ", h.Value);
                headersObj["Authorization"] = "<REDACTED>";
                var req = new JsonObject
                {
                    ["kind"] = "fetch",
                    ["url"] = url,
                    ["headers"] = headersObj,
                    ["timeoutMs"] = 10000
                };
                var rep = exchange.Call(req).AsObject();
                var status = rep["status"]!.GetValue<int>();
                var resp = new HttpResponseMessage((System.Net.HttpStatusCode)status)
                {
                    Content = new StringContent(rep["body"]!.ToJsonString(), Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            try
            {
                var adapter = ProviderAdapters.AdapterFor("codex")!;
                if (mode == "identity")
                {
                    return ProviderAdapters.BuildResultIdentity(new JsonObject
                    {
                        ["provider"] = "codex",
                        ["providerId"] = "custom",
                        ["cli"] = "gpt-exact",
                        ["accountKey"] = "fake-account"
                    });
                }
                if (mode == "registration")
                {
                    ProviderAdapters.RegisterAdapter("codex", new ProviderAdapters.AdapterDefinition(
                        NativeModel: null!, SourceProtectedResearch: false, Capabilities: new JsonObject(),
                        Executable: _ => null, Availability: _ => new JsonObject(), Execute: null!, Environment: null!,
                        Terminate: null!, QuotaAsync: null!, DiscoverAsync: null!, ResultIdentity: null!
                    ));
                    return null;
                }
                return await ProviderAdapters.DiscoverCodexAsync(new JsonObject { ["settingsFolder"] = root }, CancellationToken.None);
            }
            finally
            {
                ProviderAdapters.FetchImpl = null;
                ProviderAdapters.ResetBuiltIns();
            }
        }

        if (mode == "unknown-adapter")
        {
            return new JsonObject { ["found"] = ProviderAdapters.AdapterFor("unknown") != null };
        }

        if (mode == "account-identity")
        {
            return ProviderAdapters.AdapterFor("codex")!.ResultIdentity(new JsonObject
            {
                ["providerId"] = "fixture",
                ["cli"] = "gpt-exact",
                ["accountKey"] = "fake-account"
            });
        }

        if (mode is "missing-method" or "missing-regex" or "registered")
        {
            Func<JsonObject, string, string, bool, bool, LaunchSpecResult>? execute = null;
            if (mode != "missing-method")
            {
                execute = (m, f, pr, l, r) =>
                {
                    exchange.Call(new JsonObject { ["method"] = "execute", ["args"] = new JsonArray { m?.DeepClone(), f, pr, l, r } });
                    return new LaunchSpecResult("cmd", [], f, "fixture", "fixture", m?["cli"]?.ToString() ?? "");
                };
            }

            var def = new ProviderAdapters.AdapterDefinition(
                NativeModel: mode == "missing-regex" ? null! : ProviderAdapters.AdapterFor("codex")!.NativeModel,
                SourceProtectedResearch: false,
                Capabilities: new JsonObject(),
                Executable: p => { exchange.Call(new JsonObject { ["method"] = "executable", ["args"] = new JsonArray { p?.DeepClone() } }); return null; },
                Availability: p => { exchange.Call(new JsonObject { ["method"] = "availability", ["args"] = new JsonArray { p?.DeepClone() } }); return new JsonObject(); },
                Execute: execute!,
                Environment: (i, pl) => { exchange.Call(new JsonObject { ["method"] = "environment", ["args"] = new JsonArray { i != null ? new JsonObject() : null, pl } }); return new Dictionary<string, string>(); },
                Terminate: pid => { exchange.Call(new JsonObject { ["method"] = "terminate", ["args"] = new JsonArray { pid } }); },
                QuotaAsync: (p, ct) => { exchange.Call(new JsonObject { ["method"] = "quota", ["args"] = new JsonArray { p?.DeepClone() } }); return Task.FromResult(new JsonObject()); },
                DiscoverAsync: (p, ct) =>
                {
                    var rep = exchange.Call(new JsonObject { ["method"] = "discover", ["args"] = new JsonArray { p?.DeepClone() } });
                    return Task.FromResult(rep.AsObject());
                },
                ResultIdentity: m => { exchange.Call(new JsonObject { ["method"] = "resultIdentity", ["args"] = new JsonArray { m?.DeepClone() } }); return new JsonObject(); }
            );

            ProviderAdapters.RegisterAdapter("fixture", def);
            var registeredAdapter = ProviderAdapters.AdapterFor("fixture")!;
            var discoverReply = await registeredAdapter.DiscoverAsync(new JsonObject { ["id"] = "fixture" }, CancellationToken.None);
            return new JsonObject
            {
                ["frozen"] = true,
                ["reply"] = discoverReply?.DeepClone()
            };
        }

        throw new NotSupportedException($"Scenario '{mode}' not supported");
    }

    private sealed class CancellableResponse : HttpResponseMessage
    {
        private readonly Action onDispose;
        public CancellableResponse(System.Net.HttpStatusCode status, Action onDispose) : base(status)
        {
            this.onDispose = onDispose;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsSuccessStatusCode)
            {
                onDispose();
            }
            base.Dispose(disposing);
        }
    }

    private static async Task<JsonNode?> RunApiAgentAsync(JsonObject sc, string root, Exchange exchange, List<JsonNode> events)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "concurrent-isolated-tools")
        {
            var lanes = new[] { "one", "two" };
            var tasks = lanes.Select(async lane =>
            {
                var folder = Path.Combine(root, lane);
                Directory.CreateDirectory(folder);
                var call = new JsonObject
                {
                    ["name"] = "write_file",
                    ["input"] = new JsonObject { ["path"] = "result.txt", ["content"] = lane }
                };
                exchange.Call(new JsonObject
                {
                    ["kind"] = "tool",
                    ["lane"] = lane,
                    ["call"] = (JsonObject)call.DeepClone()
                });
                var deps = new ApiAgent.Dependencies
                {
                    Folder = folder,
                    Http = (_, _) => Task.FromResult(new HttpResponseMessage())
                };
                await ApiAgent.ExecuteToolAsync(call, new JsonObject(), deps, CancellationToken.None);
                var readCall = new JsonObject
                {
                    ["name"] = "read_file",
                    ["input"] = new JsonObject { ["path"] = "result.txt" }
                };
                return await ApiAgent.ExecuteToolAsync(readCall, new JsonObject(), deps, CancellationToken.None);
            });
            var results = await Task.WhenAll(tasks);
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r?.DeepClone());
            return arr;
        }

        if (name == "approved-command-fake-process")
        {
            var call = new JsonObject
            {
                ["name"] = "run_command",
                ["input"] = new JsonObject
                {
                    ["command"] = "fake-tool",
                    ["args"] = new JsonArray { (JsonNode)"space Ω" }
                }
            };
            var deps = new ApiAgent.Dependencies
            {
                Folder = root,
                Http = (_, _) => Task.FromResult(new HttpResponseMessage()),
                RunCommand = (cmd, ct) =>
                {
                    var argsArr = new JsonArray();
                    foreach (var a in cmd.Args) argsArr.Add((JsonNode)a);
                    var req = new JsonObject
                    {
                        ["kind"] = "process",
                        ["command"] = cmd.Executable,
                        ["args"] = argsArr,
                        ["options"] = new JsonObject
                        {
                            ["cwd"] = Normalize(JsonValue.Create(cmd.Cwd), root),
                            ["windowsHide"] = cmd.WindowsHide,
                            ["timeout"] = cmd.TimeoutMs,
                            ["maxBuffer"] = cmd.MaxBuffer,
                            ["encoding"] = "utf8"
                        }
                    };
                    var rep = exchange.Call(req).AsObject();
                    int? code = rep.ContainsKey("error") && rep["error"] is JsonObject eo && eo.ContainsKey("code")
                        ? eo["code"]!.GetValue<int>()
                        : 0;
                    string stdout = rep["stdout"]?.GetValue<string>() ?? "";
                    string stderr = rep["stderr"]?.GetValue<string>() ?? "";
                    return Task.FromResult(new ApiAgent.CommandReply(code, stdout, stderr));
                }
            };
            var job = new JsonObject { ["alwaysApprove"] = true };
            return await ApiAgent.ExecuteToolAsync(call, job, deps, CancellationToken.None);
        }

        if (name.StartsWith("tool-"))
        {
            var mode = sc["input"]!["mode"]!.GetValue<string>();
            if (mode == "read-limit")
            {
                File.WriteAllText(Path.Combine(root, "large.txt"), new string('x', 65537));
            }
            JsonObject call;
            if (mode == "unknown-tool") call = new JsonObject { ["name"] = "unknown", ["input"] = new JsonObject() };
            else if (mode == "command-denied") call = new JsonObject { ["name"] = "run_command", ["input"] = new JsonObject { ["command"] = "never-run", ["args"] = new JsonArray() } };
            else if (mode == "read-limit") call = new JsonObject { ["name"] = "read_file", ["input"] = new JsonObject { ["path"] = "large.txt" } };
            else
            {
                string targetPath = mode == "escape" ? "../escape" : mode == "control-file" ? ".git/config" : "hello.txt";
                call = new JsonObject { ["name"] = "write_file", ["input"] = new JsonObject { ["path"] = targetPath, ["content"] = "Hello Ω" } };
            }

            exchange.Call(new JsonObject { ["kind"] = "tool", ["call"] = (JsonObject)call.DeepClone() });

            var deps = new ApiAgent.Dependencies
            {
                Folder = root,
                Http = (_, _) => Task.FromResult(new HttpResponseMessage())
            };
            var job = new JsonObject { ["lead"] = mode == "lead-write" };
            var result = await ApiAgent.ExecuteToolAsync(call, job, deps, CancellationToken.None);

            if (mode == "write-read")
            {
                var readCall = new JsonObject { ["name"] = "read_file", ["input"] = new JsonObject { ["path"] = "hello.txt" } };
                var text = await ApiAgent.ExecuteToolAsync(readCall, job, deps, CancellationToken.None);
                return new JsonObject
                {
                    ["result"] = result?.DeepClone(),
                    ["text"] = text?.DeepClone()
                };
            }
            return result;
        }

        var protocol = sc["input"]!["protocol"]!.GetValue<string>();
        var agentMode = sc["input"]!["mode"]!.GetValue<string>();

        var agentJob = new JsonObject
        {
            ["model"] = "exact-model",
            ["prompt"] = "Fixture prompt",
            ["advisory"] = agentMode == "advisory",
            ["lead"] = agentMode == "tool-recovery",
            ["config"] = new JsonObject
            {
                ["baseUrl"] = "https://fixture.invalid",
                ["apiProtocol"] = protocol,
                ["maxOutputTokens"] = 32,
                ["maxTurns"] = agentMode == "turn-limit" ? 1 : 3,
                ["requestTimeoutMs"] = 1000
            }
        };

        var agentDeps = new ApiAgent.Dependencies
        {
            Folder = root,
            Emit = ev => events.Add(ev.DeepClone()),
            Http = (req, ct) =>
            {
                var actualReq = new JsonObject
                {
                    ["kind"] = "fetch",
                    ["url"] = req.Url,
                    ["method"] = "POST",
                    ["headers"] = (JsonObject)req.Headers.DeepClone(),
                    ["body"] = (JsonObject)req.Body.DeepClone(),
                    ["redirect"] = req.Redirect,
                    ["timeoutMs"] = req.TimeoutMs
                };
                var rep = exchange.Call(actualReq).AsObject();
                var status = rep["status"]!.GetValue<int>();
                var bodyStr = rep["body"]!.GetValue<string>();
                var resp = new CancellableResponse((System.Net.HttpStatusCode)status, () =>
                {
                    events.Add(new JsonObject { ["cancelledBody"] = true });
                })
                {
                    Content = new StringContent(bodyStr, Encoding.UTF8, "application/json")
                };
                return Task.FromResult<HttpResponseMessage>(resp);
            }
        };

        return await ApiAgent.RunAgentAsync(agentJob, agentDeps, CancellationToken.None);
    }

    private static JsonNode? RunWorktrees(JsonObject sc, string root, Exchange exchange)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "orphan-sweep-retains-live")
        {
            var liveDir = Path.Combine(root, "worktrees", "project", "live");
            var orphanDir = Path.Combine(root, "worktrees", "project", "orphan");
            Directory.CreateDirectory(liveDir);
            Directory.CreateDirectory(orphanDir);
            File.WriteAllText(Path.Combine(orphanDir, "recoverable.txt"), "Disposable fixture");
            var removed = Worktrees.SweepOrphans(root, new[] { "live" });
            var arr = new JsonArray();
            foreach (var r in removed) arr.Add((JsonNode)r);
            return new JsonObject
            {
                ["removed"] = arr,
                ["liveRetained"] = Directory.Exists(liveDir)
            };
        }

        Worktrees.GitRunner runner = call =>
        {
            var req = new JsonObject
            {
                ["kind"] = "process",
                ["command"] = "git",
                ["args"] = new JsonArray(call.Args.Select(a => (JsonNode)a).ToArray()),
                ["cwd"] = Normalize(JsonValue.Create(call.Folder), root)
            };
            if (call.TimeoutMs.HasValue) req["timeout"] = call.TimeoutMs.Value;
            if (call.MaxBytes.HasValue) req["maxBuffer"] = call.MaxBytes.Value;
            var rep = exchange.Call(req).AsObject();
            var stdout = rep["stdout"]?.GetValue<string>() ?? "";
            stdout = stdout.Replace("<ROOT>", root.Replace('\\', '/'));
            var stderr = rep["stderr"]?.GetValue<string>() ?? "";
            stderr = stderr.Replace("<ROOT>", root.Replace('\\', '/'));
            return new Worktrees.GitReply(
                rep["status"]?.GetValue<int>() ?? 0,
                stdout,
                stderr
            );
        };
        var wt = new Worktrees(runner);

        if (name is "ordered-merge" or "dirty-root" or "dirty-part" or "conflict-abort" or "probe-failure" or "merge-failure" or "recovery")
        {
            var first = Path.Combine(root, "part é 1");
            var second = Path.Combine(root, "part 2");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var parts = new List<Worktrees.Part>
            {
                new(first, "work--part-1", "ST-1"),
                new(second, "work--part-2", "ST-2")
            };
            var res = wt.Integrate(root, parts);
            Worktrees.MergeResult? rec = name == "recovery" ? wt.Integrate(root, parts) : null;

            static JsonNode? FormatMerge(Worktrees.MergeResult? r)
            {
                if (r == null) return null;
                var obj = new JsonObject { ["ok"] = r.Ok };
                if (r.Branch != null) obj["branch"] = r.Branch;
                if (!r.Ok)
                {
                    var confArr = new JsonArray();
                    if (r.Conflicts != null) foreach (var c in r.Conflicts) confArr.Add((JsonNode)c);
                    obj["conflicts"] = confArr;
                }
                if (r.Reason != null) obj["reason"] = r.Reason;
                return obj;
            }

            return new JsonObject
            {
                ["result"] = FormatMerge(res),
                ["recovery"] = FormatMerge(rec)
            };
        }

        var projectFolder = Path.Combine(root, "project é");
        Directory.CreateDirectory(Path.Combine(projectFolder, ".git"));
        File.WriteAllText(Path.Combine(projectFolder, ".git", "config"), "");

        var subId = name == "invalid-identity" ? "../bad" : "ST";
        var index = name == "invalid-index" ? 0 : 1;
        var destination = Path.Combine(root, "worktrees", "project", "MT", "ST");
        var branch = "work--part-1";

        if (name is "cleanup-dirty" or "discard")
        {
            Directory.CreateDirectory(destination);
            wt.Cleanup(projectFolder, root, "project", "MT", new[] { new Worktrees.Part(destination, branch, "ST") }, discardBranches: name == "discard");
            return null;
        }

        if (name is "reuse" or "occupied")
        {
            Directory.CreateDirectory(destination);
        }

        var part = wt.Create(projectFolder, root, "project", "MT", subId, index, "work");
        if (part == null) return null;
        return new JsonObject
        {
            ["folder"] = Normalize(JsonValue.Create(part.Folder), root),
            ["branch"] = part.Branch,
            ["subId"] = part.SubId
        };
    }

    private static async Task<JsonNode?> RunPrCheckAsync(JsonObject sc, string root, Exchange exchange)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "concurrent-independent-repairs")
        {
            var turn = 0;
            var turnLock = new object();
            var branches = new[] { "one", "two" };
            var tasks = branches.Select(async branch =>
            {
                var myRemainder = branch == "one" ? 0 : 1;
                PrCommand cmd = async (folder, command, args) =>
                {
                    while (true)
                    {
                        lock (turnLock)
                        {
                            if (turn % 2 == myRemainder) break;
                        }
                        await Task.Yield();
                    }
                    var req = new JsonObject
                    {
                        ["kind"] = "process",
                        ["command"] = command,
                        ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
                        ["cwd"] = Normalize(JsonValue.Create(folder), root)
                    };
                    var rep = exchange.Call(req).AsObject();
                    lock (turnLock)
                    {
                        turn++;
                    }
                    var stdout = rep["stdout"]?.GetValue<string>() ?? "";
                    stdout = stdout.Replace("<ROOT>", root.Replace('\\', '/'));
                    var stderr = rep["stderr"]?.GetValue<string>() ?? "";
                    stderr = stderr.Replace("<ROOT>", root.Replace('\\', '/'));
                    return new PrCommandResult(
                        rep["status"]?.GetValue<int>() ?? 0,
                        stdout,
                        stderr
                    );
                };
                var api = new PrCheck(cmd);
                return await api.PrepareWorktree(root, branch, "main", Array.Empty<string>(), Path.Combine(root, branch));
            });
            var results = await Task.WhenAll(tasks);
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r?.DeepClone());
            return arr;
        }

        PrCommand runner = (folder, command, args) =>
        {
            var req = new JsonObject
            {
                ["kind"] = "process",
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
                ["cwd"] = Normalize(JsonValue.Create(folder), root)
            };
            var rep = exchange.Call(req).AsObject();
            var stdout = rep["stdout"]?.GetValue<string>() ?? "";
            stdout = stdout.Replace("<ROOT>", root.Replace('\\', '/'));
            var stderr = rep["stderr"]?.GetValue<string>() ?? "";
            stderr = stderr.Replace("<ROOT>", root.Replace('\\', '/'));
            return Task.FromResult(new PrCommandResult(
                rep["status"]?.GetValue<int>() ?? 0,
                stdout,
                stderr
            ));
        };
        var prCheck = new PrCheck(runner);

        var worktree = Path.Combine(root, "repair é");
        if (!Directory.Exists(worktree)) Directory.CreateDirectory(worktree);

        if (name.Contains("list"))
        {
            var res = await prCheck.ListPrs(root);
            if (res.TryGetPropertyValue("prs", out var prsNode) && prsNode is JsonArray prsArr)
            {
                foreach (var item in prsArr)
                {
                    if (item is JsonObject prObj && prObj.TryGetPropertyValue("url", out var urlVal) && urlVal == null)
                    {
                        prObj.Remove("url");
                    }
                }
            }
            return res;
        }
        if (name.StartsWith("resolve"))
        {
            return await prCheck.ResolveConflicts(root, 7);
        }
        if (name == "rerun")
        {
            var notRun = new JsonArray
            {
                new JsonObject { ["runId"] = "12" },
                new JsonObject { ["runId"] = "12" },
                new JsonObject { ["runId"] = "13" },
                new JsonObject()
            };
            return await prCheck.RerunNotRun(root, notRun);
        }
        if (name is "finish-noop" or "push-refused" or "push-success" or "markers")
        {
            var fix = new JsonObject
            {
                ["before"] = "before",
                ["branch"] = "feature",
                ["conflicts"] = new JsonArray()
            };
            return await prCheck.FinishFix(root, worktree, fix, 7);
        }

        if (name.StartsWith("prepare-failure-"))
        {
            var failWorktree = Path.Combine(root, "repair");
            return await prCheck.PrepareFix(root, 7, failWorktree);
        }

        return await prCheck.PrepareFix(root, 7, worktree);
    }

    private static async Task<JsonNode?> RunPhoneActivationCheckAsync(JsonObject sc, string root, Exchange exchange, List<JsonNode> events)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "concurrent-isolated-checks")
        {
            var lanes = new[] { "one", "two" };
            var turn = 0;
            var turnLock = new object();
            var tasks = lanes.Select(async lane =>
            {
                var dir = Path.Combine(root, lane);
                Directory.CreateDirectory(dir);
                var myRemainder = lane == "one" ? 0 : 1;
                var report = Path.Combine(dir, "report.md");
                var argv = new[] { "node", "check", "--attempts", dir, "--report", report };

                var options = new PhoneActivationCheckOptions
                {
                    Argv = argv,
                    StdoutWrite = text =>
                    {
                        var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                        lock (events)
                        {
                            events.Add(new JsonObject { ["lane"] = lane, ["stdout"] = norm });
                        }
                    },
                    StderrWrite = text =>
                    {
                        var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                        lock (events)
                        {
                            events.Add(new JsonObject { ["lane"] = lane, ["stderr"] = norm });
                        }
                    },
                    ListInstalledModels = async opts =>
                    {
                        while (true)
                        {
                            lock (turnLock) { if (turn % 2 == myRemainder) break; }
                            await Task.Yield();
                        }
                        var req = new JsonObject
                        {
                            ["lane"] = lane,
                            ["kind"] = "listInstalledModels",
                            ["options"] = (JsonObject)opts!.DeepClone()
                        };
                        var rep = exchange.Call(req).AsObject();
                        lock (turnLock) { turn++; }
                        return rep;
                    },
                    AskOllama = _ => throw new InvalidOperationException("Unexpected inference")
                };

                var res = await PhoneActivationCheck.RunAsync(options);
                return new JsonObject { ["lane"] = lane, ["exitCode"] = res.ExitCode };
            });
            var results = await Task.WhenAll(tasks);
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r.DeepClone());
            return arr;
        }

        var mode = sc["input"]!["mode"]!.GetValue<string>();
        var attempts = Path.Combine(root, "attempts");
        Directory.CreateDirectory(attempts);
        if (mode is not ("blocked-no-token" or "live-blocked"))
        {
            File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "synthetic-token");
        }
        var report = Path.Combine(root, mode == "report-collision" ? "report.txt" : "report.md");
        var argv = new List<string>
        {
            "node", "phone-activation-check.cjs",
            "--attempts", attempts,
            "--report", report,
            "--build", Path.Combine(root, "build/Planwharf.exe")
        };
        if (mode is "live" or "live-blocked")
        {
            argv.Add("--live");
            argv.Add("yes");
        }
        if (mode is "listener" or "bad-listeners")
        {
            var file = Path.Combine(root, "listeners.json");
            var content = mode == "bad-listeners"
                ? "{"
                : new JsonArray
                {
                    new JsonObject
                    {
                        ["path"] = Path.Combine(root, "installed/Planwharf.exe"),
                        ["running"] = true,
                        ["pollsTelegram"] = true,
                        ["pid"] = 17
                    }
                }.ToJsonString();
            File.WriteAllText(file, content);
            argv.Add("--listeners");
            argv.Add(file);
        }
        if (mode == "backup-notebook")
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var starter = Path.Combine(root, "starter.json");
            File.WriteAllText(starter, "{\"schema\":2,\"projects\":[]}\n");
            File.WriteAllText(Path.Combine(source, "plan.json"), "{\"schema\":2,\"projects\":[]}\n");
            argv.AddRange(new[]
            {
                "--from", source,
                "--backup-to", Path.Combine(root, "backup"),
                "--notebook", Path.Combine(root, "isolated"),
                "--starter", starter
            });
        }

        var options = new PhoneActivationCheckOptions
        {
            Argv = argv,
            StdoutWrite = text =>
            {
                var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                events.Add(new JsonObject { ["stdout"] = norm });
            },
            StderrWrite = text =>
            {
                var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                events.Add(new JsonObject { ["stderr"] = norm });
            },
            ListInstalledModels = async opts =>
            {
                var req = new JsonObject
                {
                    ["kind"] = "listInstalledModels",
                    ["options"] = (JsonObject)opts!.DeepClone()
                };
                var rep = exchange.Call(req).AsObject();
                return rep;
            },
            AskOllama = async opts =>
            {
                var req = new JsonObject
                {
                    ["kind"] = "askOllama",
                    ["options"] = (JsonObject)opts!.DeepClone()
                };
                var rep = exchange.Call(req).AsObject();
                return rep;
            },
            FetchImpl = async url =>
            {
                var redUrl = url.Replace("synthetic-token", "<REDACTED>");
                var req = new JsonObject
                {
                    ["kind"] = "fetch",
                    ["url"] = redUrl
                };
                var rep = exchange.Call(req).AsObject();
                var status = rep["ok"]?.GetValue<bool>() == true ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.BadRequest;
                var resp = new HttpResponseMessage(status)
                {
                    Content = new StringContent(rep.ToJsonString(), Encoding.UTF8, "application/json")
                };
                return resp;
            }
        };

        var result = await PhoneActivationCheck.RunAsync(options);
        return new JsonObject { ["exitCode"] = result.ExitCode };
    }

    [Fact]
    public void Mutation_UnexpectedRequest_Fails()
    {
        var exchange = new Exchange(
            new JsonArray { new JsonObject { ["kind"] = "expected" } },
            new JsonArray { new JsonObject { ["ok"] = true } },
            "fake-root");

        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            exchange.Call(new JsonObject { ["kind"] = "unexpected" }));
        Assert.Contains("Request #1 mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_UnconsumedReply_Fails()
    {
        var exchange = new Exchange(
            new JsonArray { new JsonObject { ["kind"] = "call" } },
            new JsonArray { new JsonObject { ["ok"] = true } },
            "fake-root");

        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            exchange.AssertAllConsumed());
        Assert.Contains("Unconsumed replies: used 0 of 1", ex.Message);
    }

    [Fact]
    public void Mutation_ResultMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["result"] = new JsonObject { ["exitCode"] = 0 }
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["exitCode"] = 1 }, null, new JsonObject(), exchange));
        Assert.Contains("Result mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_EffectsMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["result"] = new JsonObject { ["ok"] = true },
            ["effects"] = new JsonObject { ["file.txt"] = "expected content" }
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["ok"] = true }, null, new JsonObject { ["file.txt"] = "mutated content" }, exchange));
        Assert.Contains("Effects mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_EventsMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["result"] = new JsonObject { ["ok"] = true },
            ["events"] = new JsonArray { new JsonObject { ["stdout"] = "expected" } }
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");
        var actualEvents = new List<JsonNode> { new JsonObject { ["stdout"] = "mutated" } };

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["ok"] = true }, null, new JsonObject(), exchange, actualEvents));
        Assert.Contains("Events mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_ErrorMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["error"] = "Expected error message"
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");

        var exWrongErr = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, null, "Completely different error", new JsonObject(), exchange));
        Assert.Contains("Sub-string not found", exWrongErr.Message);

        var exNoErr = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["ok"] = true }, null, new JsonObject(), exchange));
        Assert.Contains("Expected error 'Expected error message' but got result:", exNoErr.Message);
    }
}
