// Port of the standalone cases in engine/model-policy.test.cjs, engine/quota-switch.test.cjs,
// and the availableAgain case in engine/finish-clean.test.cjs.
// Deferred (need the server, orchestrator, or another unported module):
//  - "ChatGPT siblings inherit one provider policy while Grok stays independent after restart"
//    (createOrchestrator, setProviderPolicy, restart, pickWorkers, launchSpec).
//  - providerPolicyFor assertions in "remaining quota semantics..." (engine/provider-policy.cjs).
//  - "permission restrictions and privacy-safe categorized reports" (launchSpec, diagnoseProvider).
//  - "a failed quota refresh cannot keep authorizing calls using stale percentages" (orchestrator).
//  - "policy API persists, lead and approved workers use automatic fallback without inference"
//    (createServer, /api/models/policy, /api/providers/policy, export).
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class ModelPolicyTests
{
    static readonly JsonObject Lead = Parse("""{"id":"lead","provider":"codex","cli":"gpt-6-astra","plan":"ChatGPT","enabled":true,"kind":"premium"}""");
    static readonly JsonObject Fallback = Parse("""{"id":"fallback","provider":"grok","cli":"grok-4.7","plan":"Grok","enabled":true,"kind":"default"}""");
    static readonly JsonObject Policy = Parse("""{"minRemainingPercent":20,"warningRemainingPercent":30,"fallbackModelId":"fallback","alwaysApprove":false}""");

    static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
    static JsonArray Arr(string json) => (JsonArray)JsonNode.Parse(json)!;
    static JsonObject Copy(JsonObject value) => (JsonObject)value.DeepClone();

    [Fact]
    public void Shared_usage_boundaries_unread_quotas_explicit_fallbacks_and_cycles()
    {
        var lead = Copy(Lead);
        lead["policy"] = Copy(Policy);
        var plans = Arr("""[{"id":"ChatGPT","readable":true,"leftPct":20},{"id":"Grok","readable":true,"leftPct":95}]""");
        Assert.Equal("limit", State(lead, plans));
        Assert.Equal("warning", State(lead, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":30}]""")));
        Assert.Equal("unread", State(lead, Arr("[]")));
        Assert.Equal("unread", State(lead, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":150}]""")));
        var roster = Models(lead, Copy(Fallback));
        Assert.Equal("fallback", Id(ModelPolicy.ResolveUsageFallback(lead, roster, plans, _ => true)));
        Assert.Null(ModelPolicy.ResolveUsageFallback(lead, roster, Arr("[]"), _ => true));
        var noFallback = Copy(lead);
        noFallback["policy"] = Parse("""{"minRemainingPercent":20,"warningRemainingPercent":30,"fallbackModelId":null,"alwaysApprove":false}""");
        Assert.Null(ModelPolicy.ResolveUsageFallback(noFallback, Models(Copy(Lead), Copy(Fallback)), plans, _ => true));
        var looping = Copy(Fallback);
        looping["policy"] = Parse("""{"minRemainingPercent":20,"warningRemainingPercent":30,"fallbackModelId":"lead","alwaysApprove":false}""");
        Assert.Null(ModelPolicy.ResolveUsageFallback(lead, Models(lead, looping), Arr("""[{"id":"ChatGPT","readable":true,"leftPct":20},{"id":"Grok","readable":true,"leftPct":0}]"""), _ => true));
        Assert.Null(ModelPolicy.ResolveUsageFallback(lead, roster, plans, model => Id(model) != "lead"));
        foreach (var invalid in new JsonNode?[]
        {
            Parse("""{"minRemainingPercent":-1}"""),
            Parse("""{"minRemainingPercent":"80"}"""),
            Parse("""{"warningRemainingPercent":10,"minRemainingPercent":20}"""),
            Parse("""{"alwaysApprove":"yes"}"""),
            Parse("""{"unknown":true}""")
        })
            Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(invalid));
    }

    [Fact]
    public void Remaining_quota_semantics_automatic_early_warning_reset_and_legacy_migration()
    {
        var model = Copy(Lead);
        model["policy"] = Parse("""{"minRemainingPercent":20}""");
        foreach (var (leftPct, expected) in new[] { (80, "ok"), (31, "ok"), (30, "warning"), (21, "warning"), (20, "limit"), (0, "limit"), (100, "ok") })
        {
            var status = ModelPolicy.UsagePolicy(model, Arr($"[{{\"id\":\"ChatGPT\",\"readable\":true,\"leftPct\":{leftPct}}}]"));
            Assert.Equal(expected, status["state"]!.ToString().Trim('"'));
            Assert.Equal(leftPct, Number(status["remainingPercent"]));
            Assert.Equal(100 - leftPct, Number(status["usedPercent"]));
            Assert.Equal(30, Number(status["warningRemainingPercent"]));
        }
        var high = Copy(Lead);
        high["policy"] = Parse("""{"minRemainingPercent":95}""");
        Assert.Equal("warning", State(high, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":100}]""")));
        var full = Copy(Lead);
        full["policy"] = Parse("""{"minRemainingPercent":100}""");
        Assert.Equal("limit", State(full, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":100}]""")));
        var zero = Copy(Lead);
        zero["policy"] = Parse("""{"minRemainingPercent":0}""");
        Assert.False(Blocked(zero, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":1}]""")));
        Assert.True(Blocked(zero, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":0}]""")));
        foreach (var invalid in new JsonNode?[]
        {
            null,
            new JsonArray(),
            Parse("""{"minRemainingPercent":20,"warningRemainingPercent":20}"""),
            Parse("""{"minRemainingPercent":20,"warningRemainingPercent":10}"""),
            Parse("""{"minRemainingPercent":20,"maxUsedPercent":80}""")
        })
            Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(invalid));
        var old = Parse("""{"maxUsedPercent":80,"warningUsedPercent":70,"alwaysApprove":false}""");
        var before = old.ToJsonString();
        Assert.True(JsonNode.DeepEquals(Parse("""{"minRemainingPercent":20,"warningRemainingPercent":30,"fallbackModelId":null,"alwaysApprove":false}"""), ModelPolicy.ValidatePolicy(old)));
        Assert.Equal(before, old.ToJsonString());
    }

    [Fact]
    public void Usage_reasons_match_JavaScript_text_and_pct_fills_a_missing_leftPct()
    {
        var model = Copy(Lead);
        model["policy"] = Parse("""{"minRemainingPercent":20,"warningRemainingPercent":30}""");
        var limit = ModelPolicy.UsagePolicy(model, Arr("""[{"id":"ChatGPT","readable":true,"leftPct":20}]"""));
        Assert.Equal("Remaining quota stop threshold reached (20.0% remaining / stop at 20%).", limit["reason"]!.GetValue<string>());
        var warning = ModelPolicy.UsagePolicy(model, Arr("""[{"id":"ChatGPT","readable":true,"pct":70}]"""));
        Assert.Equal(30, Number(warning["remainingPercent"]));
        Assert.Equal("Low provider quota (30.0% remaining / warning at 30%).", warning["reason"]!.GetValue<string>());
        Assert.Equal("unread", State(model, Arr("""[{"id":"ChatGPT","readable":false,"leftPct":90}]""")));
    }

    [Fact]
    public void Automatic_fallback_skips_sibling_pools_restricted_roles_disabled_and_limited_providers()
    {
        var lead = Copy(Lead);
        lead["policy"] = Parse("""{"minRemainingPercent":20}""");
        lead["fallbackProviderId"] = "@automatic";
        JsonObject Alt(string json)
        {
            var model = Copy(Fallback);
            foreach (var pair in Parse(json)) model[pair.Key] = pair.Value?.DeepClone();
            return model;
        }
        var candidates = Models(lead, CopyWithId(lead, "same-provider"),
            Alt("""{"id":"same-pool","provider":"other","plan":"ChatGPT"}"""),
            Alt("""{"id":"disabled","enabled":false}"""),
            Alt("""{"id":"unavailable","available":false}"""),
            Alt("""{"id":"provider-off","providerEnabled":false}"""),
            Alt("""{"id":"wrong-role"}"""),
            Alt("""{"id":"limited","policy":{"minRemainingPercent":10},"plan":"Low"}"""),
            Alt("""{"id":"unknown-limited","policy":{"minRemainingPercent":10},"plan":"Unknown"}"""),
            Alt("""{"id":"chosen","alwaysApprove":false}"""));
        var plans = Arr("""[{"id":"ChatGPT","readable":true,"leftPct":20},{"id":"Grok","readable":true,"leftPct":80},{"id":"Low","readable":true,"leftPct":5}]""");
        bool Eligible(JsonObject model) => Id(model) != "wrong-role";
        var result = ModelPolicy.ResolveUsageFallback(lead, candidates, plans, Eligible)!;
        Assert.Equal("chosen", Id(result));
        Assert.False(result["alwaysApprove"]!.GetValue<bool>());
        Assert.Null(ModelPolicy.ResolveUsageFallback(lead, WithoutLast(candidates), plans, Eligible));
        var early = Arr("""[{"id":"ChatGPT","readable":true,"leftPct":21},{"id":"Grok","readable":true,"leftPct":80},{"id":"Low","readable":true,"leftPct":5}]""");
        Assert.Equal("lead", Id(ModelPolicy.ResolveUsageFallback(lead, candidates, early, Eligible)));
    }

    [Fact]
    public void Provider_fallback_follows_the_first_limited_sibling_to_its_own_model()
    {
        var lead = Copy(Lead);
        lead["policy"] = Parse("""{"minRemainingPercent":20}""");
        lead["fallbackProviderId"] = "grok";
        var first = Copy(Fallback);
        first["id"] = "grok-a";
        first["policy"] = Parse("""{"minRemainingPercent":50,"fallbackModelId":"rescue"}""");
        var second = Copy(Fallback);
        second["id"] = "grok-b";
        second["policy"] = Parse("""{"minRemainingPercent":50}""");
        var rescue = Copy(Fallback);
        rescue["id"] = "rescue";
        rescue["provider"] = "other";
        rescue["plan"] = "Other";
        var plans = Arr("""[{"id":"ChatGPT","readable":true,"leftPct":10},{"id":"Grok","readable":true,"leftPct":10},{"id":"Other","readable":true,"leftPct":90}]""");
        Assert.Equal("rescue", Id(ModelPolicy.ResolveUsageFallback(lead, Models(lead, first, second, rescue), plans, _ => true)));
    }

    [Fact]
    public void The_default_threshold_is_35_percent_left_on_the_5_hour_window_and_on_the_weekly_one()
    {
        var defaults = ModelPolicy.AutoSwitchDefault();
        Assert.Equal(35, defaults["thresholdPercent"]!.GetValue<int>());
        Assert.Equal(35, defaults["fiveHourPercent"]!.GetValue<int>());
        Assert.Equal(35, defaults["weeklyPercent"]!.GetValue<int>());
        Assert.True(defaults["enabled"]!.GetValue<bool>());
        Assert.Equal(300000, ModelPolicy.RetryUnavailableMs);
    }

    [Fact]
    public void Quota_is_low_at_or_under_the_threshold_or_when_exhausted_unread_quota_is_not_low()
    {
        var plans = SwitchPlans();
        var models = SwitchModels();
        Assert.Equal(12, ModelPolicy.QuotaLeft(models[0]!.AsObject(), plans));
        Assert.True(ModelPolicy.LowQuota(models[0]!.AsObject(), plans, 20));
        Assert.False(ModelPolicy.LowQuota(models[1]!.AsObject(), plans, 20));
        var exhausted = Parse("""{"id":"x","plan":"chatgpt","enabled":true}""");
        Assert.True(ModelPolicy.LowQuota(exhausted, Arr("""[{"id":"chatgpt","readable":true,"leftPct":50,"exhausted":true}]"""), 20));
        Assert.False(ModelPolicy.LowQuota(models[3]!.AsObject(), plans, 20));
    }

    [Fact]
    public void A_working_step_on_a_low_model_moves_to_the_eligible_model_with_the_most_quota_left()
    {
        var switches = ModelPolicy.QuotaSwitches(Arr("""[{"subTaskId":"s1","modelId":"terra"},{"subTaskId":"s2","modelId":"sonnet"}]"""), SwitchModels(), SwitchPlans(), 20, eligible: (_, _) => true);
        Assert.True(JsonNode.DeepEquals(Arr("""[{"subTaskId":"s1","fromModelId":"terra","toModelId":"gemini-pro","leftPercent":12}]"""), switches));
    }

    [Fact]
    public void No_switch_without_an_eligible_model_above_the_threshold_and_none_when_switching_is_off()
    {
        var running = Arr("""[{"subTaskId":"s1","modelId":"terra"}]""");
        var plans = SwitchPlans();
        Assert.Empty(ModelPolicy.QuotaSwitches(running, SwitchModels(), plans, 20, eligible: (candidate, _) => Id(candidate) == "terra"));
        Assert.Empty(ModelPolicy.QuotaSwitches(running, SwitchModels(), plans, 20, enabled: false, eligible: (_, _) => true));
        // Grok's quota is not reported (Grok Business bills in dollars): it can still take over, so the work goes on.
        Assert.Equal("grok", ModelPolicy.QuotaSwitches(running, Models(SwitchModels()[0]!.AsObject(), SwitchModels()[3]!.AsObject()), plans, 20, eligible: (_, _) => true).Single()!["toModelId"]!.GetValue<string>());
        // A model whose quota is read and above its limit comes before one whose quota is not reported.
        Assert.Equal("sonnet", ModelPolicy.QuotaSwitches(running, Models(SwitchModels()[0]!.AsObject(), SwitchModels()[3]!.AsObject(), SwitchModels()[1]!.AsObject()), plans, 20, eligible: (_, _) => true)[0]!["toModelId"]!.GetValue<string>());
    }

    [Fact]
    public void The_switch_keeps_the_check_independent_it_prefers_a_company_the_other_steps_do_not_use()
    {
        var models = Arr("""[{"id":"terra","plan":"chatgpt","enabled":true,"provider":"codex"},{"id":"sonnet","plan":"claude","enabled":true,"provider":"claude"},{"id":"gemini-pro","plan":"gemini","enabled":true,"provider":"antigravity"}]""");
        var running = Arr("""[{"subTaskId":"s1","modelId":"terra","avoidProviders":["antigravity"]}]""");
        Assert.Equal("sonnet", ModelPolicy.QuotaSwitches(running, models, SwitchPlans(), 20, eligible: (_, _) => true)[0]!["toModelId"]!.GetValue<string>());
        var onlyGemini = Models(models[0]!.AsObject(), models[2]!.AsObject());
        Assert.Equal("gemini-pro", ModelPolicy.QuotaSwitches(running, onlyGemini, SwitchPlans(), 20, eligible: (_, _) => true)[0]!["toModelId"]!.GetValue<string>());
    }

    [Fact]
    public void A_model_marked_unavailable_after_a_failure_is_tried_again_after_a_few_minutes()
    {
        var at = DateTimeOffset.Parse("2026-11-11T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var model = Parse($$"""{"id":"gemini","available":false,"unavailableReason":"CLI failed","unavailableAt":"{{DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}"}""");
        Assert.False(ModelPolicy.AvailableAgain(model, at + 60000));
        Assert.True(ModelPolicy.AvailableAgain(model, at + 6 * 60000));
        Assert.False(ModelPolicy.AvailableAgain(Parse("""{"id":"x","available":false,"unavailableReason":"not installed"}"""), at + 6 * 60000));
    }

    [Fact]
    public void Invalid_policies_use_the_JavaScript_error_text()
    {
        Assert.Equal("Invalid quota policy.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(null)).Message);
        Assert.Equal("Invalid quota policy.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(new JsonArray())).Message);
        Assert.Equal("Do not mix consumed and remaining quota settings.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"minRemainingPercent":20,"maxUsedPercent":80}"""))).Message);
        Assert.Equal("Invalid legacy usage percentage.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"maxUsedPercent":101}"""))).Message);
        Assert.Equal("Invalid model policy fields.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"unknown":true}"""))).Message);
        Assert.Equal("Remaining quota thresholds must be percentages from 0 to 100, or null.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"minRemainingPercent":"80"}"""))).Message);
        Assert.Equal("Warning remaining quota must be higher than the stop threshold.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"minRemainingPercent":20,"warningRemainingPercent":20}"""))).Message);
        Assert.Equal("Always approve must be a boolean.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"alwaysApprove":"yes"}"""))).Message);
        Assert.Equal("Fallback must be an explicit model ID, or null.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"fallbackModelId":"  "}"""))).Message);
        Assert.Equal("Configured remaining quota threshold cannot be verified: provider quota is unread.", ModelPolicy.UsagePolicy(WithPolicy(Lead, """{"minRemainingPercent":20}"""), Arr("[]"))["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Quota_left_skips_null_plans_and_keeps_an_out_of_range_leftPct()
    {
        var model = Parse("""{"id":"terra","plan":"chatgpt","enabled":true}""");
        var plans = new JsonArray();
        plans.Add(null);
        plans.Add(Parse("""{"id":"chatgpt","readable":true,"leftPct":150}"""));
        Assert.Equal(150, ModelPolicy.QuotaLeft(model, plans));
        model["policy"] = Parse("""{"minRemainingPercent":20}""");
        Assert.Equal("unread", State(model, Arr("""[{"id":"chatgpt","readable":true,"leftPct":150}]""")));
        Assert.False(ModelPolicy.AvailableAgain(null, 0));
        Assert.False(ModelPolicy.AvailableAgain(Parse("""{"available":false,"unavailableAt":"not-a-date"}"""), 0));
    }

    [Fact]
    public void Invalid_switch_inputs_fail_without_partial_results()
    {
        var running = Arr("""[{"subTaskId":"s1","modelId":"terra"},null]""");
        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelPolicy.QuotaSwitches(running, SwitchModels(), SwitchPlans()));
        Assert.Equal("Invalid running step.", error.Message);

        var avoidProviders = Arr("""[{"subTaskId":"s1","modelId":"terra","avoidProviders":"antigravity"}]""");
        var providerModels = SwitchModels();
        foreach (var model in providerModels.OfType<JsonObject>())
            model["provider"] = "fixture";
        error = Assert.Throws<InvalidOperationException>(() =>
            ModelPolicy.QuotaSwitches(avoidProviders, providerModels, SwitchPlans()));
        Assert.Equal("avoidProviders must be an array.", error.Message);

        var nullPlan = new JsonArray { null };
        error = Assert.Throws<InvalidOperationException>(() =>
            ModelPolicy.UsagePolicy(Lead, nullPlan));
        Assert.Equal("Invalid quota plan.", error.Message);
    }

    [Fact]
    public void Retry_boundary_and_concurrent_reads_are_deterministic()
    {
        var at = DateTimeOffset.Parse("2026-11-11T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var model = Parse($$"""{"available":false,"unavailableAt":"{{DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}"}""");
        Assert.False(ModelPolicy.AvailableAgain(model, at + ModelPolicy.RetryUnavailableMs - 1));
        Assert.True(ModelPolicy.AvailableAgain(model, at + ModelPolicy.RetryUnavailableMs));

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 100, _ =>
        {
            try
            {
                var result = ModelPolicy.QuotaSwitches(
                    Arr("""[{"subTaskId":"s1","modelId":"terra"}]"""),
                    SwitchModels(), SwitchPlans());
                Assert.Equal("gemini-pro", result[0]!["toModelId"]!.GetValue<string>());
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void Defaults_quota_pool_pct_and_exact_threshold_match_JavaScript()
    {
        var defaults = ModelPolicy.DefaultPolicy();
        Assert.True(JsonNode.DeepEquals(Parse("""{"minRemainingPercent":null,"warningRemainingPercent":null,"fallbackModelId":null,"alwaysApprove":false}"""), defaults));
        Assert.True(JsonNode.DeepEquals(defaults, ModelPolicy.ValidatePolicy(new JsonObject())));

        var pooled = Copy(Lead);
        pooled["quotaPoolId"] = "Grok";
        pooled["policy"] = Parse("""{"minRemainingPercent":20}""");
        var plans = Arr("""[{"id":"ChatGPT","readable":true,"leftPct":10},{"id":"Grok","readable":true,"leftPct":90}]""");
        Assert.Equal(90, ModelPolicy.QuotaLeft(pooled, plans));
        Assert.Equal("ok", State(pooled, plans));

        var fromPct = Parse("""{"id":"x","plan":"p","enabled":true}""");
        Assert.Equal(40, ModelPolicy.QuotaLeft(fromPct, Arr("""[{"id":"p","readable":true,"pct":60}]""")));

        var atThreshold = Parse("""{"id":"x","plan":"p","enabled":true}""");
        Assert.True(ModelPolicy.LowQuota(atThreshold, Arr("""[{"id":"p","readable":true,"leftPct":20}]"""), 20));
        Assert.False(ModelPolicy.LowQuota(atThreshold, Arr("""[{"id":"p","readable":true,"leftPct":21}]"""), 20));
        Assert.False(ModelPolicy.AvailableAgain(Parse("""{"id":"ok","available":true,"unavailableAt":"2026-11-11T16:00:00.000Z"}"""), 0));

        var lead = Copy(Lead);
        lead["policy"] = Parse("""{"minRemainingPercent":20,"fallbackModelId":"fallback"}""");
        var exhausted = Arr("""[{"id":"ChatGPT","readable":true,"leftPct":10},{"id":"Grok","readable":true,"leftPct":0}]""");
        Assert.Null(ModelPolicy.ResolveUsageFallback(lead, Models(lead, Copy(Fallback)), exhausted, _ => true));
    }

    [Fact]
    public void Zero_balance_null_identity_and_retry_edges_match_JavaScript()
    {
        var stopped = Parse("""{"id":"x","plan":"p","enabled":true,"policy":{"minRemainingPercent":20,"warningRemainingPercent":30}}""");
        var zeroLeft = ModelPolicy.UsagePolicy(stopped, Arr("""[{"id":"p","readable":true,"leftPct":0,"pct":40}]"""));
        Assert.Equal(0, Number(zeroLeft["remainingPercent"]));
        Assert.Equal(100, Number(zeroLeft["usedPercent"]));
        Assert.Equal("limit", zeroLeft["state"]!.GetValue<string>());
        Assert.Equal(0, ModelPolicy.QuotaLeft(Parse("""{"id":"x","plan":"p"}"""), Arr("""[{"id":"p","readable":true,"leftPct":0,"pct":40}]""")));

        var fractional = ModelPolicy.UsagePolicy(stopped, Arr("""[{"id":"p","readable":true,"leftPct":20.25}]"""));
        Assert.Equal("Low provider quota (20.3% remaining / warning at 30%).", fractional["reason"]!.GetValue<string>());

        var noPool = Parse("""{"id":"x","enabled":true,"policy":{"minRemainingPercent":10}}""");
        var shadowed = new JsonArray { JsonValue.Create(false), Parse("""{"readable":true,"leftPct":40}""") };
        Assert.Null(ModelPolicy.UsagePolicy(noPool, shadowed)["remainingPercent"]);
        Assert.Equal("unread", State(noPool, shadowed));
        Assert.Equal(40, ModelPolicy.QuotaLeft(noPool, shadowed));
        var arrayShadow = new JsonArray { new JsonArray(), Parse("""{"readable":true,"leftPct":40}""") };
        Assert.Null(ModelPolicy.QuotaLeft(noPool, arrayShadow));
        var exhaustedAfterFalse = new JsonArray { JsonValue.Create(false), Parse("""{"readable":true,"leftPct":5,"exhausted":true}""") };
        Assert.True(ModelPolicy.LowQuota(noPool, exhaustedAfterFalse, 20));

        var explicitNull = Parse("""{"id":"x","plan":null,"enabled":true,"policy":{"minRemainingPercent":10}}""");
        var nullIds = Arr("""[{"id":null,"readable":true,"leftPct":33},{"readable":true,"leftPct":77}]""");
        Assert.Equal(33, Number(ModelPolicy.UsagePolicy(explicitNull, nullIds)["remainingPercent"]));
        Assert.Equal(77, Number(ModelPolicy.UsagePolicy(noPool, nullIds)["remainingPercent"]));

        var emptyPool = Parse("""{"id":"x","quotaPoolId":"","plan":"real","enabled":true}""");
        var zeroPool = Parse("""{"id":"x","quotaPoolId":0,"plan":"real","enabled":true}""");
        var pooled = Arr("""[{"id":"real","readable":true,"leftPct":50},{"id":"","readable":true,"leftPct":1},{"id":0,"readable":true,"leftPct":1}]""");
        Assert.Equal(50, ModelPolicy.QuotaLeft(emptyPool, pooled));
        Assert.Equal(50, ModelPolicy.QuotaLeft(zeroPool, pooled));

        var legacy = Parse("""{"maxUsedPercent":0,"warningUsedPercent":null}""");
        var legacyBefore = legacy.ToJsonString();
        Assert.True(JsonNode.DeepEquals(Parse("""{"minRemainingPercent":100,"warningRemainingPercent":null,"fallbackModelId":null,"alwaysApprove":false}"""), ModelPolicy.ValidatePolicy(legacy)));
        Assert.Equal(legacyBefore, legacy.ToJsonString());
        Assert.True(JsonNode.DeepEquals(
            Parse("""{"minRemainingPercent":0,"warningRemainingPercent":100,"fallbackModelId":null,"alwaysApprove":false}"""),
            ModelPolicy.ValidatePolicy(Parse("""{"maxUsedPercent":100,"warningUsedPercent":0}"""))));
        Assert.Equal("Warning remaining quota must be higher than the stop threshold.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.ValidatePolicy(Parse("""{"warningUsedPercent":70}"""))).Message);
        Assert.Equal("  id  ", ModelPolicy.ValidatePolicy(Parse("""{"fallbackModelId":"  id  "}"""))["fallbackModelId"]!.GetValue<string>());

        var tied = Arr("""[{"id":"a","plan":"pa","enabled":true,"provider":"A"},{"id":"b","plan":"pb","enabled":true,"provider":"B"},{"id":"c","plan":"pc","enabled":true,"provider":"C"}]""");
        var tiedPlans = Arr("""[{"id":"pa","readable":true,"leftPct":10},{"id":"pb","readable":true,"leftPct":40},{"id":"pc","readable":true,"leftPct":40}]""");
        Assert.Equal("b", ModelPolicy.QuotaSwitches(Arr("""[{"subTaskId":"s","modelId":"a"}]"""), tied, tiedPlans)[0]!["toModelId"]!.GetValue<string>());

        var nullCompany = Arr("""[{"id":"a","plan":"pa","enabled":true,"provider":null},{"id":"b","plan":"pb","enabled":true,"provider":"B"},{"id":"c","plan":"pc","enabled":true,"provider":null}]""");
        var nullCompanyPlans = Arr("""[{"id":"pa","readable":true,"leftPct":10},{"id":"pb","readable":true,"leftPct":40},{"id":"pc","readable":true,"leftPct":90}]""");
        Assert.Equal("b", ModelPolicy.QuotaSwitches(Arr("""[{"subTaskId":"s","modelId":"a","avoidProviders":[null]}]"""), nullCompany, nullCompanyPlans)[0]!["toModelId"]!.GetValue<string>());

        var exhaustedSwitch = ModelPolicy.QuotaSwitches(
            Arr("""[{"subTaskId":"s","modelId":"a"}]"""),
            Arr("""[{"id":"a","plan":"pa","enabled":true},{"id":"b","plan":"pb","enabled":true}]"""),
            Arr("""[{"id":"pa","readable":true,"exhausted":true},{"id":"pb","readable":true,"leftPct":80}]"""));
        Assert.True(JsonNode.DeepEquals(Arr("""[{"subTaskId":"s","fromModelId":"a","toModelId":"b","leftPercent":null}]"""), exhaustedSwitch));

        var beforeExhausted = new JsonArray { null, Parse("""{"id":"p","readable":true,"leftPct":50,"exhausted":true}""") };
        Assert.True(ModelPolicy.LowQuota(Parse("""{"id":"x","plan":"p","enabled":true}"""), beforeExhausted, 20));
        var nullThenReadable = new JsonArray { null, Parse("""{"id":"p","readable":true,"leftPct":50}""") };
        Assert.Equal("Invalid quota plan.", Assert.Throws<InvalidOperationException>(() => ModelPolicy.UsagePolicy(Parse("""{"id":"x","plan":"p","enabled":true,"policy":{"minRemainingPercent":1}}"""), nullThenReadable)).Message);

        Assert.Empty(ModelPolicy.QuotaSwitches(
            Arr("""[{"subTaskId":"s","modelId":"sonnet","avoidProviders":"not-an-array"}]"""),
            SwitchModels(), SwitchPlans()));

        var self = Copy(Lead);
        self["policy"] = Parse("""{"minRemainingPercent":20,"fallbackModelId":"lead"}""");
        Assert.Null(ModelPolicy.ResolveUsageFallback(self, Models(self), Arr("""[{"id":"ChatGPT","readable":true,"leftPct":0}]"""), _ => true));

        var automatic = Copy(Lead);
        automatic.Remove("provider");
        automatic["plan"] = "P";
        automatic["policy"] = Parse("""{"minRemainingPercent":20}""");
        automatic["fallbackProviderId"] = "@automatic";
        var nullProvider = Parse("""{"id":"other","provider":null,"plan":"Q","enabled":true}""");
        Assert.Equal("other", Id(ModelPolicy.ResolveUsageFallback(automatic, Models(automatic, nullProvider), Arr("""[{"id":"P","readable":true,"leftPct":0},{"id":"Q","readable":true,"leftPct":90}]"""), _ => true)));
        var disabled = Parse("""{"id":"z","provider":"other","plan":"Grok","enabled":0}""");
        Assert.Null(ModelPolicy.ResolveUsageFallback(automatic, Models(automatic, disabled), Arr("""[{"id":"P","readable":true,"leftPct":0},{"id":"Grok","readable":true,"leftPct":90}]"""), _ => true));

        var at = DateTimeOffset.Parse("2026-11-11T16:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var unavailable = Parse($$"""{"available":false,"unavailableAt":"{{stamp}}"}""");
        var before = unavailable.ToJsonString();
        Assert.False(ModelPolicy.AvailableAgain(unavailable, at - 1));
        Assert.True(ModelPolicy.AvailableAgain(unavailable, at, 0));
        Assert.Equal(before, unavailable.ToJsonString());
        Assert.False(ModelPolicy.AvailableAgain(Parse("""{"available":false,"unavailableAt":""}"""), at + ModelPolicy.RetryUnavailableMs));

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 40, _ =>
        {
            try
            {
                var policy = ModelPolicy.ValidatePolicy(Parse("""{"maxUsedPercent":80,"warningUsedPercent":70}"""));
                Assert.Equal(20, policy["minRemainingPercent"]!.GetValue<int>());
                Assert.Equal(30, policy["warningRemainingPercent"]!.GetValue<int>());
                var retry = Parse($$"""{"available":false,"unavailableAt":"{{stamp}}"}""");
                Assert.False(ModelPolicy.AvailableAgain(retry, at + ModelPolicy.RetryUnavailableMs - 1));
                Assert.True(ModelPolicy.AvailableAgain(retry, at + ModelPolicy.RetryUnavailableMs));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        });
        Assert.Empty(failures);
    }

    static JsonObject WithPolicy(JsonObject model, string policy)
    {
        var copy = Copy(model);
        copy["policy"] = Parse(policy);
        return copy;
    }

    static string State(JsonObject model, JsonArray plans) => ModelPolicy.UsagePolicy(model, plans)["state"]!.GetValue<string>();
    static string? Id(JsonObject? model) => model?["id"]?.GetValue<string>();
    static JsonArray Models(params JsonObject[] models)
    {
        var array = new JsonArray();
        foreach (var model in models) array.Add(model.DeepClone());
        return array;
    }
    static JsonObject CopyWithId(JsonObject model, string id)
    {
        var copy = Copy(model);
        copy["id"] = id;
        return copy;
    }
    static JsonArray WithoutLast(JsonArray values)
    {
        var copy = new JsonArray();
        for (var i = 0; i < values.Count - 1; i++) copy.Add(values[i]?.DeepClone());
        return copy;
    }
    static JsonArray SwitchPlans() => Arr("""[{"id":"chatgpt","readable":true,"leftPct":12},{"id":"claude","readable":true,"leftPct":60},{"id":"gemini","readable":true,"leftPct":98},{"id":"grok","readable":false}]""");
    static JsonArray SwitchModels() => Arr("""[{"id":"terra","label":"terra","plan":"chatgpt","enabled":true,"available":true},{"id":"sonnet","label":"sonnet","plan":"claude","enabled":true,"available":true},{"id":"gemini-pro","label":"gemini-pro","plan":"gemini","enabled":true,"available":true},{"id":"grok","label":"grok","plan":"grok","enabled":true,"available":true}]""");
    static bool Blocked(JsonObject model, JsonArray plans) => ModelPolicy.UsagePolicy(model, plans)["blocked"]!.GetValue<bool>();
    static double Number(JsonNode? node) => double.Parse(node!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
}
