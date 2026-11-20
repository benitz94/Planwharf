// Port of the models.js contract in native/porting/jobs/23-phase2-rest.md.
// No Node test calls models.js. These cases are the sheet's comparison cases, plus the
// checks that contract states directly.
//
// Deferred cases (job 24, not implemented in this file):
// - attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// - createServer, createOrchestrator, spawnWorker, runner.cjs, and the HTTP loops in
//   task-completion.test.cjs, grill-chat.test.cjs, project-export.test.cjs,
//   project-teams.test.cjs, start-over.test.cjs (archiveProject), and
//   worker-containment.test.cjs (launch artifacts and provider cwd)
// - Live npm test, live node --test, and live taskkill
// - Byte-identical randomUUID values across JavaScript and C#
// - gitEnvironment and within as parity JSON
// - The browser root / window / AMD wrappers, models.json, and provider-catalog
// - A second process appending to invocations.jsonl at the same time
// - Reading the checkout's plan.json or .git
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ModelsTests
{
    static JsonArray Arr(string json) => (JsonArray)JsonNode.Parse(json)!;
    static JsonObject Claim(JsonNode? models, string? modelId) => Models.ValidateWorkerClaim(models, modelId);
    static string? Reason(JsonObject claim) => claim["reason"]?.GetValue<string>();
    static string Id(JsonNode? node) => node!["id"]!.GetValue<string>();

    [Fact]
    public void Null_entry_is_omitted_and_stays_null()
    {
        var input = Arr("""[null, {"id":"a","label":"A","enabled":true}, null]""");
        var before = input.ToJsonString();

        var enabled = Models.GetEnabledModels(input);
        Assert.Single(enabled);
        Assert.Equal("a", Id(enabled[0]));
        Assert.Equal(before, input.ToJsonString());

        var updated = Models.SetModelEnabled(input, "a", false);
        Assert.Null(updated[0]);
        Assert.Null(updated[2]);
        Assert.False(updated[1]!["enabled"]!.GetValue<bool>());
        Assert.Equal(before, input.ToJsonString());
    }

    [Fact]
    public void Enabled_true_with_available_false_is_omitted()
    {
        var input = Arr("""[{"id":"down","enabled":true,"available":false},{"id":"up","enabled":true,"available":true},{"id":"open","enabled":true},{"id":"off","enabled":false,"available":true}]""");
        var enabled = Models.GetEnabledModels(input);
        Assert.Equal(2, enabled.Count);
        Assert.Equal("up", Id(enabled[0]));
        Assert.Equal("open", Id(enabled[1]));
        Assert.True(input[0]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Copied_object_does_not_alias_the_input()
    {
        var input = Arr("""[{"id":"a","label":"A","enabled":true,"available":true,"rank":1}]""");
        var source = (JsonObject)input[0]!;

        var enabled = Models.GetEnabledModels(input);
        var copy = (JsonObject)enabled[0]!;
        Assert.NotSame(source, copy);
        copy["label"] = "changed";
        copy["enabled"] = false;
        Assert.Equal("A", source["label"]!.GetValue<string>());
        Assert.True(source["enabled"]!.GetValue<bool>());
        Assert.Equal("1", copy["rank"]!.ToJsonString());

        var updated = Models.SetModelEnabled(input, "other", false);
        var untouched = (JsonObject)updated[0]!;
        Assert.NotSame(source, untouched);
        untouched["label"] = "also changed";
        Assert.Equal("A", source["label"]!.GetValue<string>());
        Assert.True(untouched["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void SetModelEnabled_refuses_to_turn_an_unavailable_model_on()
    {
        var input = Arr("""[{"id":"down","label":"Down","enabled":false,"available":false},{"id":"up","enabled":true,"available":true}]""");
        var before = input.ToJsonString();
        var updated = Models.SetModelEnabled(input, "down", true);

        Assert.False(updated[0]!["enabled"]!.GetValue<bool>());
        Assert.False(updated[0]!["available"]!.GetValue<bool>());
        Assert.Equal("Down", updated[0]!["label"]!.GetValue<string>());
        Assert.True(updated[1]!["enabled"]!.GetValue<bool>());
        Assert.NotSame(input[0], updated[0]);
        Assert.NotSame(input[1], updated[1]);
        Assert.Equal(before, input.ToJsonString());

        var forcedOff = Models.SetModelEnabled(input, "down", false);
        Assert.False(forcedOff[0]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void No_model_enabled()
    {
        Assert.Equal("no model enabled", Reason(Claim(Arr("[]"), "a")));
        Assert.Equal("no model enabled", Reason(Claim(Arr("[]"), null)));
        Assert.Equal("no model enabled", Reason(Claim(null, null)));
        Assert.Equal("no model enabled", Reason(Claim(Arr("""[{"id":"a","enabled":false}]"""), null)));
        Assert.Equal("no model enabled", Reason(Claim(Arr("""[{"id":"a","enabled":true,"available":false}]"""), "a")));
        Assert.False(Claim(Arr("""[null, {"enabled":"true"}]"""), "missing")["ok"]!.GetValue<bool>());
        Assert.Equal("no model enabled", Reason(Claim(Arr("""[null, {"enabled":"true"}]"""), "missing")));
    }

    [Fact]
    public void Model_not_present()
    {
        var models = Arr("""[{"id":"a","enabled":true,"available":true}, {"id":"b","enabled":false}]""");
        var claim = Claim(models, "missing");
        Assert.False(claim["ok"]!.GetValue<bool>());
        Assert.Equal("model not present", Reason(claim));
        Assert.Equal("model not present", Reason(Claim(models, null)));
    }

    [Fact]
    public void Model_disabled()
    {
        var models = Arr("""[{"id":"on","enabled":true,"available":true},{"id":"off","enabled":false,"available":true},{"id":"down","enabled":true,"available":false},{"id":"loose","enabled":1,"available":true}]""");
        Assert.Equal("model disabled", Reason(Claim(models, "off")));
        Assert.Equal("model disabled", Reason(Claim(models, "down")));
        Assert.Equal("model disabled", Reason(Claim(models, "loose")));
        Assert.NotEqual("model not eligible", Reason(Claim(models, "off")));

        var ok = Claim(models, "on");
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Null(ok["reason"]);
    }

    [Fact]
    public void Non_array_inputs_return_an_empty_array()
    {
        JsonNode?[] inputs = [null, JsonNode.Parse("""{"id":"a","enabled":true}"""), JsonValue.Create("a"), JsonValue.Create(1)];
        foreach (var input in inputs)
        {
            Assert.Empty(Models.GetEnabledModels(input));
            Assert.Empty(Models.SetModelEnabled(input, "a", true));
        }
    }

    [Fact]
    public void SetModelEnabled_stores_a_boolean_and_can_turn_an_available_model_off_and_on()
    {
        var input = Arr("""[{"id":"a","label":"A","enabled":"yes","available":true}]""");
        var off = Models.SetModelEnabled(input, "a", false);
        Assert.False(off[0]!["enabled"]!.GetValue<bool>());
        Assert.Equal("A", off[0]!["label"]!.GetValue<string>());

        var on = Models.SetModelEnabled(off, "a", true);
        Assert.True(on[0]!["enabled"]!.GetValue<bool>());
        Assert.Equal("a", Id(Models.GetEnabledModels(on)[0]));

        var open = Arr("""[{"id":"b","enabled":true}]""");
        var claim = Claim(open, "b");
        Assert.True(claim["ok"]!.GetValue<bool>());
        Assert.Null(claim["reason"]);
    }

    [Fact]
    public void SetModelEnabled_keeps_falsy_entries_and_assigns_non_objects()
    {
        var input = Arr("""[0, "", false, "ab", 2, true, [{"id":"z"}]]""");
        var before = input.ToJsonString();
        Assert.Empty(Models.GetEnabledModels(input));

        var updated = Models.SetModelEnabled(input, "ab", true);
        Assert.Equal("0", updated[0]!.ToJsonString());
        Assert.Equal("\"\"", updated[1]!.ToJsonString());
        Assert.False(updated[2]!.GetValue<bool>());
        Assert.Equal("a", updated[3]!["0"]!.GetValue<string>());
        Assert.Equal("b", updated[3]!["1"]!.GetValue<string>());
        Assert.Equal("{}", updated[4]!.ToJsonString());
        Assert.Equal("{}", updated[5]!.ToJsonString());
        Assert.Equal("z", updated[6]!["0"]!["id"]!.GetValue<string>());
        Assert.NotSame(((JsonArray)input[6]!)[0], updated[6]!["0"]);
        Assert.Equal(before, input.ToJsonString());
    }
}
