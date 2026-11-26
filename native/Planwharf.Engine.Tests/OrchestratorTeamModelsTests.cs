using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class OrchestratorTeamModelsTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    private static JsonArray Arr(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void UsageClampsExhaustionAndNormalizesInvalidValues()
    {
        var usage = OrchestratorTeamModels.CalculateUsage(Obj("""{"used":120,"limit":100}"""));
        Assert.Equal(0, usage.Remaining);
        Assert.Equal(100, usage.Pct);
        Assert.Equal(0, usage.LeftPct);
        Assert.Equal(0, OrchestratorTeamModels.CalculateUsage(Obj("""{"used":-2,"limit":0}""")).Used);
    }

    [Fact]
    public void OrchestratorRequiresToolsFilesystemAndEnabledRole()
    {
        var model = Obj("""{"enabled":true,"kind":"default"}""");
        Assert.True(OrchestratorTeamModels.CanOrchestrate(model));
        model["capabilities"] = Obj("""{"canUseTools":false}""");
        Assert.False(OrchestratorTeamModels.CanOrchestrate(model));
        model["capabilities"] = Obj("""{"canInspectFilesystem":false}""");
        Assert.False(OrchestratorTeamModels.CanOrchestrate(model));
        model["capabilities"] = new JsonObject();
        model["roles"] = Obj("""{"orchestrator":false}""");
        Assert.False(OrchestratorTeamModels.CanOrchestrate(model));
    }
    [Fact]
    public void RolesEnforceSupervisorTransportAndInheritVerifierFromWorker()
    {
        var model = Obj("""{"provider":"codex","roles":{"worker":false}}""");
        var roles = ModelRoles.WithModelRoles(model)["roles"]!;
        Assert.False(roles["verifier"]!.GetValue<bool>());
        Assert.False(ModelRoles.RoleEligibility(model)["supervisor"]);
        model["provider"] = "api";
        Assert.True(ModelRoles.RoleEligibility(model)["supervisor"]);
        model["capabilities"] = Obj("""{"canUseTools":false}""");
        Assert.False(ModelRoles.RoleEligibility(model)["reviewer"]);
        Assert.True(ModelRoles.RoleEligibility(model)["worker"]);
    }

    [Fact]
    public void RoutingRejectsMissingOrMismatchedProviderAndCli()
    {
        Assert.NotNull(ModelRouting.ModelRouteError(Obj("""{"cli":"gpt-6"}""")));
        Assert.NotNull(ModelRouting.ModelRouteError(Obj("""{"provider":"codex"}""")));
        Assert.NotNull(ModelRouting.ModelRouteError(Obj("""{"provider":"codex","cli":"claude-opus"}""")));
        Assert.Null(ModelRouting.ModelRouteError(Obj("""{"provider":"codex","cli":"gpt-6"}""")));
        var result = OrchestratorTeamModels.NormalizeModels(Arr("""[{"id":"bad","provider":"codex","cli":"claude-opus","enabled":true}]"""));
        Assert.True(result[0]!["neverUsable"]!.GetValue<bool>());
        Assert.False(result[0]!["available"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("normal", 0, true)]
    [InlineData("safe", 0, false)]
    [InlineData("safe", 1, true)]
    [InlineData("ultra-safe", 1, false)]
    [InlineData("ultra-safe", 2, true)]
    public void SavingModesFilterByTier(string mode, int tier, bool allowed)
    {
        Assert.Equal(allowed, OrchestratorTeamModels.AllowedBySavingMode(mode, new JsonObject { ["leadTier"] = tier }));
    }

    [Fact]
    public void TeamWeightOverridesTierForSavingMode()
    {
        var model = Obj("""{"leadTier":0,"weight":"light","weightSource":"team"}""");
        Assert.True(OrchestratorTeamModels.AllowedBySavingMode("ultra-safe", model));
        model["weightSource"] = "lineup";
        Assert.False(OrchestratorTeamModels.AllowedBySavingMode("ultra-safe", model));
    }

    [Fact]
    public void LegacyPolicyAndWindowLimitsAreValidated()
    {
        var migrated = ModelPolicy.MigratePolicy(Obj("""{"maxUsedPercent":80,"warningUsedPercent":60}"""));
        Assert.Equal(20, migrated["minRemainingPercent"]!.GetValue<int>());
        Assert.Equal(40, migrated["warningRemainingPercent"]!.GetValue<int>());
        Assert.Throws<InvalidOperationException>(() => ModelPolicy.MigratePolicy(Obj("""{"maxUsedPercent":80,"minRemainingPercent":20}""")));
        var limit = ModelPolicy.QuotaLimit(Obj("""{"fiveHourPercent":10,"weeklyPercent":25}"""));
        Assert.Equal(10, limit["fiveHourPercent"]!.GetValue<int>());
        Assert.Equal(25, limit["weeklyPercent"]!.GetValue<int>());
        Assert.Equal("weekly", ModelPolicy.WindowKind(JsonValue.Create("seven-day")));
    }

    [Fact]
    public void GoldAssignmentRequiresExactProviderAndCli()
    {
        var model = Obj("""{"id":"m","provider":"codex","cli":"gpt-6"}""");
        var job = Obj("""{"goldModel":{"id":"m","provider":"codex","cli":"gpt-6"}}""");
        Assert.True(ModelRoles.PermitsAssignment(model, job));
        job["goldModel"]!["cli"] = "gpt-6-mini";
        Assert.False(ModelRoles.PermitsAssignment(model, job));
        job["goldModel"]!["cli"] = "gpt-6";
        job["goldModel"]!["provider"] = "other";
        Assert.False(ModelRoles.PermitsAssignment(model, job));
    }

    private static TeamRosterContext Context(JsonNode models, JsonObject? store = null) => new()
    {
        Models = models, Store = store ?? new JsonObject(), ProviderDefinitions = new JsonArray(),
        PlansLive = new JsonArray(), Now = () => "2026-01-02T12:00:00Z"
    };

    [Fact]
    public void CreditLockExpiresAfterTwentyFourHours()
    {
        var context = Context(Arr("""[{"id":"m","enabled":true}]"""), Obj("""{"creditsNeeded":{"m":{"at":"2026-01-01T12:00:01Z"}}}"""));
        var locked = OrchestratorTeamModels.BuildRoster(context)[0]!;
        Assert.True(locked["creditsNeeded"]!.GetValue<bool>());
        Assert.False(locked["available"]!.GetValue<bool>());
        context.Store["creditsNeeded"]!["m"]!["at"] = "2026-01-01T12:00:00Z";
        Assert.True(OrchestratorTeamModels.BuildRoster(context)[0]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public void ProjectRolesDoNotChangeGlobalRoles()
    {
        var context = Context(Arr("""[{"id":"m","enabled":true}]"""));
        Assert.True(OrchestratorTeamModels.SetModelRoles(context, "m", Obj("""{"worker":false}"""), "p1")["ok"]!.GetValue<bool>());
        Assert.True(OrchestratorTeamModels.BuildRoster(context)[0]!["roles"]!["worker"]!.GetValue<bool>());
        var project = context with { CurrentProjectId = () => "p1" };
        Assert.False(OrchestratorTeamModels.BuildRoster(project)[0]!["roles"]!["worker"]!.GetValue<bool>());
    }

    [Fact]
    public void LeadRestrictionCannotRemoveCurrentLead()
    {
        var context = Context(Arr("""[{"id":"m","enabled":true}]"""), Obj("""{"orchestratorId":"m"}"""));
        var result = OrchestratorTeamModels.SetLeadAllowed(context, "m", false);
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Contains("lead", result["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task CrewChangeRejectsWrongProjectAndMissingBridge()
    {
        var context = Context(new JsonArray());
        var action = Obj("""{"type":"reassign_crew","projectId":"p","assignments":{}}""");
        var wrong = await OrchestratorTeamModels.ReassignCrew(context, action, Obj("""{"id":"other"}"""));
        Assert.False(wrong["ok"]!.GetValue<bool>());
        Assert.Contains("different project", wrong["reason"]!.GetValue<string>());
        var unavailable = await OrchestratorTeamModels.ReassignCrew(context, action, Obj("""{"id":"p","megaTaskId":"t"}"""));
        Assert.Contains("bridge", unavailable["reason"]!.GetValue<string>());
    }
    [Fact]
    public void LeadRankingPrefersTierThenAvailableQuota()
    {
        var models = Arr("""[{"id":"light","label":"Haiku","plan":"L"},{"id":"low","label":"Opus","plan":"A"},{"id":"high","label":"Fable","plan":"B"}]""");
        var plans = Arr("""[{"id":"A","readable":true,"leftPct":20},{"id":"B","readable":true,"leftPct":80}]""");
        var ranked = OrchestratorTeamModels.LeadRanking(models, plans);
        Assert.Equal("high", ranked[0]!["id"]!.GetValue<string>());
        Assert.Equal("low", ranked[1]!["id"]!.GetValue<string>());
        Assert.Equal("light", ranked[2]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void LeadHandoverSkipsRestrictedCandidates()
    {
        var context = Context(Arr("""[{"id":"a","label":"Opus","enabled":true},{"id":"b","label":"Sonnet","enabled":true},{"id":"c","label":"Haiku","enabled":true}]"""),
            Obj("""{"orchestratorId":"a","notLead":["b"]}""")) with { IsLow = model => model["id"]!.GetValue<string>() == "a" };
        Assert.Equal("c", OrchestratorTeamModels.SelectLead(context)!["id"]!.GetValue<string>());
        Assert.Equal("c", context.Store["orchestratorId"]!.GetValue<string>());
    }

    [Fact]
    public void RefusalProducesReadableReasonAndExpires()
    {
        var context = Context(Arr("""[{"id":"m","enabled":true}]"""), Obj("""{"refusals":{"m":{"until":"2026-01-02T13:00:00Z","reason":"QUOTA_ERROR: limit reached"}}}"""));
        var item = OrchestratorTeamModels.BuildRoster(context)[0]!;
        Assert.False(item["available"]!.GetValue<bool>());
        Assert.Contains("Quota used up (limit reached)", item["unavailableReason"]!.GetValue<string>());
        context.Store["refusals"]!["m"]!["until"] = "2026-01-02T12:00:00Z";
        Assert.True(OrchestratorTeamModels.BuildRoster(context)[0]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public void WorkerSelectionSkipsExhaustedModels()
    {
        var models = Arr("""[{"id":"a","enabled":true,"cost":1,"limit":10},{"id":"b","enabled":true,"cost":1,"limit":10}]""");
        var options = Obj("""{"usage":{"a":{"used":10}}}""");
        Assert.Equal("b", OrchestratorTeamModels.PickWorker(models, options)!["id"]!.GetValue<string>());
        options["usage"]!["b"] = Obj("""{"used":10}""");
        Assert.Null(OrchestratorTeamModels.PickWorker(models, options));
    }
    [Fact]
    public void NewAccountDoesNotInheritPreviousAccountRoles()
    {
        var context = Context(Arr("""[{"id":"m","provider":"codex","cli":"gpt-6","enabled":true}]""")) with
        {
            ProviderDefinitions = Arr("""[{"id":"codex","enabled":true,"account":{"key":"first"}}]""")
        };
        Assert.True(OrchestratorTeamModels.BuildRoster(context)[0]!["roles"]!["worker"]!.GetValue<bool>());
        context.ProviderDefinitions![0]!["account"]!["key"] = "second";
        var next = OrchestratorTeamModels.BuildRoster(context)[0]!;
        Assert.Equal("codex:second", next["accountScope"]!.GetValue<string>());
        Assert.False(next["roles"]!["worker"]!.GetValue<bool>());
        context.ProviderDefinitions[0]!["account"]!["key"] = "first";
        Assert.True(OrchestratorTeamModels.BuildRoster(context)[0]!["roles"]!["worker"]!.GetValue<bool>());
    }

    [Fact]
    public void PolicyFallbackUsesConfiguredUsableModel()
    {
        var models = Arr("""[{"id":"a","provider":"codex","cli":"gpt-6","plan":"A","enabled":true,"policy":{"minRemainingPercent":20,"fallbackModelId":"b"}},{"id":"b","provider":"grok","cli":"grok-4","plan":"B","enabled":true}]""");
        var plans = Arr("""[{"id":"A","readable":true,"leftPct":20},{"id":"B","readable":true,"leftPct":90}]""");
        Assert.Equal("b", ModelPolicy.ResolveUsageFallback(models[0]!.AsObject(), models, plans, _ => true)!["id"]!.GetValue<string>());
        Assert.Null(ModelPolicy.ResolveUsageFallback(models[0]!.AsObject(), models, plans, _ => false));
    }
    [Fact]
    public async Task CrewReassignmentUpdatesRemainingProposalAndRequestsApproval()
    {
        var row = new JsonObject();
        var models = new List<JsonObject> { Obj("""{"id":"a","provider":"codex","cli":"gpt-6","enabled":true}"""), Obj("""{"id":"b","provider":"grok","cli":"grok-4","enabled":true}""") };
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = new JsonObject(), Attempts = AttemptEngine.Create(new AttemptIo()), Directory = "synthetic",
            RowFor = (_, _) => row, Inspect = _ => Obj("""{"git":false,"dirty":[]}"""),
            Models = () => models, Now = () => "2026-01-02T12:00:00Z", LeadId = () => "lead",
            Persist = () => { }, NewId = () => "stable", Files = new MemoryFiles()
        });
        var mega = bridge.Propose(Obj("""{"project":{"id":"p","folder":"synthetic"},"revisions":[{"id":1,"direction":"Build"}]}"""),
            Obj("""{"id":"job","title":"Build","detail":"Implement","revision_id":1,"kind":"implementation"}"""),
            [Obj("""{"slice":"Build","modelId":"a","scope":["engine"]}""")]);
        var step = bridge.Data.SubTasks[0]!;
        var action = new JsonObject { ["type"] = "reassign_crew", ["projectId"] = "p", ["assignments"] = new JsonObject { [step["id"]!.GetValue<string>()] = "b" } };
        var context = Context(new JsonArray()) with { ExecutionBridge = bridge };
        var result = await OrchestratorTeamModels.ReassignCrew(context, action, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Contains("Approve", result["text"]!.GetValue<string>());
        Assert.Equal("b", step["assignedModelId"]!.GetValue<string>());
    }

    private static (ExecutionBridge bridge, JsonObject mega, JsonObject row, List<int> killed, MemoryFiles files, HashSet<int> live) CreateBridgeFixture(string status = "PROPOSED")
    {
        var row = new JsonObject();
        var models = new List<JsonObject>
        {
            Obj("""{"id":"a","label":"Terra","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"roles":{"worker":true}}"""),
            Obj("""{"id":"b","label":"Sonnet","provider":"claude","cli":"claude-3-5-sonnet","enabled":true,"available":true,"roles":{"worker":true}}"""),
            Obj("""{"id":"no-worker","label":"NoWorker","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"roles":{"worker":false}}""")
        };
        var files = new MemoryFiles();
        var killed = new List<int>();
        var live = new HashSet<int>();
        var bridge = Execution.CreateExecutionBridge(new ExecutionOptions
        {
            Store = new JsonObject(),
            Attempts = AttemptEngine.Create(new AttemptIo()),
            Directory = "synthetic",
            RowFor = (_, _) => row,
            Inspect = _ => Obj("""{"git":false,"dirty":[]}"""),
            Models = () => models,
            Now = () => "2026-01-02T12:00:00Z",
            LeadId = () => "lead",
            Persist = () => { },
            NewId = () => "stable",
            Files = files,
            WorkerEligible = (m, _) => m["roles"]?["worker"]?.GetValue<bool>() ?? true,
            KillWorker = pid => { killed.Add(pid); live.Remove(pid); return true; },
            IsProcessAlive = pid => live.Contains(pid)
        });
        var mega = bridge.Propose(
            Obj("""{"project":{"id":"p","folder":"synthetic"},"revisions":[{"id":1,"direction":"Build"}]}"""),
            Obj("""{"id":"job","title":"Build","detail":"Implement","revision_id":1,"kind":"implementation"}"""),
            [Obj("""{"slice":"Build","modelId":"a","scope":["engine"]}""")]);
        if (status != "READY_FOR_APPROVAL" && status != "PROPOSED")
        {
            mega["status"] = status;
        }
        return (bridge, mega, row, killed, files, live);
    }

    [Fact]
    public async Task CrewReassignmentRunningInvokesTickAndRecordsTakeover()
    {
        var (bridge, mega, row, killed, files, live) = CreateBridgeFixture("RUNNING");
        var step = bridge.Data.SubTasks[0]!;
        step["status"] = "RUNNING";
        step["attemptIds"] = Arr("""["att-1"]""");
        live.Add(4242);
        files.Write("worker.log", System.Text.Encoding.UTF8.GetBytes("header\nrunning tests\n"));
        row["dispatch"] = Arr("""[{"subTaskId":"stable-2","megaTaskId":"stable-1","pid":4242,"alive":true,"attemptId":"att-1","structuredResultPath":"none.json","logPath":"worker.log"}]""");
        row["dispatch"]![0]!["subTaskId"] = step["id"]!.DeepClone();
        row["dispatch"]![0]!["megaTaskId"] = mega["id"]!.DeepClone();

        var ticked = false;
        var context = Context(new JsonArray()) with
        {
            ExecutionBridge = bridge,
            Tick = () => { ticked = true; return Task.FromResult(new JsonObject()); }
        };
        var action = new JsonObject
        {
            ["type"] = "reassign_crew",
            ["projectId"] = "p",
            ["assignments"] = new JsonObject { [step["id"]!.GetValue<string>()] = "b" }
        };
        var result = await OrchestratorTeamModels.ReassignCrew(context, action, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal("Crew changed; the remaining steps start with the new models.", result["text"]!.GetValue<string>());
        Assert.True(ticked);
        Assert.Contains(4242, killed);
        Assert.Equal("b", step["assignedModelId"]!.GetValue<string>());
        Assert.Equal("PENDING", step["status"]!.GetValue<string>());
        Assert.Equal("a", step["takeover"]?["fromModelId"]?.GetValue<string>());
        Assert.Contains("running tests", step["takeover"]?["logTail"]?.GetValue<string>());
    }

    [Fact]
    public async Task CrewReassignmentStoppedHandlesAllResumeVariations()
    {
        var (bridge, mega, _, _, _, _) = CreateBridgeFixture("STOPPED");
        var step = bridge.Data.SubTasks[0]!;
        var action = new JsonObject
        {
            ["type"] = "reassign_crew",
            ["projectId"] = "p",
            ["assignments"] = new JsonObject { [step["id"]!.GetValue<string>()] = "b" }
        };

        // 1. Resume ok
        var context = Context(new JsonArray()) with
        {
            ExecutionBridge = bridge,
            Resume = (_, _) => Task.FromResult(new JsonObject { ["ok"] = true })
        };
        var result1 = await OrchestratorTeamModels.ReassignCrew(context, action, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.True(result1["ok"]!.GetValue<bool>());
        Assert.Equal("Crew changed and the work resumed.", result1["text"]!.GetValue<string>());

        // 2. Requires confirmation
        context = context with
        {
            Resume = (_, _) => Task.FromResult(new JsonObject { ["ok"] = false, ["requiresConfirmation"] = true })
        };
        var result2 = await OrchestratorTeamModels.ReassignCrew(context, action, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.True(result2["ok"]!.GetValue<bool>());
        Assert.Equal("Crew changed. Press Resume to continue and confirm the changes made since the checkpoint.", result2["text"]!.GetValue<string>());

        // 3. Resume reason
        context = context with
        {
            Resume = (_, _) => Task.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "Repo changed" })
        };
        var result3 = await OrchestratorTeamModels.ReassignCrew(context, action, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.True(result3["ok"]!.GetValue<bool>());
        Assert.Equal("Crew changed. Press Resume to continue: Repo changed", result3["text"]!.GetValue<string>());

        // 4. Null resume
        context = context with { Resume = null };
        var result4 = await OrchestratorTeamModels.ReassignCrew(context, action, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.True(result4["ok"]!.GetValue<bool>());
        Assert.Equal("Crew changed. Press Resume to continue.", result4["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChangeCrewUsesUserAttributionAndRejectsIneligibleRole()
    {
        var (bridge, mega, _, _, _, _) = CreateBridgeFixture();
        var step = bridge.Data.SubTasks[0]!;
        var projectId = "p";
        var store = new JsonObject
        {
            ["projects"] = new JsonObject
            {
                [projectId] = new JsonObject
                {
                    ["id"] = projectId,
                    ["megaTaskId"] = mega["id"]!.DeepClone()
                }
            }
        };
        var context = Context(new JsonArray(), store) with
        {
            ExecutionBridge = bridge
        };

        // User attribution through ChangeCrew
        var okResult = await OrchestratorTeamModels.ChangeCrew(context, projectId, new JsonObject { [step["id"]!.GetValue<string>()] = "b" });
        Assert.True(okResult["ok"]!.GetValue<bool>());
        var events = bridge.Data.Timeline;
        Assert.Contains(events, e => e?["summary"]?.GetValue<string>().StartsWith("You assigned") == true);

        // Model lacking worker role rejected
        var failResult = await OrchestratorTeamModels.ChangeCrew(context, projectId, new JsonObject { [step["id"]!.GetValue<string>()] = "no-worker" });
        Assert.False(failResult["ok"]!.GetValue<bool>());
        Assert.Contains("cannot take", failResult["reason"]!.GetValue<string>());
    }

    [Fact]
    public void SetGoldModelWorkerRequirementAndClearing()
    {
        var context = Context(Arr("""[{"id":"worker","enabled":true,"capabilities":{"workerEligible":true}},{"id":"noworker","enabled":true,"capabilities":{"workerEligible":false}}]"""));
        var fail = OrchestratorTeamModels.SetGoldModel(context, "noworker");
        Assert.False(fail["ok"]!.GetValue<bool>());
        Assert.Contains("Worker role", fail["reason"]!.GetValue<string>());

        var missing = OrchestratorTeamModels.SetGoldModel(context, "unknown");
        Assert.False(missing["ok"]!.GetValue<bool>());

        var ok = OrchestratorTeamModels.SetGoldModel(context, "worker");
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("worker", context.Store["goldModelId"]!.GetValue<string>());

        var clear = OrchestratorTeamModels.SetGoldModel(context, null);
        Assert.True(clear["ok"]!.GetValue<bool>());
        Assert.Null(context.Store["goldModelId"]);
    }

    [Fact]
    public void SetOrchestratorValidatesPresenceEligibilityAndClearing()
    {
        var context = Context(Arr("""[{"id":"good","enabled":true,"capabilities":{"canUseTools":true,"canInspectFilesystem":true}},{"id":"bad","enabled":true,"capabilities":{"canUseTools":false}}]"""));
        var unknown = OrchestratorTeamModels.SetOrchestrator(context, "unknown");
        Assert.False(unknown["ok"]!.GetValue<bool>());
        Assert.Contains("not present", unknown["reason"]!.GetValue<string>());

        var ineligible = OrchestratorTeamModels.SetOrchestrator(context, "bad");
        Assert.False(ineligible["ok"]!.GetValue<bool>());
        Assert.Contains("not eligible", ineligible["reason"]!.GetValue<string>());

        var ok = OrchestratorTeamModels.SetOrchestrator(context, "good");
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("good", context.Store["orchestratorId"]!.GetValue<string>());

        var clear = OrchestratorTeamModels.SetOrchestrator(context, null);
        Assert.True(clear["ok"]!.GetValue<bool>());
        Assert.Null(context.Store["orchestratorId"]);
    }

    [Fact]
    public void BulkRoleSettingsEnforceValidationAndCapabilities()
    {
        var context = Context(Arr("""[{"id":"m1","enabled":true,"provider":"codex"},{"id":"m2","enabled":true,"provider":"api"}]"""));
        var badRole = OrchestratorTeamModels.SetRoleForAll(context, "invalid-role", true);
        Assert.False(badRole["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid bulk role settings", badRole["reason"]!.GetValue<string>());

        var okRole = OrchestratorTeamModels.SetRoleForAll(context, "worker", false);
        Assert.True(okRole["ok"]!.GetValue<bool>());

        var emptyUpdates = OrchestratorTeamModels.SetRolesForMany(context, new JsonArray());
        Assert.False(emptyUpdates["ok"]!.GetValue<bool>());
        Assert.Contains("Choose at least one model", emptyUpdates["reason"]!.GetValue<string>());

        var badUpdates = OrchestratorTeamModels.SetRolesForMany(context, Arr("""[{"id":"m1","roles":{"bad":true}}]"""));
        Assert.False(badUpdates["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid role settings", badUpdates["reason"]!.GetValue<string>());

        var okUpdates = OrchestratorTeamModels.SetRolesForMany(context, Arr("""[{"id":"m1","roles":{"worker":true}},{"id":"m2","roles":{"supervisor":true}}]"""));
        Assert.True(okUpdates["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void ResearchModelEnforcesConstraintsAndProviderAdapter()
    {
        var context = Context(Arr("""[{"id":"good","provider":"api","enabled":true,"available":true,"cost":1,"remaining":10,"capabilities":{"canUseTools":true,"canInspectFilesystem":true}},{"id":"no-worker","provider":"api","enabled":true,"available":true,"roles":{"worker":false}},{"id":"disabled","provider":"api","enabled":false,"roles":{"worker":true}}]"""));
        var missingEx = Assert.Throws<InvalidOperationException>(() => OrchestratorTeamModels.ResearchModel(context, "unknown"));
        Assert.Contains("not in the roster", missingEx.Message);

        var noWorkerEx = Assert.Throws<InvalidOperationException>(() => OrchestratorTeamModels.ResearchModel(context, "no-worker"));
        Assert.Contains("Worker role", noWorkerEx.Message);

        var disabledEx = Assert.Throws<InvalidOperationException>(() => OrchestratorTeamModels.ResearchModel(context, "disabled"));
        Assert.Contains("disabled or unavailable", disabledEx.Message);
    }

    [Fact]
    public void DispatchPlanAndJobKindFormatExecutionSlices()
    {
        Assert.Equal("build", OrchestratorTeamModels.JobKind(Obj("""{"title":"Implement feature"}""")));
        Assert.Equal("hard", OrchestratorTeamModels.JobKind(Obj("""{"title":"Complex task"}""")));
        Assert.Equal("inspect", OrchestratorTeamModels.JobKind(Obj("""{"title":"Audit architecture"}""")));
        Assert.Equal(2, OrchestratorTeamModels.SliceCount(Obj("""{"title":"Implement"}"""), null, 2));
        Assert.Equal(1, OrchestratorTeamModels.SliceCount(Obj("""{"title":"Audit"}"""), null, 2));

        var singleWorker = Arr("""[{"id":"w1","label":"Worker 1"}]""");
        var singlePlan = OrchestratorTeamModels.DispatchPlan(singleWorker, Obj("""{"title":"Implement"}"""));
        Assert.Single(singlePlan);
        Assert.Equal("Whole job", singlePlan[0]!["slice"]!.GetValue<string>());

        var multiWorkers = Arr("""[{"id":"w1","label":"Worker 1"},{"id":"w2","label":"Worker 2"}]""");
        var dualPlanClean = OrchestratorTeamModels.DispatchPlan(multiWorkers, Obj("""{"title":"Implement"}"""));
        Assert.Equal(2, dualPlanClean.Count);
        Assert.Contains("Implement the whole job", dualPlanClean[0]!["slice"]!.GetValue<string>());
        Assert.Contains(ModelRoles.VerifySlice, dualPlanClean[1]!["slice"]!.GetValue<string>());

        var dirtyFacts = Obj("""{"dirty":["file.cs"]}""");
        var dualPlanDirty = OrchestratorTeamModels.DispatchPlan(multiWorkers, Obj("""{"title":"Implement"}"""), dirtyFacts);
        Assert.Contains("starting from the uncommitted changes", dualPlanDirty[0]!["slice"]!.GetValue<string>());

        var reviewPlan = OrchestratorTeamModels.DispatchPlan(multiWorkers, Obj("""{"kind":"review"}"""));
        Assert.Contains("Review", reviewPlan[0]!["slice"]!.GetValue<string>());
    }

    [Fact]
    public void FleetOfBuildsAssignmentsAndDerivesState()
    {
        var models = Arr("""[{"id":"w1","enabled":true,"available":true},{"id":"w2","enabled":true,"available":false}]""");
        var projects = Arr("""[{"id":"p1","name":"Project 1","status":"watching","dispatch":[{"modelId":"w1","slice":"S1","alive":true,"pid":100}]}]""");
        var fleet = OrchestratorTeamModels.FleetOf(models, projects, "w1");
        Assert.Equal(2, fleet.Count);
        Assert.Equal("working", fleet[0]!["state"]!.GetValue<string>());
        Assert.True(fleet[0]!["lead"]!.GetValue<bool>());
        Assert.Equal("unavailable", fleet[1]!["state"]!.GetValue<string>());
    }

    private sealed class AttemptIo : IAttemptIo
    {
        private JsonObject data = new();
        public T WithLock<T>(Func<T> work) => work();
        public JsonObject Read() => data;
        public void Write(JsonObject store) => data = store;
    }
    private sealed class MemoryFiles : IExecutionFiles
    {
        private readonly Dictionary<string, byte[]> data = new();
        public byte[] Read(string path) => data[path];
        public void Write(string path, byte[] bytes) => data[path] = bytes;
        public void Append(string path, byte[] bytes) => Write(path, data.GetValueOrDefault(path, []).Concat(bytes).ToArray());
        public void CreateDirectory(string path) { }
        public string? LinkedWorkspace(string? path) => path;
        public string RealDirectory(string path) => path;
        public ExecutionFileStat Stat(string path) => new(0, true, false);
    }
}
