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

    [Fact]
    public void PickWorkersEnforcesProviderDiversityAndUsageAccumulation()
    {
        var models = Arr("""
        [
            {"id":"w1","label":"Worker 1","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"cost":5,"limit":8},
            {"id":"w2","label":"Worker 2","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"cost":2,"limit":10},
            {"id":"w3","label":"Worker 3","provider":"grok","cli":"grok-4","enabled":true,"available":true,"cost":2,"limit":10}
        ]
        """);
        var options = Obj("""{"job":{"title":"Build parser","detail":"Implement parser"},"usage":{}}""");

        // Primary worker should be w1 (first in pool).
        // Verifier slice should choose w3 (different provider: grok) over w2 (same provider: codex).
        var workers = OrchestratorTeamModels.PickWorkers(models, options);
        Assert.Equal(2, workers.Count);
        Assert.Equal("w1", workers[0]!["id"]!.GetValue<string>());
        Assert.Equal("w3", workers[1]!["id"]!.GetValue<string>());

        // If usage for w1 is 5, w1 (limit 8, cost 5) exceeds limit, so w2 is picked instead.
        var highUsageOptions = Obj("""{"job":{"title":"Build parser","detail":"Implement parser"},"usage":{"w1":{"used":5}}}""");
        var singleWorker = OrchestratorTeamModels.PickWorker(models, highUsageOptions);
        Assert.NotNull(singleWorker);
        Assert.Equal("w2", singleWorker!["id"]!.GetValue<string>());
    }

    [Fact]
    public void PickWorkersVerifierRecoversWhenAllVerifiersAreLowQuota()
    {
        var models = Arr("""
        [
            {"id":"w1","label":"Builder","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"cost":1,"limit":10},
            {"id":"v1","label":"Verifier Low","provider":"grok","cli":"grok-4","enabled":true,"available":true,"cost":1,"limit":10}
        ]
        """);
        var options = Obj("""{"job":{"title":"Build parser","detail":"Implement parser"}}""");

        // Predicate marks only v1 as low quota.
        Func<JsonObject, bool> isLow = m => m["id"]!.GetValue<string>() == "v1";

        // Since v1 is the only available verifier, PickWorkers should fall back to v1 for verification
        var workers = OrchestratorTeamModels.PickWorkers(models, options, isLow);
        Assert.Equal(2, workers.Count);
        Assert.Equal("w1", workers[0]!["id"]!.GetValue<string>());
        Assert.Equal("v1", workers[1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void CalculateUsageAndUsageOfHandleNullAndZeroLimits()
    {
        var nullUsage = OrchestratorTeamModels.CalculateUsage(null);
        Assert.Equal(0, nullUsage.Used);
        Assert.Equal(0, nullUsage.Limit);
        Assert.Equal(0, nullUsage.Remaining);
        Assert.Equal(0, nullUsage.Pct);
        Assert.Equal(100, nullUsage.LeftPct);

        var nullObj = OrchestratorTeamModels.UsageOf(null);
        Assert.Equal(0, nullObj["used"]!.GetValue<double>());
        Assert.Equal(100, nullObj["leftPct"]!.GetValue<double>());

        var zeroLimit = OrchestratorTeamModels.CalculateUsage(Obj("""{"used":10,"limit":0}"""));
        Assert.Equal(0, zeroLimit.Limit);
        Assert.Equal(100, zeroLimit.LeftPct);
        Assert.Equal(0, zeroLimit.Pct);
    }

    [Fact]
    public void LeadRankingAndQualityTieBreakingAndClaudeFamilies()
    {
        // Claude families order: fable (0) < opus (1) < sonnet (2) < haiku (3)
        var models = Arr("""
        [
            {"id":"h","label":"Claude 3.5 Haiku","kind":"default"},
            {"id":"s","label":"Claude 3.5 Sonnet","kind":"default"},
            {"id":"o","label":"Claude Opus 4","kind":"premium"},
            {"id":"f","label":"Claude Fable 1","kind":"premium"}
        ]
        """);
        var quality = OrchestratorTeamModels.LeadQuality(models);
        Assert.Equal("f", quality[0]!["id"]!.GetValue<string>());
        Assert.Equal("o", quality[1]!["id"]!.GetValue<string>());
        Assert.Equal("s", quality[2]!["id"]!.GetValue<string>());
        Assert.Equal("h", quality[3]!["id"]!.GetValue<string>());

        // Null and empty checks
        Assert.Empty(OrchestratorTeamModels.LeadQuality(null));
        Assert.Empty(OrchestratorTeamModels.LeadRanking(null));
        Assert.Empty(OrchestratorTeamModels.LeadQuality(new JsonArray()));
    }

    [Fact]
    public void CanOrchestrateLocalModelGatingAndProviderDisable()
    {
        var localModel = Obj("""{"kind":"local","enabled":true,"available":true}""");
        // Local model cannot orchestrate unless orchestratorEligible is explicitly set
        Assert.False(OrchestratorTeamModels.CanOrchestrate(localModel));

        localModel["orchestratorEligible"] = true;
        Assert.True(OrchestratorTeamModels.CanOrchestrate(localModel));

        // Provider disabled disables orchestration
        localModel["providerEnabled"] = false;
        Assert.False(OrchestratorTeamModels.CanOrchestrate(localModel));
    }

    [Fact]
    public void SelectLeadCrossProviderFallbackOnActiveLeadRefusal()
    {
        // Setup: Active lead 'lead1' from provider 'codex' is refused.
        // Another model 'peer' from same provider 'codex' is also low quota, so LeadRanking finds no eligible candidate.
        // A model 'alt' from provider 'grok' is eligible.
        // SelectLead should fall back across providers to 'alt' and record fallbackFrom: 'lead1'.
        var models = Arr("""
        [
            {"id":"lead1","label":"Lead 1","provider":"codex","cli":"gpt-6","enabled":true,"available":true},
            {"id":"peer","label":"Peer Codex","provider":"codex","cli":"gpt-6","enabled":true,"available":true},
            {"id":"alt","label":"Alt Grok","provider":"grok","cli":"grok-4","enabled":true,"available":true}
        ]
        """);
        var store = Obj("""
        {
            "orchestratorId": "lead1",
            "refusals": {
                "lead1": {
                    "until": "2026-01-02T15:00:00Z",
                    "reason": "QUOTA_ERROR: out of credits"
                }
            }
        }
        """);
        var context = Context(models, store) with
        {
            // isLow returns true for peer so peer cannot be selected via LeadRanking
            IsLow = m => m["id"]!.GetValue<string>() == "peer"
        };

        var lead = OrchestratorTeamModels.SelectLead(context);
        Assert.NotNull(lead);
        Assert.Equal("alt", lead!["id"]!.GetValue<string>());
        Assert.Equal("lead1", lead["fallbackFrom"]!.GetValue<string>());
    }

    [Fact]
    public void SwitchLeadTruncatesProjectLogAtTwelveEntries()
    {
        var existingLog = new JsonArray();
        for (var i = 0; i < 20; i++)
        {
            existingLog.Add(new JsonObject { ["at"] = "2026-01-01T00:00:00Z", ["text"] = $"Log entry {i}" });
        }
        var models = Arr("""
        [
            {"id":"lead1","label":"Lead 1","provider":"codex","cli":"gpt-6","enabled":true,"available":true},
            {"id":"lead2","label":"Lead 2","provider":"codex","cli":"gpt-6","enabled":true,"available":true}
        ]
        """);
        var store = new JsonObject
        {
            ["orchestratorId"] = "lead1",
            ["projects"] = new JsonObject
            {
                ["p1"] = new JsonObject
                {
                    ["id"] = "p1",
                    ["log"] = existingLog
                }
            }
        };
        var context = Context(models, store) with
        {
            CurrentProjectId = () => "p1",
            IsLow = m => m["id"]!.GetValue<string>() == "lead1"
        };

        var chosen = OrchestratorTeamModels.SelectLead(context);
        Assert.NotNull(chosen);
        Assert.Equal("lead2", chosen!["id"]!.GetValue<string>());

        var projectLog = store["projects"]!["p1"]!["log"] as JsonArray;
        Assert.NotNull(projectLog);
        // The project log must contain at most 12 items (1 new + up to 11 previous)
        Assert.Equal(12, projectLog!.Count);
        Assert.Contains("Planwharf switched the lead", projectLog[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ResearchModelEnforcesSavingModeRouteErrorsAndRemainingQuota()
    {
        var models = Arr("""
        [
            {"id":"flagship","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"kind":"premium","cost":1,"remaining":10},
            {"id":"exhausted","provider":"codex","cli":"gpt-6","enabled":true,"available":true,"cost":5,"remaining":2},
            {"id":"unsupported-adapter","provider":"grok","cli":"grok-4","enabled":true,"available":true,"cost":1,"remaining":10}
        ]
        """);
        var store = new JsonObject
        {
            ["savingModes"] = new JsonObject { ["p1"] = "safe" },
            ["usage"] = new JsonObject
            {
                ["exhausted"] = new JsonObject { ["used"] = 50 }
            }
        };
        var context = Context(models, store) with { CurrentProjectId = () => "p1" };

        // Flagship model rejected in safe mode
        var modeEx = Assert.Throws<InvalidOperationException>(() => OrchestratorTeamModels.ResearchModel(context, "flagship"));
        Assert.Contains("Safe does not", modeEx.Message);

        // Exhausted model rejected when remaining < cost
        var quotaEx = Assert.Throws<InvalidOperationException>(() => OrchestratorTeamModels.ResearchModel(context, "exhausted"));
        Assert.Contains("no remaining usage", quotaEx.Message);

        // Adapter lacking source-protected research rejected
        var adapterEx = Assert.Throws<InvalidOperationException>(() => OrchestratorTeamModels.ResearchModel(context, "unsupported-adapter"));
        Assert.Contains("Source-protected research is not supported", adapterEx.Message);
    }

    [Fact]
    public void SetModelRolesClearsOrchestratorWhenOrchestratorRoleIsRemoved()
    {
        var models = Arr("""[{"id":"m1","enabled":true,"available":true}]""");
        var store = Obj("""{"orchestratorId":"m1"}""");
        var context = Context(models, store);

        // Disabling orchestrator role on the active lead should clear orchestratorId
        var result = OrchestratorTeamModels.SetModelRoles(context, "m1", Obj("""{"orchestrator":false}"""));
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Null(store["orchestratorId"]);
    }

    [Fact]
    public void SetRolesForManyHandlesPartialEligibility()
    {
        var models = Arr("""
        [
            {"id":"m1","provider":"codex","enabled":true,"available":true},
            {"id":"m2","provider":"api","enabled":true,"available":true}
        ]
        """);
        var context = Context(models);

        // m1 has provider "codex", so it is not eligible for supervisor role.
        // But it is eligible for worker role.
        // SetRolesForMany should filter out supervisor and apply worker.
        var updates = Arr("""
        [
            {"id":"m1","roles":{"worker":true,"supervisor":true}},
            {"id":"m2","roles":{"supervisor":true}}
        ]
        """);
        var result = OrchestratorTeamModels.SetRolesForMany(context, updates);
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(2, result["changed"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReassignCrewValidatesWhyLengthAndBridgeExceptions()
    {
        var (bridge, mega, row, _, _, _) = CreateBridgeFixture();
        var step = bridge.Data.SubTasks[0]!;
        var context = Context(new JsonArray()) with { ExecutionBridge = bridge };

        // 1. Extra invalid properties on action
        var badAction = new JsonObject
        {
            ["type"] = "reassign_crew",
            ["projectId"] = "p",
            ["assignments"] = new JsonObject { [step["id"]!.GetValue<string>()] = "b" },
            ["extraUnexpected"] = true
        };
        var badResult = await OrchestratorTeamModels.ReassignCrew(context, badAction, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.False(badResult["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid crew change", badResult["reason"]!.GetValue<string>());

        // 2. Long why string truncated cleanly without error
        var longWhy = new string('x', 500);
        var longWhyAction = new JsonObject
        {
            ["type"] = "reassign_crew",
            ["projectId"] = "p",
            ["assignments"] = new JsonObject { [step["id"]!.GetValue<string>()] = "b" },
            ["why"] = longWhy
        };
        var okResult = await OrchestratorTeamModels.ReassignCrew(context, longWhyAction, new JsonObject { ["id"] = "p", ["megaTaskId"] = mega["id"]!.DeepClone() });
        Assert.True(okResult["ok"]!.GetValue<bool>());

        // 3. Missing megaTaskId in row
        var noWorkResult = await OrchestratorTeamModels.ReassignCrew(context, new JsonObject
        {
            ["type"] = "reassign_crew",
            ["projectId"] = "p",
            ["assignments"] = new JsonObject { [step["id"]!.GetValue<string>()] = "b" }
        }, new JsonObject { ["id"] = "p" });
        Assert.False(noWorkResult["ok"]!.GetValue<bool>());
        Assert.Contains("no proposed or stopped work", noWorkResult["reason"]!.GetValue<string>());
    }

    [Fact]
    public void FleetOfEvaluatesExhaustionHistoricalAssignmentsAndRoleLabels()
    {
        var models = Arr("""
        [
            {"id":"m1","enabled":true,"available":true,"plan":"pool1","roles":{"worker":true,"reviewer":true}},
            {"id":"m2","enabled":true,"available":true,"plan":"pool2","roles":{"worker":false,"reviewer":false,"orchestrator":false,"supervisor":false}},
            {"id":"m3","enabled":true,"available":true,"plan":"pool3"}
        ]
        """);
        var projects = Arr("""
        [
            {
                "id":"p1",
                "name":"Project 1",
                "status":"idle",
                "dispatch":[
                    {
                        "modelId":"m1",
                        "historicalAssignment":true,
                        "assignmentStatus":"complete"
                    }
                ]
            }
        ]
        """);
        var plans = Arr("""
        [
            {"id":"pool1","planStatus":"exhausted","leftPct":0,"readable":true},
            {"id":"pool2","planStatus":"active","leftPct":50,"readable":true}
        ]
        """);

        var fleet = OrchestratorTeamModels.FleetOf(models, projects, "m3", plans);
        Assert.Equal(3, fleet.Count);

        // m1: plan is exhausted, not working, so state is exhausted
        var m1 = fleet[0]!;
        Assert.True(m1["exhausted"]!.GetValue<bool>());
        Assert.Equal("exhausted", m1["state"]!.GetValue<string>());
        Assert.Equal("Worker / Reviewer", m1["role"]!.GetValue<string>());
        var assignments = m1["assignments"] as JsonArray;
        Assert.NotNull(assignments);
        Assert.Single(assignments);
        Assert.True(assignments![0]!["historicalAssignment"]!.GetValue<bool>());
        Assert.Equal("Historical assignment title not retained", assignments[0]!["taskTitle"]!.GetValue<string>());
        Assert.Equal("Original activity is not retained for this historical assignment.", assignments[0]!["activity"]!.GetValue<string>());

        // m2: no allowed roles
        var m2 = fleet[1]!;
        Assert.Equal("No allowed roles", m2["role"]!.GetValue<string>());
        Assert.False(m2["exhausted"]!.GetValue<bool>());

        // m3: lead model
        var m3 = fleet[2]!;
        Assert.Equal("Lead", m3["role"]!.GetValue<string>());
        Assert.True(m3["lead"]!.GetValue<bool>());
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
