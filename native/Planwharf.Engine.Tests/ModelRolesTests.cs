// Port of the standalone cases in engine/model-roles.test.cjs, plus module-alone
// checks from engine/supervisor-role.test.cjs, engine/autonomy-crew.test.cjs and
// engine/gold-model.test.cjs.
//
// Ported:
//  - "user role permissions constrain routing independently of model capability":
//    withModelRoles preferences, inspector eligibility, and an ineligible
//    orchestrator preference staying false. pickWorker / canOrchestrate need
//    orchestrator.js and are deferred.
//  - "fourth permission is opt-in, preserves old roles, and never authorizes
//    implementation or review" (supervisor-role.test.cjs): ROLE_IDS, legacy
//    roles, supervisor opt-in, and permitsAssignment for implementation/audit.
//  - "the Verifier role takes the verify slice" (autonomy-crew.test.cjs):
//    assignmentRole and permitsAssignment; pickWorkers / dispatchPlan deferred.
//  - "Gold pins exact provider/model for workers while reviewers keep their
//    roles" (gold-model.test.cjs): permitsAssignment gold id/provider/cli
//    mismatch. pickWorker gold routing needs orchestrator.js and is deferred.
//
// Deferred (need the server or orchestrator.js, not this module alone):
//  - Remaining pickWorker / canOrchestrate assertions in
//    "user role permissions constrain routing independently of model capability".
//  - Whole of "role settings persist, incompatible roles are rejected, exact
//    audit/implementation assignments honor flags" (createServer, HTTP
//    /api/models/roles, lead, approve, export, restart).
//  - Remaining gold-model.test.cjs cases (pickWorker, bulk roles HTTP, Gold
//    restart and quota).
//  - Remaining supervisor-role.test.cjs cases (createSupervisor, createServer).
//  - Remaining autonomy-crew.test.cjs cases (pickWorkers, dispatchPlan).
//
// Differences from the JavaScript:
//  - Models and jobs are JsonObject, not plain objects. Missing keys are JSON
//    absence; JSON null is treated as missing for ?. and ??, matching JS.
//  - withModelRoles deep-clones the model. JavaScript spreads one level.
//    Unknown fields are kept. If roleEligibility/roles already existed, C#
//    overwrites them in place; JavaScript puts the new keys at the end.
//  - Gold id/provider/cli use JsonNode.DeepEquals (JSON value equality).
//    JavaScript uses === (reference equality for objects).
//  - ROLE_IDS is RoleIds (PascalCase). VERIFY_SLICE is VerifySlice.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ModelRolesTests
{
    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    static JsonObject Capable() => Obj("""{"id":"capable","enabled":true,"kind":"default","orchestratorEligible":true,"workerEligible":true}""");
    static bool Role(JsonObject model, string role) => model["roles"]![role]!.GetValue<bool>();
    static bool Eligible(JsonObject model, string role) => model["roleEligibility"]![role]!.GetValue<bool>();

    [Fact]
    public void Role_ids_and_verify_slice_match_javascript()
    {
        Assert.Equal(["orchestrator", "worker", "verifier", "reviewer", "supervisor"], ModelRoles.RoleIds);
        Assert.Equal("Verify the work independently against the job", ModelRoles.VerifySlice);
    }

    [Fact]
    public void User_role_permissions_constrain_routing_independently_of_model_capability()
    {
        var baseModel = Capable();
        var reviewer = ModelRoles.WithModelRoles(baseModel, Obj("""{"orchestrator":false,"worker":false,"reviewer":true}"""));
        Assert.False(Role(reviewer, "orchestrator"));
        Assert.False(Role(reviewer, "worker"));
        Assert.True(Role(reviewer, "reviewer"));
        Assert.True(Eligible(reviewer, "orchestrator"));
        Assert.True(Eligible(reviewer, "worker"));

        var lead = ModelRoles.WithModelRoles(baseModel, Obj("""{"orchestrator":true,"worker":false,"reviewer":false}"""));
        Assert.True(Role(lead, "orchestrator"));
        Assert.False(Role(lead, "worker"));
        Assert.False(Role(lead, "reviewer"));

        var inspector = Capable();
        inspector["workerEligible"] = false;
        inspector["reviewerEligible"] = true;
        inspector["capabilities"] = Obj("""{"canCode":false}""");
        var inspected = ModelRoles.WithModelRoles(inspector);
        Assert.False(Role(inspected, "worker"));
        Assert.True(Role(inspected, "reviewer"));
        Assert.True(Eligible(inspected, "reviewer"));

        var unsupported = Capable();
        unsupported["orchestratorEligible"] = false;
        var blocked = ModelRoles.WithModelRoles(unsupported, Obj("""{"orchestrator":true}"""));
        Assert.False(Role(blocked, "orchestrator"));
        Assert.False(Eligible(blocked, "orchestrator"));
    }

    [Fact]
    public void Fourth_permission_is_opt_in_preserves_old_roles_and_never_authorizes_implementation_or_review()
    {
        var legacy = Obj("""{"orchestrator":false,"worker":true,"reviewer":false}""");
        foreach (var provider in new[] { "api", "codex", "grok", "antigravity" })
        {
            var model = Obj("""{"id":"advisory","provider":"api","cli":"exact-fixture","enabled":true,"available":true}""");
            model["provider"] = provider;
            var roles = ModelRoles.WithModelRoles(model, legacy)["roles"]!.AsObject();
            Assert.False(roles["orchestrator"]!.GetValue<bool>());
            Assert.True(roles["worker"]!.GetValue<bool>());
            Assert.True(roles["verifier"]!.GetValue<bool>());
            Assert.False(roles["reviewer"]!.GetValue<bool>());
            Assert.False(roles["supervisor"]!.GetValue<bool>());
        }
        var api = Obj("""{"id":"advisory","provider":"api","cli":"exact-fixture","enabled":true,"available":true}""");
        Assert.False(Role(ModelRoles.WithModelRoles(api), "supervisor"));
        var opted = ModelRoles.WithModelRoles(api, Obj("""{"orchestrator":false,"worker":false,"reviewer":false,"supervisor":true}"""));
        Assert.True(Role(opted, "supervisor"));
        Assert.False(ModelRoles.PermitsAssignment(opted, Obj("""{"kind":"implementation"}""")));
        Assert.False(ModelRoles.PermitsAssignment(opted, Obj("""{"kind":"audit"}""")));
        var codex = Obj("""{"id":"advisory","provider":"codex","cli":"exact-fixture","enabled":true,"available":true}""");
        Assert.False(Role(ModelRoles.WithModelRoles(codex, Obj("""{"supervisor":true}""")), "supervisor"));
    }

    [Fact]
    public void The_verifier_role_takes_the_verify_slice()
    {
        var job = Obj("""{"title":"Build it","detail":"Implement the feature with tests."}""");
        Assert.Equal("verifier", ModelRoles.AssignmentRole(Obj("""{"title":"Verify the work independently against the job: run the tests"}""")));
        Assert.Equal("worker", ModelRoles.AssignmentRole(job));
        Assert.True(Role(ModelRoles.WithModelRoles(Obj("""{"id":"a","roles":{"worker":true}}""")), "verifier"));
        Assert.False(Role(ModelRoles.WithModelRoles(Obj("""{"id":"a","roles":{"worker":false}}""")), "verifier"));
        var checker = Obj("""{"id":"opus","label":"opus","provider":"claude","cli":"opus","modelRouting":"configured","enabled":true,"available":true,"kind":"default","roles":{"worker":false,"verifier":true}}""");
        Assert.True(ModelRoles.PermitsAssignment(checker, Obj("""{"title":"Verify the work independently against the job"}""")));
        Assert.False(ModelRoles.PermitsAssignment(checker, job));
    }

    [Fact]
    public void Gold_pins_exact_provider_model_for_workers()
    {
        var gold = Obj("""{"id":"gold","provider":"codex","modelRouting":"configured","cli":"gold","enabled":true,"available":true,"kind":"default"}""");
        var goldModel = Obj("""{"id":"gold","provider":"codex","cli":"gold"}""");
        var job = new JsonObject { ["goldModel"] = goldModel };
        Assert.True(ModelRoles.PermitsAssignment(gold, job));
        var changedCli = (JsonObject)gold.DeepClone();
        changedCli["cli"] = "changed";
        Assert.False(ModelRoles.PermitsAssignment(changedCli, job));
        var changedProvider = (JsonObject)gold.DeepClone();
        changedProvider["provider"] = "different";
        Assert.False(ModelRoles.PermitsAssignment(changedProvider, job));
        var audit = new JsonObject { ["kind"] = "audit", ["goldModel"] = goldModel.DeepClone() };
        Assert.True(ModelRoles.PermitsAssignment(gold, audit));
        var other = Obj("""{"id":"default","provider":"codex","cli":"gold","enabled":true,"kind":"default"}""");
        Assert.False(ModelRoles.PermitsAssignment(other, job));
    }

    [Fact]
    public void Assignment_role_reads_type_kind_and_verify_slice_text()
    {
        Assert.Equal("worker", ModelRoles.AssignmentRole(null));
        Assert.Equal("worker", ModelRoles.AssignmentRole(new JsonObject()));
        Assert.Equal("reviewer", ModelRoles.AssignmentRole(Obj("""{"type":"AUDIT"}""")));
        Assert.Equal("reviewer", ModelRoles.AssignmentRole(Obj("""{"kind":"audit"}""")));
        Assert.Equal("reviewer", ModelRoles.AssignmentRole(Obj("""{"kind":"review"}""")));
        Assert.Equal("reviewer", ModelRoles.AssignmentRole(Obj("""{"type":"AUDIT","kind":"verify","title":"Verify the work independently against the job"}""")));
        Assert.Equal("verifier", ModelRoles.AssignmentRole(Obj("""{"kind":"verify"}""")));
        Assert.Equal("verifier", ModelRoles.AssignmentRole(Obj("""{"detail":"Verify the work independently against the job and fix it"}""")));
        Assert.Equal("verifier", ModelRoles.AssignmentRole(Obj("""{"slice":"Verify the work independently against the job"}""")));
        Assert.Equal("worker", ModelRoles.AssignmentRole(Obj("""{"type":"IMPLEMENTATION","title":"Implement"}""")));
        Assert.Equal("worker", ModelRoles.AssignmentRole(Obj("""{"kind":"implementation"}""")));
        Assert.False(ModelRoles.IsVerifySlice(null));
        Assert.False(ModelRoles.IsVerifySlice(JsonValue.Create(1)));
        Assert.False(ModelRoles.IsVerifySlice(JsonValue.Create("verify the work independently against the job")));
        Assert.True(ModelRoles.IsVerifySlice(JsonValue.Create(ModelRoles.VerifySlice)));
    }

    [Fact]
    public void Role_eligibility_follows_tools_kind_and_capability_flags()
    {
        var local = ModelRoles.RoleEligibility(Obj("""{"kind":"local"}"""));
        Assert.False(local["orchestrator"]);
        Assert.True(local["worker"]);
        Assert.True(local["verifier"]);
        Assert.True(local["reviewer"]);
        Assert.False(local["supervisor"]);

        var noTools = ModelRoles.RoleEligibility(Obj("""{"capabilities":{"canUseTools":false},"orchestratorEligible":true,"reviewerEligible":true}"""));
        Assert.False(noTools["orchestrator"]);
        Assert.False(noTools["reviewer"]);
        Assert.True(noTools["worker"]);

        var noFs = ModelRoles.RoleEligibility(Obj("""{"capabilities":{"canInspectFilesystem":false}}"""));
        Assert.False(noFs["orchestrator"]);
        Assert.False(noFs["reviewer"]);

        var workerOff = ModelRoles.RoleEligibility(Obj("""{"workerEligible":false}"""));
        Assert.False(workerOff["worker"]);
        Assert.False(workerOff["verifier"]);
        Assert.False(workerOff["reviewer"]);

        var capWorkerOff = ModelRoles.RoleEligibility(Obj("""{"capabilities":{"workerEligible":false}}"""));
        Assert.False(capWorkerOff["worker"]);
        Assert.True(capWorkerOff["reviewer"]);

        var api = ModelRoles.RoleEligibility(Obj("""{"provider":"api"}"""));
        Assert.True(api["supervisor"]);
        var adapter = ModelRoles.RoleEligibility(Obj("""{"adapter":"api","provider":"codex"}"""));
        Assert.True(adapter["supervisor"]);
        var emptyAdapter = ModelRoles.RoleEligibility(Obj("""{"adapter":"","provider":"api"}"""));
        Assert.True(emptyAdapter["supervisor"]);
        Assert.False(ModelRoles.SupportsSupervisor(Obj("""{"adapter":"codex","provider":"api"}""")));
        Assert.False(ModelRoles.RoleEligibility(Obj("""{"provider":"api","supervisorEligible":false}"""))["supervisor"]);
        Assert.False(ModelRoles.RoleEligibility(Obj("""{"provider":"api","capabilities":{"supervisorEligible":false}}"""))["supervisor"]);
        Assert.True(ModelRoles.RoleEligibility(Obj("""{"kind":"local","capabilities":{"orchestratorEligible":true}}"""))["orchestrator"]);
        Assert.True(ModelRoles.RoleEligibility(Obj("""{"kind":"local","capabilities":{"canOrchestrate":true}}"""))["orchestrator"]);
    }

    [Fact]
    public void With_model_roles_keeps_unknown_fields_and_does_not_mutate_the_input()
    {
        var model = Obj("""{"id":"x","extra":{"keep":true},"roles":{"worker":false},"orchestratorEligible":true}""");
        var copy = ModelRoles.WithModelRoles(model, Obj("""{"reviewer":true}"""));
        Assert.True(JsonNode.DeepEquals(copy["extra"], Obj("""{"keep":true}""")));
        Assert.Equal("x", copy["id"]!.GetValue<string>());
        Assert.False(Role(copy, "worker"));
        Assert.True(Role(copy, "reviewer"));
        Assert.False(Role(copy, "verifier"));
        Assert.False(model.ContainsKey("roleEligibility"));
        Assert.False(model["roles"]!["reviewer"]?.GetValue<bool>() ?? false);
        var numeric = ModelRoles.WithModelRoles(Capable(), Obj("""{"orchestrator":0,"worker":1,"reviewer":""}"""));
        Assert.False(Role(numeric, "orchestrator"));
        Assert.True(Role(numeric, "worker"));
        Assert.False(Role(numeric, "reviewer"));
        var nullPref = ModelRoles.WithModelRoles(Obj("""{"id":"a","roles":{"worker":false}}"""), Obj("""{"worker":null}"""));
        Assert.False(Role(nullPref, "worker"));
        Assert.False(Role(nullPref, "verifier"));
    }

    [Fact]
    public void Permits_assignment_uses_provider_id_fallback_and_skips_gold_when_not_a_worker_job()
    {
        var model = Obj("""{"id":"gold","providerId":"grok","provider":"codex","cli":"gold","enabled":true}""");
        var job = Obj("""{"goldModel":{"id":"gold","provider":"grok","cli":"gold"}}""");
        Assert.True(ModelRoles.PermitsAssignment(model, job));
        var emptyId = Obj("""{"id":"gold","providerId":"","provider":"codex","cli":"gold"}""");
        Assert.False(ModelRoles.PermitsAssignment(emptyId, job));
        var goldString = new JsonObject { ["goldModel"] = "gold" };
        Assert.False(ModelRoles.PermitsAssignment(model, goldString));
        var noGold = ModelRoles.PermitsAssignment(Obj("""{"id":"a","roles":{"worker":true}}"""), new JsonObject());
        Assert.True(noGold);
        var disabled = Obj("""{"id":"a","roles":{"worker":false,"verifier":false,"reviewer":false}}""");
        Assert.False(ModelRoles.PermitsAssignment(disabled, new JsonObject()));
        Assert.False(ModelRoles.PermitsAssignment(disabled, Obj("""{"kind":"verify"}""")));
        var zeroGold = new JsonObject { ["goldModel"] = 0 };
        Assert.True(ModelRoles.PermitsAssignment(Obj("""{"id":"a"}"""), zeroGold));
        var providerIdPreferred = Obj("""{"id":"gold","providerId":"grok","provider":"codex","cli":"gold"}""");
        var wrongGoldProvider = Obj("""{"goldModel":{"id":"gold","provider":"codex","cli":"gold"}}""");
        Assert.False(ModelRoles.PermitsAssignment(providerIdPreferred, wrongGoldProvider));
    }
}
